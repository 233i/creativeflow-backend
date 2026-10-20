using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Security;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Features.Workflow;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;
using CreativeFlow.Infrastructure.Ai;
using CreativeFlow.Infrastructure;
using CreativeFlow.Infrastructure.Ai.Embeddings;
using CreativeFlow.Infrastructure.Options;
using CreativeFlow.UnitTests.Support;

namespace CreativeFlow.UnitTests.Ai;

/// <summary>验证新版模型快照、实际聊天适配器、独立端点许可与无重试策略。</summary>
public sealed class WorkflowV2ModelTests
{
    [Theory]
    [InlineData(ProviderType.OpenAiChat)][InlineData(ProviderType.OpenAiResponses)]
    [InlineData(ProviderType.Anthropic)][InlineData(ProviderType.Gemini)]
    public async Task StreamingUsesProductionModelServiceGuardAndReceipt(ProviderType protocol)
    {
        using var fixture = new Fixture();
        var model = fixture.Providers.Provider.Models[0] with { Type = protocol };
        fixture.Providers.Provider = fixture.Providers.Provider with { Type = protocol, BaseUrl = "http://127.0.0.1:1234", Models = [model] };
        var path = protocol switch { ProviderType.OpenAiChat => "/chat/completions", ProviderType.OpenAiResponses => "/responses", ProviderType.Anthropic => "/v1/messages", _ => "/v1beta/models/local-chat:generateContent" };
        fixture.Network = new(true, new EmbeddingEndpointPolicy(new EmbeddingOptions { AllowedEndpoints = ["http://127.0.0.1:1234" + path,
            "http://127.0.0.1:1234/v1beta/models/local-chat:streamGenerateContent?alt=sse"] }, true, allowGeminiSseQuery: true));
        var frames = TextStreamProtocolTests.Frames(protocol);
        fixture.Transport.OnSend = _ => { var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(frames.First + frames.Last) }; response.Content.Headers.ContentType = new("text/event-stream"); return Task.FromResult(response); };
        var saved = new MemoryEvents(); var stream = new WorkflowPublicTextStream(saved);
        var now = DateTimeOffset.UtcNow;
        stream.Configure(new WorkflowRun(WorkflowRunId.New(), WorkflowId.New(), DeviceId.New(), "{}", null, WorkflowRunStatus.Running, null, now, now, now, null)
        { TriggerType = WorkflowRunTriggerType.Api, InputJson = "{\"stream\":true}" }, fixture.Plan);
        var service = new WorkflowV2Models(fixture.Providers, new Protector(), fixture.Network, fixture, fixture.Trace,
            new TextGenerationDispatcher([TextStreamProtocolTests.Client(protocol, fixture)]), stream);
        var snapshot = await service.CaptureAsync(fixture.Plan, TestContext.Current.CancellationToken);
        var output = await service.GenerateAsync(fixture.Node, snapshot[fixture.Node.Id]!.AsObject(), "test", TestContext.Current.CancellationToken);
        Assert.Equal("合成回答", output); Assert.Equal(output, string.Concat(saved.Text));
        Assert.Equal(1, fixture.Transport.Calls); Assert.Equal(200, fixture.Trace.Current!["httpStatus"]!.GetValue<int>());
        Assert.NotNull(fixture.Trace.Current["usage"]); Assert.NotNull(fixture.Trace.Current["finishReason"]);
    }

    [Theory]
    [InlineData("?alt=sse&key=secret")][InlineData("?alt=json")][InlineData("?key=secret")]
    public void StreamingQueryExceptionCannotOpenArbitraryQueryOrEmbeddingEndpoints(string query)
    {
        var baseUrl = "https://example.test/v1beta/models/test:streamGenerateContent";
        var options = new EmbeddingOptions { AllowedEndpoints = [baseUrl + "?alt=sse", baseUrl + query] };
        var workflow = new EmbeddingEndpointPolicy(options, false, allowGeminiSseQuery: true);
        workflow.ValidateEndpoint(new(baseUrl + "?alt=sse"));
        Assert.Throws<InvalidOperationException>(() => workflow.ValidateEndpoint(new(baseUrl + query)));
        Assert.Throws<InvalidOperationException>(() => new EmbeddingEndpointPolicy(options, false).ValidateEndpoint(new(baseUrl + "?alt=sse")));
    }

    private sealed class MemoryEvents : IWorkflowRunEventRepository
    {
        public List<string> Text { get; } = [];
        public Task AppendAsync(WorkflowRunId id, string data, CancellationToken ct) { Text.Add(JsonNode.Parse(data)!["delta"]!.GetValue<string>()); return Task.CompletedTask; }
        public Task<IReadOnlyList<WorkflowRunTextEvent>> ReadAsync(WorkflowRunId id, long after, CancellationToken ct) => throw new NotSupportedException();
        public Task<long> LastSequenceAsync(WorkflowRunId id, CancellationToken ct) => throw new NotSupportedException();
    }

    /// <summary>复用生产聊天协议，变量、模型和参数原样发送，本地无密钥不会添加空授权头。</summary>
    [Theory]
    [InlineData(512)][InlineData(32768)][InlineData(131072)]
    public async Task SendsOneBoundedOpenAiChatRequestWithoutLeakingSecretsIntoSnapshot(int tokens)
    {
        using var fixture = new Fixture();
        fixture.Node.Data["maxOutputTokens"] = tokens;
        var snapshots = await fixture.Service.CaptureAsync(fixture.Plan, TestContext.Current.CancellationToken);
        var result = await fixture.Service.GenerateAsync(fixture.Node, snapshots["llm"]!.AsObject(), "a question", TestContext.Current.CancellationToken);
        Assert.Equal("test answer", result); Assert.Equal(1, fixture.Transport.Calls);
        Assert.Null(fixture.Transport.Authorization);
        var body = JsonNode.Parse(fixture.Transport.Payload!)!;
        Assert.Equal("local-chat", body["model"]!.GetValue<string>());
        Assert.Equal(tokens, body["max_tokens"]!.GetValue<int>());
        Assert.Equal(tokens, fixture.Trace.Current!["maxOutputTokens"]!.GetValue<int>());
        Assert.Equal(0.7, body["temperature"]!.GetValue<double>());
        Assert.Equal("a question", body["messages"]![1]!["content"]!.GetValue<string>());
        Assert.DoesNotContain("apiKey", snapshots.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", snapshots.ToJsonString(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>真实聊天适配器不按普通文本预算拦截知识问答的最长 JSON，普通 LLM 仍保持原限制。</summary>
    [Theory]
    [InlineData("knowledgeAnswer", true)][InlineData("llm", false)]
    public async Task KnowledgeAnswerHasItsOwnBoundedResponseContract(string type, bool accepted)
    {
        using var fixture = new Fixture();
        var node = fixture.Node with { Type = type };
        var plan = new WorkflowPlan([node]);
        var snapshot = (await fixture.Service.CaptureAsync(plan, TestContext.Current.CancellationToken))[node.Id]!.AsObject();
        var response = new JsonObject { ["text"] = new string('文', 8000),
            ["citations"] = new JsonArray(Enumerable.Range(0, 32).Select(_ => (JsonNode?)JsonValue.Create(Guid.NewGuid().ToString())).ToArray()) }.ToJsonString();
        fixture.Transport.Body = new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["content"] = response } }) }.ToJsonString();
        var prompt = new string('源', 32 * 1600);
        if (accepted)
        {
            var result = await fixture.Service.GenerateAsync(node, snapshot, prompt, TestContext.Current.CancellationToken);
            Assert.Equal(response, result);
            Assert.Equal(prompt, JsonNode.Parse(fixture.Transport.Payload!)!["messages"]![1]!["content"]!.GetValue<string>());
        }
        else await Assert.ThrowsAsync<WorkflowV2Exception>(() => fixture.Service.GenerateAsync(node, snapshot, prompt, TestContext.Current.CancellationToken));
        Assert.Equal(1, fixture.Transport.Calls);
    }

    /// <summary>图片字段只接受 assetId；视觉模型能力在提交快照阶段校验，避免排队后才发现配置错误。</summary>
    [Fact]
    public async Task RequiresVisionForImageInputAndPreservesAssetShape()
    {
        using var fixture = new Fixture();
        var llm = fixture.Node;
        ((JsonArray)llm.Data["fields"]!).Add(new JsonObject { ["key"] = "image", ["type"] = "image", ["required"] = false });
        await Assert.ThrowsAsync<WorkflowV2Exception>(() => fixture.Service.CaptureAsync(fixture.Plan, TestContext.Current.CancellationToken));
        var field = (JsonObject)((JsonArray)llm.Data["fields"]!)[1]!;
        Assert.False(WorkflowV2Values.Matches(field, JsonValue.Create("https://example.test/image.png")));
        Assert.True(WorkflowV2Values.Matches(field, JsonNode.Parse("{\"assetId\":\"00000000-0000-0000-0000-000000000001\"}")));
    }

    /// <summary>工作流按后台模型的协议分派，并始终指定受限的工作流 HTTP 客户端。</summary>
    [Theory]
    [InlineData(ProviderType.OpenAiChat)]
    [InlineData(ProviderType.OpenAiResponses)]
    [InlineData(ProviderType.Anthropic)]
    [InlineData(ProviderType.Gemini)]
    public async Task DispatchesConfiguredTextProtocolThroughWorkflowClient(ProviderType protocol)
    {
        using var fixture = new Fixture();
        fixture.Node.Data["maxOutputTokens"] = 32768;
        var model = fixture.Providers.Provider.Models[0] with { Type = protocol };
        var baseUrl = protocol is ProviderType.Anthropic or ProviderType.Gemini
            ? "http://127.0.0.1:1234"
            : "http://127.0.0.1:1234/v1";
        var endpoint = protocol switch
        {
            ProviderType.OpenAiChat => $"{baseUrl}/chat/completions",
            ProviderType.OpenAiResponses => $"{baseUrl}/responses",
            ProviderType.Anthropic => $"{baseUrl}/v1/messages",
            ProviderType.Gemini => $"{baseUrl}/v1beta/models/local-chat:generateContent",
            _ => throw new ArgumentOutOfRangeException(nameof(protocol)),
        };
        fixture.Providers.Provider = fixture.Providers.Provider with
        {
            Type = protocol,
            BaseUrl = baseUrl,
            Models = [model],
        };
        fixture.Network = Network([endpoint]);

        var snapshots = await fixture.ServiceWithDispatcher.CaptureAsync(fixture.Plan, TestContext.Current.CancellationToken);
        Assert.Equal(protocol.ToWire(), snapshots["llm"]!["protocol"]!.GetValue<string>());
        var output = await fixture.ServiceWithDispatcher.GenerateAsync(
            fixture.Node, snapshots["llm"]!.AsObject(), "text", TestContext.Current.CancellationToken);

        Assert.Equal("dispatcher answer", output);
        Assert.Equal(protocol, fixture.Dispatcher.Protocol);
        Assert.Equal("workflow-model", fixture.Dispatcher.Request.HttpClientName);
        Assert.Equal(32768, fixture.Dispatcher.Request.MaxOutputTokens);
    }

    /// <summary>模型显式配置的上限继续在提交和执行前生效，不允许绕过或自动降低请求预算。</summary>
    [Fact]
    public async Task ConfiguredModelLimitIsEnforcedBeforeAnyRequest()
    {
        using var fixture = new Fixture();
        fixture.Providers.Provider = fixture.Providers.Provider with { Models = [fixture.Providers.Provider.Models[0] with { MaxOutputTokens = 16384 }] };
        fixture.Node.Data["maxOutputTokens"] = 16384;
        var snapshots = await fixture.Service.CaptureAsync(fixture.Plan, TestContext.Current.CancellationToken);
        fixture.Node.Data["maxOutputTokens"] = 32768;
        var error = await Assert.ThrowsAsync<WorkflowV2Exception>(() => fixture.Service.CaptureAsync(fixture.Plan, TestContext.Current.CancellationToken));
        Assert.Contains("16384", error.Message);
        await Assert.ThrowsAsync<WorkflowV2Exception>(() => fixture.Service.GenerateAsync(fixture.Node, snapshots["llm"]!.AsObject(), "test", TestContext.Current.CancellationToken));
        Assert.Equal(0, fixture.Transport.Calls);
    }

    /// <summary>HTTP 200 的空正文仍是已收到响应，截断原因、真实用量和请求标识均保留，不自动重试。</summary>
    [Theory]
    [InlineData("length")][InlineData("stop")]
    public async Task EmptySuccessfulResponseKeepsHttpStatusAndExplainsTruncation(string finishReason)
    {
        using var fixture = new Fixture();
        fixture.Transport.Body = $$$"""{"id":"upstream-budget","choices":[{"message":{"content":"","reasoning_content":"synthetic private reasoning"},"finish_reason":"{{{finishReason}}}"}],"usage":{"prompt_tokens":7191,"completion_tokens":4096,"total_tokens":11287}}""";
        var snapshot = (await fixture.Service.CaptureAsync(fixture.Plan, TestContext.Current.CancellationToken))["llm"]!.AsObject();
        var error = await Assert.ThrowsAsync<WorkflowV2Exception>(() => fixture.Service.GenerateAsync(fixture.Node, snapshot, "test", TestContext.Current.CancellationToken));
        Assert.Contains("HTTP 200", error.Message); Assert.DoesNotContain("HTTP 未知", error.Message);
        if (finishReason == "length") Assert.Contains("提高节点的最大输出 token", error.Message);
        Assert.Equal(1, fixture.Transport.Calls);
        var receipt = fixture.Trace.Current!;
        Assert.Equal("completed", receipt["status"]!.GetValue<string>()); Assert.Equal(200, receipt["httpStatus"]!.GetValue<int>());
        Assert.Equal(finishReason, receipt["finishReason"]!.GetValue<string>());
        Assert.Equal(4096, receipt["usage"]!["completion_tokens"]!.GetValue<long>());
        Assert.Equal("upstream-budget", receipt["upstreamRequestId"]!.GetValue<string>());
        Assert.DoesNotContain("private reasoning", receipt.ToJsonString());
    }

    /// <summary>模型禁用、协议或地址变更、白名单缺失、生产回环均在发送之前被阻止。</summary>
    [Theory]
    [InlineData("disabled")][InlineData("changed")][InlineData("protocol")]
    [InlineData("allowlist")][InlineData("production")][InlineData("off")]
    public async Task RevalidatesPermissionAndCapturedConfiguration(string mode)
    {
        using var fixture = new Fixture();
        var snapshot = (await fixture.Service.CaptureAsync(fixture.Plan, TestContext.Current.CancellationToken))["llm"]!.AsObject();
        if (mode == "disabled") fixture.Providers.Provider = fixture.Providers.Provider with { Enabled = false };
        if (mode == "changed") fixture.Providers.Provider = fixture.Providers.Provider with { BaseUrl = "http://127.0.0.1:1235/v1" };
        if (mode == "protocol") fixture.Providers.Provider = fixture.Providers.Provider with { Models = [fixture.Providers.Provider.Models[0] with { Type = ProviderType.OpenAiEmbeddings }] };
        if (mode == "allowlist") fixture.Network = Network([]);
        if (mode == "production") fixture.Network = Network([Fixture.Endpoint], development: false);
        if (mode == "off") fixture.Network = fixture.Network with { Enabled = false };
        await Assert.ThrowsAsync<CreativeFlow.Application.Abstractions.Ai.WorkflowV2Exception>(() => fixture.Service.GenerateAsync(fixture.Node, snapshot, "text", TestContext.Current.CancellationToken));
        Assert.Equal(0, fixture.Transport.Calls);
    }

    /// <summary>上游失败只调用一次，并把原始错误内容隔离在用户响应之外。</summary>
    [Fact]
    public async Task FailureDoesNotRetryOrExposeRawUpstreamBody()
    {
        using var fixture = new Fixture();
        var snapshot = (await fixture.Service.CaptureAsync(fixture.Plan, TestContext.Current.CancellationToken))["llm"]!.AsObject();
        fixture.Transport.Status = HttpStatusCode.InternalServerError;
        fixture.Transport.Body = "secret-upstream-detail";
        var error = await Assert.ThrowsAsync<CreativeFlow.Application.Abstractions.Ai.WorkflowV2Exception>(() => fixture.Service.GenerateAsync(fixture.Node, snapshot, "text", TestContext.Current.CancellationToken));
        Assert.Equal(1, fixture.Transport.Calls); Assert.DoesNotContain("secret-upstream-detail", error.Message);
    }

    /// <summary>入队快照允许未解析但显式许可的域名，不发送请求；未许可 URL 仍立即拒绝。</summary>
    [Fact]
    public async Task CaptureDoesNotResolveDnsOrWeakenExactEndpointPermission()
    {
        using var fixture = new Fixture();
        const string endpoint = "https://synthetic-workflow-model.invalid/v1/chat/completions";
        fixture.Providers.Provider = fixture.Providers.Provider with { BaseUrl = "https://synthetic-workflow-model.invalid/v1" };
        fixture.Network = Network([endpoint]);
        var snapshots = await fixture.Service.CaptureAsync(fixture.Plan, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Assert.Equal("https://synthetic-workflow-model.invalid/v1", snapshots["llm"]!["baseUrl"]!.GetValue<string>());
        fixture.Network = Network([]);
        await Assert.ThrowsAsync<CreativeFlow.Application.Abstractions.Ai.WorkflowV2Exception>(() =>
            fixture.Service.CaptureAsync(fixture.Plan, TestContext.Current.CancellationToken));
        Assert.Equal(0, fixture.Transport.Calls);
    }

    /// <summary>公网通配使四类后台聊天模型通过提交预检；捕获快照不请求模型，也不等待 DNS。</summary>
    [Theory]
    [InlineData(ProviderType.OpenAiChat)]
    [InlineData(ProviderType.OpenAiResponses)]
    [InlineData(ProviderType.Anthropic)]
    [InlineData(ProviderType.Gemini)]
    public async Task PublicHttpsWildcardAllowsConfiguredModelsAtSubmission(ProviderType protocol)
    {
        using var fixture = new Fixture();
        fixture.Providers.Provider = fixture.Providers.Provider with
        {
            BaseUrl = "https://synthetic-workflow-model.invalid/v1",
            Models = [fixture.Providers.Provider.Models[0] with { Type = protocol }],
        };
        fixture.Network = DependencyInjection.CreateWorkflowV2Network(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Workflow:Enabled"] = "true", ["Workflow:AllowedEndpoints:0"] = "https://*" }).Build(), false);
        var snapshots = await fixture.ServiceWithDispatcher.CaptureAsync(fixture.Plan, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Assert.Equal(protocol.ToWire(), snapshots["llm"]!["protocol"]!.GetValue<string>());
        Assert.Equal(0, fixture.Transport.Calls);
    }

    /// <summary>开发连接等待可以覆盖缓慢 DNS，但不能设为无限或超过一分钟，默认值不影响旧部署。</summary>
    [Theory]
    [InlineData(null, 10)][InlineData("1", 1)][InlineData("45", 45)][InlineData("60", 60)]
    public void ConnectionTimeoutIsBoundedAndRetainsDefault(string? value, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), DependencyInjection.WorkflowV2ConnectTimeout(value));

    /// <summary>错误或超界配置在注册时拒绝，不静默放开连接期限。</summary>
    [Theory]
    [InlineData("0")][InlineData("61")][InlineData("-1")][InlineData("invalid")]
    public void InvalidConnectionTimeoutIsRejected(string value) =>
        Assert.Throws<InvalidOperationException>(() => DependencyInjection.WorkflowV2ConnectTimeout(value));

    /// <summary>精确 URL 检查在连接池复用前执行，不允许相同主机上的未授权路径。</summary>
    [Fact]
    public async Task GuardRejectsWrongPathBeforeTransport()
    {
        using var fixture = new Fixture();
        using var client = fixture.CreateClient("ignored");
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("http://127.0.0.1:1234/v1/admin", TestContext.Current.CancellationToken));
        Assert.Equal(0, fixture.Transport.Calls);
    }

    /// <summary>原生请求只在显式设置时发送，并保存真实 usage、状态和请求标识；不虚构 token。</summary>
    [Fact]
    public async Task NativeResponseFormatAndActualUsageAreRecordedOnce()
    {
        using var fixture = new Fixture();
        fixture.Transport.Status = HttpStatusCode.Created;
        fixture.Node.Data["outputMode"] = "json"; fixture.Node.Data["outputTransport"] = "json_schema";
        fixture.Node.Data["outputFields"] = JsonNode.Parse("""[{"key":"result","type":"number","required":true}]""");
        fixture.Transport.Body = """{"id":"upstream-test","choices":[{"message":{"content":"{\"result\":2}"},"finish_reason":"stop"}],"usage":{"prompt_tokens":17,"completion_tokens":5,"total_tokens":22}}""";
        var receipts = new List<JsonObject>();
        fixture.Trace.Bind((record, _) => { receipts.Add((JsonObject)record.DeepClone()); return Task.CompletedTask; });
        var snapshot = (await fixture.Service.CaptureAsync(fixture.Plan, TestContext.Current.CancellationToken))["llm"]!.AsObject();
        var output = await fixture.Service.GenerateAsync(fixture.Node, snapshot, "test", TestContext.Current.CancellationToken);
        Assert.Equal("{\"result\":2}", output); Assert.Equal(1, fixture.Transport.Calls);
        Assert.True(JsonNode.Parse(fixture.Transport.Payload!)!["response_format"]!["json_schema"]!["strict"]!.GetValue<bool>());
        Assert.Equal(2, receipts.Count); Assert.Equal("requesting", receipts[0]["status"]!.GetValue<string>());
        Assert.Equal("completed", receipts[1]["status"]!.GetValue<string>()); Assert.Equal(22, receipts[1]["usage"]!["total_tokens"]!.GetValue<long>());
        Assert.Equal(201, receipts[1]["httpStatus"]!.GetValue<int>());
        Assert.Equal("upstream-test", receipts[1]["upstreamRequestId"]!.GetValue<string>()); Assert.DoesNotContain("apiKey", receipts[1].ToJsonString());
    }

    /// <summary>上游未报告用量时保持未知，原生格式失败不换成提示词模式重新发送。</summary>
    [Fact]
    public async Task FailureAndMissingUsageStayExplicitWithoutFallback()
    {
        using var fixture = new Fixture(); fixture.Transport.Status = HttpStatusCode.BadRequest;
        var snapshot = (await fixture.Service.CaptureAsync(fixture.Plan, TestContext.Current.CancellationToken))["llm"]!.AsObject();
        await Assert.ThrowsAsync<CreativeFlow.Application.Abstractions.Ai.WorkflowV2Exception>(() => fixture.Service.GenerateAsync(fixture.Node, snapshot, "test", TestContext.Current.CancellationToken));
        Assert.Equal(1, fixture.Transport.Calls); Assert.Equal("failed", fixture.Trace.Current!["status"]!.GetValue<string>());
        Assert.Null(fixture.Trace.Current!["usage"]); Assert.Equal(400, fixture.Trace.Current!["httpStatus"]!.GetValue<int>());
    }

    /// <summary>调用前持久化失败时停止，不把无法记账的请求继续发向上游。</summary>
    [Fact]
    public async Task ReceiptPersistenceFailurePreventsSending()
    {
        using var fixture = new Fixture(); fixture.Trace.Bind((_, _) => throw new InvalidOperationException("storage unavailable"));
        var snapshot = (await fixture.Service.CaptureAsync(fixture.Plan, TestContext.Current.CancellationToken))["llm"]!.AsObject();
        await Assert.ThrowsAsync<CreativeFlow.Application.Abstractions.Ai.WorkflowV2Exception>(() => fixture.Service.GenerateAsync(fixture.Node, snapshot, "test", TestContext.Current.CancellationToken));
        Assert.Equal(0, fixture.Transport.Calls);
    }

    /// <summary>仅为测试数字回环主机配置许可；不访问真实网络。</summary>
    private static WorkflowV2Network Network(string[] endpoints, bool development = true) =>
        new(true, new EmbeddingEndpointPolicy(new EmbeddingOptions { AllowedEndpoints = endpoints }, development));

    /// <summary>保留生产适配器和守卫，仅替换网络传输与模型仓储。</summary>
    private sealed class Fixture : IHttpClientFactory, IDisposable
    {
        /// <summary>精确许可的合成聊天端点。</summary>
        public const string Endpoint = "http://127.0.0.1:1234/v1/chat/completions";
        /// <summary>可变化的测试网络策略。</summary>
        public WorkflowV2Network Network { get; set; } = WorkflowV2ModelTests.Network([Endpoint]);
        /// <summary>可变化的模型记录。</summary>
        public EmbeddingFixtures.Providers Providers { get; } = new();
        /// <summary>不会发送网络请求的记录传输层。</summary>
        public EmbeddingFixtures.Transport Transport { get; } = new() { Body = """{"choices":[{"message":{"content":"test answer"}}]}""" };
        /// <summary>当前测试的已验证节点计划。</summary>
        public WorkflowPlan Plan { get; }
        /// <summary>已编译的 LLM 节点。</summary>
        public WorkflowPlanNode Node => Plan.Nodes.Single(node => node.Type == "llm");
        /// <summary>按当前网络策略创建生产服务，避免保留旧策略影响权限测试。</summary>
        public WorkflowV2Models Service => new(Providers, new Protector(), Network, this, Trace);
        /// <summary>注入文本分派器，验证工作流不会固定使用 OpenAI Chat 适配器。</summary>
        public WorkflowV2Models ServiceWithDispatcher => new(Providers, new Protector(), Network, this, Trace, Dispatcher);
        /// <summary>记录工作流选择的协议和命名 HTTP 客户端。</summary>
        public RecordingDispatcher Dispatcher { get; } = new();
        /// <summary>每次调用的实际记录，默认不触碰数据库。</summary>
        public WorkflowModelCallTrace Trace { get; } = new();

        /// <summary>把合成向量仓储改为显式聊天模型，并同步图内模型 ID。</summary>
        public Fixture()
        {
            var model = Providers.Provider.Models[0] with { Category = ModelCategory.Chat, Type = ProviderType.OpenAiChat, ModelKey = "local-chat" };
            Providers.Provider = Providers.Provider with { Type = ProviderType.OpenAiChat, Models = [model] };
            var graph = WorkflowV2Fixtures.Graph(); graph["nodes"]![1]!["data"]!["modelId"] = model.Id.ToString();
            Plan = WorkflowGraphCompiler.Validate(graph).Plan!;
        }

        /// <summary>生产守卫后接合成传输，保持生产响应体大小限制。</summary>
        public HttpClient CreateClient(string name) => new(new WorkflowV2RequestGuard(Network) { InnerHandler = Transport }, disposeHandler: false)
            { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = 1024 * 1024 };
        /// <summary>释放测试传输，不影响任何全局 HTTP 服务。</summary>
        public void Dispose() => Transport.Dispose();
    }

    /// <summary>不访问网络的文本协议分派器测试替身。</summary>
    private sealed class RecordingDispatcher : ITextGenerationDispatcher
    {
        /// <summary>最近一次收到的解析后协议。</summary>
        public ProviderType Protocol { get; private set; }
        /// <summary>最近一次收到的请求，包括工作流专用客户端名称。</summary>
        public TextGenerationRequest Request { get; private set; } = null!;

        /// <summary>记录参数并返回合成文本。</summary>
        public Task<TextGenerationResult> GenerateAsync(
            ProviderType protocol, TextGenerationRequest request, CancellationToken ct)
        {
            Protocol = protocol;
            Request = request;
            return Task.FromResult(TextGenerationResult.Success("dispatcher answer"));
        }
    }

    /// <summary>无密钥测试不应触发解密；任何调用说明凭据处理越界。</summary>
    private sealed class Protector : IApiKeyProtector
    {
        /// <summary>本测试不创建或存储真实密钥。</summary>
        public ApiKeySecret Protect(string apiKey) => throw new NotSupportedException();
        /// <summary>本测试的本地模型无密钥，解密不应发生。</summary>
        public string Unprotect(ApiKeySecret secret) => throw new NotSupportedException();
    }
}
