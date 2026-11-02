using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Common;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Infrastructure.Ai.Protocols;
using CreativeFlow.UnitTests.Support;

namespace CreativeFlow.UnitTests.Ai;

/// <summary>使用合成 HTTP 响应复现参考图 404，锁定正确 JSON、授权内容、错误及单次调用边界。</summary>
public sealed class SeedreamImagesTests
{
    /// <summary>文生图与单、多参考图都只调用原版本下 generations，字段不借用 OpenAI multipart。</summary>
    [Theory]
    [InlineData(0)][InlineData(1)][InlineData(2)]
    public async Task ReferencesUseJsonGenerationsAndPreserveAuthorizedBytes(int count)
    {
        var handler = new Handler(); var client = Client(handler);
        var input = Request() with { ReferenceImages = Enumerable.Range(0, count).Select(i => new ReferenceImage([(byte)i, 2, 3], "image/png", "/private-not-public")).ToArray() };
        var result = await client.GenerateAsync(input, TestContext.Current.CancellationToken);
        Assert.True(result.Ok); Assert.Single(result.Images); Assert.Single(handler.Requests);
        var sent = handler.Requests[0]; var body = JsonNode.Parse(sent.Body)!;
        Assert.Equal("https://ark.cn-beijing.volces.com/api/plan/v3/images/generations", sent.Url);
        Assert.Equal("application/json; charset=utf-8", sent.ContentType); Assert.Equal("Bearer synthetic-test-key", sent.Authorization);
        Assert.Equal("png", body["output_format"]!.GetValue<string>());
        Assert.Null(body["n"]); Assert.Null(body["quality"]); Assert.Null(body["seed"]);
        if (count == 0) Assert.Null(body["image"]);
        else
        {
            var values = count == 1 ? new[] { body["image"]!.GetValue<string>() } : body["image"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
            Assert.Equal(count, values.Length);
            for (var i = 0; i < count; i++) Assert.Equal(input.ReferenceImages[i].Bytes, Convert.FromBase64String(values[i].Split(',')[1]));
        }
        Assert.DoesNotContain("private-not-public", sent.Body);
    }

    /// <summary>空错误正文显示真实状态与路径，404 后不切换端点、不自动重发。</summary>
    [Theory]
    [InlineData(404, "")][InlineData(429, "   ")][InlineData(400, "{\"error\":{\"message\":\"invalid image\"}}")]
    public async Task FailedResponsesKeepHttpDetailsAndNeverRetry(int code, string body)
    {
        var handler = new Handler { Status = (HttpStatusCode)code, Failure = body };
        var result = await Client(handler).GenerateAsync(Request() with { Count = 4 }, TestContext.Current.CancellationToken);
        Assert.False(result.Ok); Assert.Equal(code, result.StatusCode); Assert.Contains($"HTTP {code}", result.Error);
        Assert.Contains("/api/plan/v3/images/generations", result.Error); Assert.DoesNotContain("synthetic-test-key", result.Error);
        Assert.Single(handler.Requests);
    }

    /// <summary>多张输出逐张执行，后续失败不丢弃已返回图片，不补发替代请求。</summary>
    [Fact]
    public async Task RequestedCountAndPartialFailureDoNotRepeatPaidCalls()
    {
        var handler = new Handler { FailAfter = 1 };
        var result = await Client(handler).GenerateAsync(Request() with { Count = 4 }, TestContext.Current.CancellationToken);
        Assert.True(result.Ok); Assert.Single(result.Images); Assert.Equal(2, handler.Requests.Count);
        var success = new Handler(); result = await Client(success).GenerateAsync(Request() with { Count = 3 }, TestContext.Current.CancellationToken);
        Assert.Equal(3, result.Images.Count); Assert.Equal(3, success.Requests.Count);
    }

    /// <summary>空提示词、空图片、蒙版及不支持编码在发请求前失败。</summary>
    [Theory]
    [InlineData("prompt")][InlineData("reference")][InlineData("mask")][InlineData("format")]
    public async Task InvalidInputsNeverCallTheProvider(string kind)
    {
        var handler = new Handler(); var request = Request();
        request = kind switch
        {
            "prompt" => request with { Prompt = " " },
            "reference" => request with { ReferenceImages = [new([], "image/png")] },
            "mask" => request with { Mask = new([1], "image/png") },
            _ => request with { OutputFormat = "webp" },
        };
        Assert.False((await Client(handler).GenerateAsync(request, TestContext.Current.CancellationToken)).Ok);
        Assert.Empty(handler.Requests);
    }

    /// <summary>OpenAI 原端点仍使用 multipart，空错误正文也不再吞掉 HTTP 404。</summary>
    [Fact]
    public async Task OpenAiEditsRemainMultipartAndReportEmpty404()
    {
        var handler = new Handler { Status = HttpStatusCode.NotFound };
        var client = Transport(handler);
        var result = await client.GenerateAsync(Request() with { ReferenceImages = [new([1, 2], "image/png")] }, TestContext.Current.CancellationToken);
        Assert.Contains("/images/edits", handler.Requests[0].Url); Assert.StartsWith("multipart/form-data", handler.Requests[0].ContentType);
        Assert.Contains("HTTP 404", result.Error); Assert.Single(handler.Requests);
        Assert.Contains("HTTP 404", ImageGenerationResult.Failure(404, "").Error);
    }

    /// <summary>原生参考图地址即使私有也不直接下载，延迟结果沿用保存检查点规则。</summary>
    private static SeedreamImagesClient Client(Handler handler) => new(Transport(handler));
    /// <summary>构造共享响应传输，不访问真实服务商。</summary>
    private static OpenAiImagesClient Transport(Handler handler) => new(new Factory(handler), NullLogger<OpenAiImagesClient>.Instance, new Clock());
    /// <summary>合成 Ark 套餐模型，保留用于检验不会透传的旧质量和随机种子。</summary>
    private static ImageGenerationRequest Request() => new(new(ProviderId.New(), null, "Ark", null, null, ProviderType.SeedreamImages,
        null, "https://ark.cn-beijing.volces.com/api/plan/v3", true, null, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
        "synthetic-test-key", "doubao-seedream-5.0-pro", "把参考图变为动画风格", [], null, "2048x2048", "high", "png", "url", 1, 42, DeferDownloads: true);
    /// <summary>固定结果地址保留窗口的时钟。</summary>
    private sealed class Clock : IClock
    {
        /// <summary>测试基准时间。</summary>
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;
    }
    /// <summary>只注入内存 HTTP 处理器。</summary>
    private sealed class Factory(Handler handler) : IHttpClientFactory
    {
        /// <summary>返回不连接网络的客户端。</summary>
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
    /// <summary>记录实际请求并模拟成功、空 404 和部分失败。</summary>
    private sealed class Handler : HttpMessageHandler
    {
        /// <summary>已发送请求的安全字段。</summary>
        internal List<(string Url, string Body, string ContentType, string? Authorization)> Requests { get; } = [];
        /// <summary>固定响应状态。</summary>
        internal HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        /// <summary>失败正文，空值复现正式环境。</summary>
        internal string Failure { get; init; } = "";
        /// <summary>允许成功的次数，之后模拟拒绝。</summary>
        internal int FailAfter { get; init; } = int.MaxValue;
        /// <summary>捕获请求，返回合成结果，不执行图像生成。</summary>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.RequestUri!.ToString(), await request.Content!.ReadAsStringAsync(ct), request.Content.Headers.ContentType!.ToString(), request.Headers.Authorization?.ToString()));
            var status = Requests.Count > FailAfter ? HttpStatusCode.TooManyRequests : Status;
            return new(status) { Content = new StringContent(status == HttpStatusCode.OK ? "{\"data\":[{\"url\":\"https://cdn.example.test/output.png\"}]}" : Failure, Encoding.UTF8, "application/json") };
        }
    }
}
