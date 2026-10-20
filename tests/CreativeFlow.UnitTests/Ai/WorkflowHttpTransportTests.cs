using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Features.Workflow;
using CreativeFlow.Infrastructure.Ai.HttpRequests;
using CreativeFlow.Infrastructure.Options;
using CreativeFlow.Infrastructure.Security;
using CreativeFlow.UnitTests.Support;

namespace CreativeFlow.UnitTests.Ai;

/// <summary>使用真实 Kestrel 验证 HTTP 方法、模板、响应类型和网络边界。</summary>
public sealed class WorkflowHttpTransportTests
{
    /// <summary>当前测试取消信号。</summary>
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>实际响应达到 1 MiB 仍可传到结束节点并恢复；多字节字符按 UTF-8 字节而非字符数计量。</summary>
    [Theory]
    [InlineData(false, false)][InlineData(true, false)][InlineData(false, true)][InlineData(true, true)]
    public async Task OneMiBResponseSurvivesTypedOutputAndRestore(bool lengthHeader, bool unicode)
    {
        var body = unicode ? string.Concat(Enumerable.Repeat("中", 349525)) + "x" : new string('x', 1048576);
        Assert.Equal(1048576, Encoding.UTF8.GetByteCount(body));
        await using var host = await Host.Create(async context => { if (lengthHeader) context.Response.ContentLength = 1048576; await context.Response.WriteAsync(body, Ct); });
        var data = HttpRequestFixtures.Data(host.Endpoint); data["http"]!["responseLimitBytes"] = 1048576;
        var result = await host.Transport.SendAsync(data, new JsonObject { ["question"] = "test" }, null, Ct);
        Assert.Equal(body, result["body"]!.GetValue<string>());
        var graph = HttpRequestFixtures.Graph(); graph["nodes"]![1]!["data"] = data;
        var plan = WorkflowV2GraphValidator.Validate(graph).Plan!;
        Assert.True(WorkflowV2Values.TryRestore(plan.Nodes.Single(n => n.Type == "httpRequest"), WorkflowV2Values.Output(result).ToJsonString(), out _));
        Assert.True(WorkflowV2Values.TryRestore(plan.Nodes.Single(n => n.Type == "end"), WorkflowV2Values.Output(new JsonObject { ["answer"] = body }).ToJsonString(), out _));
    }

    /// <summary>已声明和分块响应超过 1 MiB 一个字节均失败，不能静默截断或发起第二次请求。</summary>
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task OneByteOverOneMiBIsRejected(bool lengthHeader)
    {
        await using var host = await Host.Create(async context => { if (lengthHeader) context.Response.ContentLength = 1048577; await context.Response.WriteAsync(new string('x', 1048577), Ct); });
        var data = HttpRequestFixtures.Data(host.Endpoint); data["http"]!["responseLimitBytes"] = 1048576;
        var error = await Assert.ThrowsAsync<WorkflowHttpException>(() => host.Transport.SendAsync(data, new JsonObject { ["question"] = "test" }, null, Ct));
        Assert.Equal("HTTP_RESPONSE_TOO_LARGE", error.Code); Assert.True(error.MayHaveExecuted); Assert.Equal(1, host.Calls);
    }

    /// <summary>路径和查询安全编码；JSON 整值变量保留数字和对象，字符串不会注入新字段。</summary>
    [Theory]
    [InlineData("POST", "json")][InlineData("PUT", "text")][InlineData("PATCH", "form")]
    [InlineData("DELETE", "none")][InlineData("GET", "none")][InlineData("OPTIONS", "none")][InlineData("HEAD", "none")]
    public async Task MethodsTemplatesAndBodyModesReachTheServerOnce(string method, string mode)
    {
        // 在真实服务端断言解码后的值，确保客户端转义与类型保留不是仅对内部实现的断言。
        const string question = "查询 & injected=true ? #\"\\";
        JsonNode? json = null; string? receivedBody = null; string? form = null;
        await using var host = await Host.Create(async context =>
        {
            Assert.Equal(method, context.Request.Method);
            Assert.Equal(question, context.Request.Query["q"].ToString()); Assert.False(context.Request.Query.ContainsKey("injected"));
            Assert.Equal("fixture", context.Request.Headers["X-Test"].ToString());
            if (mode == "json") json = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: Ct);
            if (mode == "text") { using var reader = new StreamReader(context.Request.Body); receivedBody = await reader.ReadToEndAsync(Ct); }
            if (mode == "form") form = (await context.Request.ReadFormAsync(Ct))["q"];
            context.Response.Headers["X-Result"] = "ok";
            await context.Response.WriteAsync("{\"data\":{\"count\":7}}", Ct);
        });
        var data = HttpRequestFixtures.Data(host.Endpoint); var http = data["http"]!;
        http["method"] = method; http["bodyType"] = mode;
        http["body"] = mode == "json" ? """{"question":"{{question}}","count":"{{count}}","payload":"{{payload}}","label":"prefix {{question}}"}""" : "{{question}}";
        http["headers"] = JsonNode.Parse("""[{"name":"X-Test","value":"fixture"}]""");
        http["form"] = JsonNode.Parse("""[{"name":"q","value":"{{question}}"}]""");
        data["fields"]!.AsArray().Add(JsonNode.Parse("""{"key":"count","type":"number","required":true}"""));
        data["fields"]!.AsArray().Add(JsonNode.Parse("""{"key":"payload","type":"object","required":true}"""));
        if (method != "HEAD") { data["outputFields"] = JsonNode.Parse("""[{"key":"count","type":"number","required":true}]"""); data["extract"]!["count"] = "/data/count"; }
        var inputs = new JsonObject { ["question"] = question, ["count"] = 5, ["payload"] = new JsonObject { ["active"] = true } };
        var output = await host.Transport.SendAsync(data, inputs, null, Ct);
        Assert.Equal(200, output["statusCode"]!.GetValue<int>()); Assert.Equal("ok", output["headers"]!["x-result"]!.GetValue<string>()); Assert.Equal(1, host.Calls);
        if (method == "HEAD") Assert.Equal("", output["body"]!.GetValue<string>()); else Assert.Equal(7, output["count"]!.GetValue<int>());
        if (mode == "json") { Assert.Equal(question, json!["question"]!.GetValue<string>()); Assert.Equal(5, json["count"]!.GetValue<int>()); Assert.True(json["payload"]!["active"]!.GetValue<bool>()); Assert.Equal("prefix " + question, json["label"]!.GetValue<string>()); }
        if (mode == "text") Assert.Equal(question, receivedBody);
        if (mode == "form") Assert.Equal(question, form);
        var uri = HttpRequestDefinition.Url(HttpRequestFixtures.Data(host.Endpoint + "/{{question}}")["http"]!.AsObject(), inputs);
        Assert.Equal(new Uri(host.Endpoint).Authority, uri.Authority); Assert.Contains("%26", uri.AbsolutePath);
    }

    /// <summary>凭据仅发送到绑定站点，回显的字段名、数组、正文和响应头都被脱敏。</summary>
    [Fact]
    public async Task CredentialsStayAtTheirOriginAndAreRedactedBeforeOutputs()
    {
        const string secret = "synthetic-http-secret";
        await using var host = await Host.Create(context =>
        {
            Assert.Equal("Bearer " + secret, context.Request.Headers.Authorization.ToString());
            context.Response.Headers["X-Echo"] = secret; context.Response.Headers.SetCookie = "secret=" + secret;
            return context.Response.WriteAsync(new JsonObject { ["data"] = new JsonObject { [secret] = new JsonArray("Bearer " + secret) } }.ToJsonString(), Ct);
        });
        var data = HttpRequestFixtures.Data(host.Endpoint);
        data["outputFields"] = JsonNode.Parse("""[{"key":"data","type":"object","required":true}]"""); data["extract"]!["data"] = "/data";
        var revision = host.Revision(new JsonObject { ["Authorization"] = "Bearer " + secret });
        var output = await host.Transport.SendAsync(data, new JsonObject { ["question"] = "synthetic" }, revision, Ct);
        Assert.DoesNotContain(secret, output.ToJsonString()); Assert.Equal("[REDACTED]", output["headers"]!["x-echo"]!.GetValue<string>());
        Assert.False(output["headers"]!.AsObject().ContainsKey("set-cookie")); Assert.Equal("[REDACTED]", output["data"]!["[REDACTED]"]![0]!.GetValue<string>());
        var wrong = revision with { DefinitionJson = """{"origin":"https://elsewhere.example","headers":["Authorization"]}""" };
        var error = await Assert.ThrowsAsync<WorkflowHttpException>(() => host.Transport.SendAsync(data, new JsonObject { ["question"] = "synthetic" }, wrong, Ct));
        Assert.Equal("HTTP_CREDENTIAL_SCOPE", error.Code); Assert.False(error.MayHaveExecuted); Assert.Equal(1, host.Calls);
    }

    /// <summary>含中文与 HTML 标记的 JSON 正文仍是普通文本，脱敏后不变为双重 Unicode 转义。</summary>
    [Fact]
    public async Task JsonBodyPreservesReadableUnicodeAndSafeRedaction()
    {
        await using var host = await Host.Create(context => context.Response.WriteAsync("""{"message":"纺织客户 <script>text</script>","secret":"synthetic-secret"}""", Ct));
        var result = await host.Transport.SendAsync(HttpRequestFixtures.Data(host.Endpoint), new JsonObject { ["question"] = "test" }, host.Revision(new JsonObject { ["Authorization"] = "Bearer synthetic-secret" }), Ct);
        var body = result["body"]!.GetValue<string>(); Assert.Contains("纺织客户 <script>text</script>", body); Assert.DoesNotContain("synthetic-secret", body);
        Assert.Equal("[REDACTED]", JsonNode.Parse(body)!["secret"]!.GetValue<string>());
    }

    /// <summary>非成功状态可作为正常输出分支处理；两种模式都不会重试或跟随跳转。</summary>
    [Theory]
    [InlineData(302)][InlineData(401)][InlineData(429)][InlineData(500)]
    public async Task ErrorStatusesHaveExplicitHandlingAndNeverRetry(int status)
    {
        await using var host = await Host.Create(context => { context.Response.StatusCode = status; context.Response.Headers.Location = "/redirect"; return context.Response.WriteAsync("synthetic-error", Ct); });
        var data = HttpRequestFixtures.Data(host.Endpoint); var inputs = new JsonObject { ["question"] = "test" };
        var error = await Assert.ThrowsAsync<WorkflowHttpException>(() => host.Transport.SendAsync(data, inputs, null, Ct));
        Assert.Equal("HTTP_STATUS_ERROR", error.Code); Assert.DoesNotContain("synthetic-error", error.Message); Assert.True(error.MayHaveExecuted); Assert.Equal(1, host.Calls);
        data["http"]!["failOnHttpError"] = false;
        var output = await host.Transport.SendAsync(data, inputs, null, Ct);
        Assert.Equal(status, output["statusCode"]!.GetValue<int>()); Assert.Equal("synthetic-error", output["body"]!.GetValue<string>()); Assert.Equal(2, host.Calls);
    }

    /// <summary>超大流、错误编码、缺字段和错误 JSON 类型不会进入下游。</summary>
    [Theory]
    [InlineData("chunked", "HTTP_RESPONSE_TOO_LARGE")][InlineData("length", "HTTP_RESPONSE_TOO_LARGE")]
    [InlineData("encoding", "HTTP_RESPONSE_INVALID")][InlineData("missing", "HTTP_OUTPUT_MISSING")]
    [InlineData("type", "HTTP_OUTPUT_INVALID")][InlineData("json", "HTTP_RESPONSE_INVALID")]
    public async Task ResponsesRespectByteAndTypeBounds(string mode, string code)
    {
        await using var host = await Host.Create(async context =>
        {
            if (mode == "encoding") { await context.Response.Body.WriteAsync(new byte[] { 0xff, 0xfe }, Ct); return; }
            var body = mode is "chunked" or "length" ? new string('x', 5000) : mode == "missing" ? "{}" : mode == "type" ? "{\"count\":\"seven\"}" : "not-json";
            if (mode == "length") context.Response.ContentLength = Encoding.UTF8.GetByteCount(body);
            await context.Response.WriteAsync(body, Ct);
        });
        var data = HttpRequestFixtures.Data(host.Endpoint); data["outputFields"] = JsonNode.Parse("""[{"key":"count","type":"number","required":true}]"""); data["extract"]!["count"] = "/count";
        var error = await Assert.ThrowsAsync<WorkflowHttpException>(() => host.Transport.SendAsync(data, new JsonObject { ["question"] = "test" }, null, Ct));
        Assert.Equal(code, error.Code); Assert.True(error.MayHaveExecuted); Assert.Equal(1, host.Calls);
    }

    /// <summary>发送后超时不重试，已取消的请求不发送，生产策略拒绝回环和云元数据。</summary>
    [Fact]
    public async Task CancellationTimeoutAndNetworkDenialsDoNotDuplicateCalls()
    {
        await using var host = await Host.Create(async context => { try { await Task.Delay(20000, context.RequestAborted); } catch (OperationCanceledException) { } });
        var data = HttpRequestFixtures.Data(host.Endpoint); data["http"]!["timeoutSeconds"] = 1;
        var error = await Assert.ThrowsAsync<WorkflowHttpException>(() => host.Transport.SendAsync(data, new JsonObject { ["question"] = "test" }, null, Ct));
        Assert.Equal("HTTP_TIMEOUT", error.Code); Assert.True(error.MayHaveExecuted); Assert.Equal(1, host.Calls);
        using var canceled = new CancellationTokenSource(); await canceled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.Transport.SendAsync(data, new JsonObject { ["question"] = "test" }, null, canceled.Token));
        Assert.Equal(1, host.Calls);
        await Assert.ThrowsAsync<WorkflowHttpException>(() => new WorkflowHttpNetwork([host.Endpoint], false).ResolveAsync(new Uri(host.Endpoint), Ct));
        await Assert.ThrowsAsync<WorkflowHttpException>(() => new WorkflowHttpNetwork(["http://169.254.169.254/latest"], true).ResolveAsync(new Uri("http://169.254.169.254/latest"), Ct));
        await Assert.ThrowsAsync<WorkflowHttpException>(() => host.Transport.ValidateEndpointAsync(host.Endpoint + "/other", Ct));
    }

    /// <summary>头注入、未知变量和重名凭据都必须在网络请求之前失败。</summary>
    [Fact]
    public async Task InvalidExpandedInputsNeverReachTheServer()
    {
        await using var host = await Host.Create(context => context.Response.WriteAsync("ok", Ct));
        var data = HttpRequestFixtures.Data(host.Endpoint); data["http"]!["headers"] = JsonNode.Parse("""[{"name":"X-Test","value":"{{question}}"}]""");
        var inputs = new JsonObject { ["question"] = "first\r\nX-Injected: yes" };
        var error = await Assert.ThrowsAsync<WorkflowHttpException>(() => host.Transport.SendAsync(data, inputs, null, Ct));
        Assert.False(error.MayHaveExecuted);
        inputs["question"] = "safe";
        await Assert.ThrowsAsync<WorkflowHttpException>(() => host.Transport.SendAsync(data, inputs, host.Revision(new JsonObject { ["X-Test"] = "credential" }), Ct));
        data["http"]!["url"] = "https://{{question}}/query";
        Assert.NotNull(HttpRequestDefinition.Validate(data)); Assert.Equal(0, host.Calls);
    }

    /// <summary>每条测试独立启动回环 HTTP 端点并使用真实固定 IP 传输。</summary>
    private sealed class Host : IHttpClientFactory, IAsyncDisposable
    {
        /// <summary>独立服务。</summary>
        private WebApplication app = null!;
        /// <summary>精确白名单策略。</summary>
        private WorkflowHttpNetwork network = null!;
        /// <summary>真实请求数量。</summary>
        public int Calls { get; private set; }
        /// <summary>随机端口的测试地址。</summary>
        public string Endpoint { get; private set; } = "";
        /// <summary>测试专用随机加密密钥。</summary>
        private readonly AesGcmApiKeyProtector protector = new(new AiOptions { EncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) });
        /// <summary>真实传输。</summary>
        public WorkflowHttpTransport Transport => new(this, network, protector);
        /// <summary>启动独立回环宿主。</summary>
        public static async Task<Host> Create(RequestDelegate handler)
        {
            var host = new Host(); var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
            host.app = builder.Build(); host.app.Run(context => { host.Calls++; return handler(context); });
            await host.app.StartAsync(Ct); host.Endpoint = host.app.Urls.Single() + "/query"; host.network = new([host.Endpoint], true); return host;
        }
        /// <summary>只保存在内存中的合成凭据版本。</summary>
        public HttpCredentialRevision Revision(JsonObject headers) => new(Guid.NewGuid(), Guid.NewGuid(), new JsonObject { ["origin"] = new Uri(Endpoint).GetLeftPart(UriPartial.Authority), ["headers"] = new JsonArray(headers.Select(pair => (JsonNode?)JsonValue.Create(pair.Key)).ToArray()),
            ["requests"] = new JsonArray(new JsonObject { ["method"] = "GET", ["url"] = Endpoint }) }.ToJsonString(), "synthetic", protector.Protect(headers.ToJsonString()));
        /// <summary>关闭重定向、代理和 Cookie，并绑定已校验 IP。</summary>
        public HttpClient CreateClient(string name) => new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false, ConnectCallback = network.ConnectAsync }) { Timeout = Timeout.InfiniteTimeSpan };
        /// <summary>释放当前测试端口。</summary>
        public async ValueTask DisposeAsync() { await app.StopAsync(CancellationToken.None); await app.DisposeAsync(); }
    }
}
