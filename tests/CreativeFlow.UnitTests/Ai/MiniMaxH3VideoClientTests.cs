using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Infrastructure.Ai.Protocols;

namespace CreativeFlow.UnitTests.Ai;

public sealed class MiniMaxH3VideoClientTests
{
    /// <summary>回归场景：Native Protocol Round Trips。</summary>
    [Fact]
    public void NativeProtocolRoundTrips()
    {
        Assert.Equal("minimax-h3", ProviderType.MiniMaxH3.ToWire());
        Assert.True(AiEnumNames.TryParseProviderType("minimax-h3", out var type));
        Assert.Equal(ProviderType.MiniMaxH3, type);
    }

    /// <summary>回归场景：Reference Mode Preserves Every Image In Native Content。</summary>
    [Fact]
    public void ReferenceModePreservesEveryImageInNativeContent()
    {
        var body = MiniMaxH3VideoClient.BuildRequestBody(Request(Images(9)));
        Assert.Equal("MiniMax-H3", (string?)body["model"]);
        Assert.Equal("768P", (string?)body["resolution"]);
        Assert.Equal(5, (int?)body["duration"]);
        Assert.Equal("16:9", (string?)body["ratio"]);
        Assert.True((bool?)body["aigc_watermark"]);
        foreach (var field in new[] { "prompt", "metadata", "input_reference", "first_frame_image", "seed", "fps", "generate_audio" })
            Assert.Null(body[field]);

        var content = Assert.IsType<JsonArray>(body["content"]);
        Assert.Equal(10, content.Count);
        Assert.Equal("text", (string?)content[0]?["type"]);
        Assert.Equal("animate this product", (string?)content[0]?["text"]);
        for (var index = 1; index < content.Count; index++)
        {
            Assert.Equal("image_url", (string?)content[index]?["type"]);
            Assert.Equal("reference_image", (string?)content[index]?["role"]);
            Assert.Equal($"https://images.example/{index - 1}.png", (string?)content[index]?["image_url"]?["url"]);
        }
    }

    /// <summary>回归场景：First Last Mode Uses Frame Roles And Adaptive Ratio。</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void FirstLastModeUsesFrameRolesAndAdaptiveRatio(int count)
    {
        var body = MiniMaxH3VideoClient.BuildRequestBody(Request(Images(count)) with { InputMode = "first-last" });
        Assert.Equal("adaptive", (string?)body["ratio"]);
        var content = Assert.IsType<JsonArray>(body["content"]);
        Assert.Equal(count + 1, content.Count);
        Assert.Equal("first_frame", (string?)content[1]?["role"]);
        if (count == 2) Assert.Equal("last_frame", (string?)content[2]?["role"]);
    }

    /// <summary>回归场景：Text To Video Keeps An Explicit Ratio Even With First Last Selected。</summary>
    [Fact]
    public void TextToVideoKeepsAnExplicitRatioEvenWithFirstLastSelected()
    {
        var body = MiniMaxH3VideoClient.BuildRequestBody(Request([]) with { InputMode = "first-last" });
        Assert.Single(Assert.IsType<JsonArray>(body["content"]));
        Assert.Equal("16:9", (string?)body["ratio"]);
    }

    /// <summary>回归场景：Maps Existing Resolution Values To Native Values。</summary>
    [Theory]
    [InlineData("720p", "768P")]
    [InlineData("768P", "768P")]
    [InlineData("1080p", "2K")]
    [InlineData("2K", "2K")]
    public void MapsExistingResolutionValuesToNativeValues(string input, string expected)
    {
        var body = MiniMaxH3VideoClient.BuildRequestBody(Request([]) with { Resolution = input });
        Assert.Equal(expected, (string?)body["resolution"]);
    }

    /// <summary>回归场景：Local Images Are Embedded And Signed Fallbacks Are Preferred Over Localhost。</summary>
    [Fact]
    public void LocalImagesAreEmbeddedAndSignedFallbacksArePreferredOverLocalhost()
    {
        var body = MiniMaxH3VideoClient.BuildRequestBody(Request([
            new ReferenceImage([1, 2, 3], "image/png", "http://localhost:5000/image"),
            new ReferenceImage([4], "image/jpeg", "data:image/jpeg;base64,BA==", "https://creative.example/signed.jpg"),
        ]));
        Assert.Equal("data:image/png;base64,AQID", (string?)body["content"]?[1]?["image_url"]?["url"]);
        Assert.Equal("https://creative.example/signed.jpg", (string?)body["content"]?[2]?["image_url"]?["url"]);
    }

    /// <summary>回归场景：Uses Official Submit And Poll Routes Even For A Model Override。</summary>
    [Theory]
    [InlineData("https://api.minimax.cn")]
    [InlineData("https://api.minimax.cn/v1")]
    [InlineData("https://api.minimax.cn/v2/")]
    [InlineData("https://api.minimax.cn/v2/video_generation")]
    public async Task UsesOfficialSubmitAndPollRoutesEvenForAModelOverride(string baseUrl)
    {
        var handler = new RecordingHandler(
            Json(HttpStatusCode.OK, """{"task_id":"h3-task-1"}"""),
            Json(HttpStatusCode.OK, """{"task":{"id":"h3-task-1","status":"succeeded","content":{"url":"https://cdn.example/video.mp4"},"usage":{"input_image_count":1}}}"""));
        var client = Client(handler);
        var request = Request(Images(1)) with { Provider = Provider(baseUrl) };

        var submitted = await client.SubmitAsync(request, TestContext.Current.CancellationToken);
        var polled = await client.PollAsync(request.Provider, request.ApiKey, submitted.UpstreamJobId!, TestContext.Current.CancellationToken);

        Assert.True(submitted.Ok);
        Assert.Equal("h3-task-1", submitted.UpstreamJobId);
        Assert.Equal(VideoJobState.Succeeded, polled.State);
        Assert.Equal("https://cdn.example/video.mp4", polled.VideoUrl);
        Assert.Equal(1, polled.Progress);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("https://api.minimax.cn/v2/video_generation", handler.Requests[0].Url);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal("application/json", handler.Requests[0].ContentType);
        Assert.Equal("https://api.minimax.cn/v2/query/video_generation/h3-task-1", handler.Requests[1].Url);
        Assert.Equal(HttpMethod.Get, handler.Requests[1].Method);
        Assert.All(handler.Requests, captured => Assert.Equal("Bearer test-key", captured.Authorization));
    }

    /// <summary>回归场景：Invalid Inputs Are Rejected Before Calling The Provider。</summary>
    [Theory]
    [InlineData("reference", 10, 5, "720p")]
    [InlineData("first-last", 3, 5, "720p")]
    [InlineData("reference", 1, 3, "720p")]
    [InlineData("reference", 1, 16, "720p")]
    [InlineData("reference", 1, 5, "480p")]
    [InlineData("unknown", 1, 5, "720p")]
    public async Task InvalidInputsAreRejectedBeforeCallingTheProvider(string mode, int images, int duration, string resolution)
    {
        var handler = new RecordingHandler();
        var result = await Client(handler).SubmitAsync(Request(Images(images)) with
        {
            InputMode = mode, DurationSeconds = duration, Resolution = resolution,
        }, TestContext.Current.CancellationToken);
        Assert.False(result.Ok);
        Assert.Equal(400, result.StatusCode);
        Assert.NotEmpty(result.Error!);
        Assert.Empty(handler.Requests);
    }

    /// <summary>回归场景：Missing Image Bytes Are Not Silently Dropped。</summary>
    [Fact]
    public async Task MissingImageBytesAreNotSilentlyDropped()
    {
        var handler = new RecordingHandler();
        var result = await Client(handler).SubmitAsync(
            Request([new ReferenceImage([], "image/png")]), TestContext.Current.CancellationToken);
        Assert.False(result.Ok);
        Assert.Contains("image bytes", result.Error);
        Assert.Empty(handler.Requests);
    }

    /// <summary>回归场景：Max Model Rejects Reference Mode。</summary>
    [Fact]
    public async Task MaxModelRejectsReferenceMode()
    {
        var handler = new RecordingHandler();
        var result = await Client(handler).SubmitAsync(Request(Images(1)) with
        { ModelKey = "MiniMax-H3-Max" }, TestContext.Current.CancellationToken);
        Assert.False(result.Ok);
        Assert.Contains("does not support reference", result.Error);
        Assert.Empty(handler.Requests);
    }

    /// <summary>回归场景：Rejected Native Requests Never Retry Without Images Or Switch Routes。</summary>
    [Theory]
    [InlineData(400)]
    [InlineData(404)]
    public async Task RejectedNativeRequestsNeverRetryWithoutImagesOrSwitchRoutes(int status)
    {
        var handler = new RecordingHandler(Json((HttpStatusCode)status,
            """{"error":{"message":"native route rejected"},"request_id":"test-request"}"""));
        var result = await Client(handler).SubmitAsync(Request(Images(1)), TestContext.Current.CancellationToken);
        Assert.False(result.Ok);
        Assert.Equal(status, result.StatusCode);
        Assert.Contains("native route rejected", result.Error);
        var sent = Assert.Single(handler.Requests);
        Assert.Contains("reference_image", sent.Body);
        Assert.EndsWith("/v2/video_generation", sent.Url);
        if (status == 404) Assert.Contains("requires POST /v2/video_generation", result.Error);
    }

    /// <summary>回归场景：Does Not Accept An Open Ai Or Malformed Success Response。</summary>
    [Theory]
    [InlineData("{\"id\":\"openai-task\"}")]
    [InlineData("[]")]
    [InlineData("null")]
    public async Task DoesNotAcceptAnOpenAiOrMalformedSuccessResponse(string response)
    {
        var handler = new RecordingHandler(Json(HttpStatusCode.OK, response));
        var result = await Client(handler).SubmitAsync(Request([]), TestContext.Current.CancellationToken);
        Assert.False(result.Ok);
        Assert.Contains("no task_id", result.Error);
    }

    /// <summary>回归场景：Maps Native Task States And Errors。</summary>
    [Theory]
    [InlineData("queued", VideoJobState.Pending)]
    [InlineData("running", VideoJobState.Running)]
    [InlineData("failed", VideoJobState.Failed)]
    [InlineData("cancelled", VideoJobState.Failed)]
    public async Task MapsNativeTaskStatesAndErrors(string status, VideoJobState expected)
    {
        var handler = new RecordingHandler(Json(HttpStatusCode.OK,
            """{"task":{"status":"$STATUS","error":{"message":"generation rejected"}}}""".Replace("$STATUS", status)));
        var result = await Client(handler).PollAsync(Provider(), "key", "task-1", TestContext.Current.CancellationToken);
        Assert.Equal(expected, result.State);
        if (expected == VideoJobState.Failed) Assert.Equal("generation rejected", result.Error);
    }

    /// <summary>回归场景：Retries Only Transient Polling Errors。</summary>
    [Theory]
    [InlineData(401, VideoJobState.Failed)]
    [InlineData(404, VideoJobState.Failed)]
    [InlineData(429, VideoJobState.Running)]
    [InlineData(503, VideoJobState.Running)]
    public async Task RetriesOnlyTransientPollingErrors(int status, VideoJobState expected)
    {
        var handler = new RecordingHandler(Json((HttpStatusCode)status, """{"error":{"message":"unavailable"}}"""));
        var result = await Client(handler).PollAsync(Provider(), "key", "task-1", TestContext.Current.CancellationToken);
        Assert.Equal(expected, result.State);
    }

    /// <summary>回归场景：Malformed Results Do Not Become Successful Videos。</summary>
    [Theory]
    [InlineData("{\"task\":{\"status\":\"succeeded\",\"content\":{}}}")]
    [InlineData("{\"task\":{\"status\":\"succeeded\",\"content\":{\"url\":\"javascript:alert(1)\"}}}")]
    [InlineData("{\"status\":\"completed\"}")]
    [InlineData("[]")]
    [InlineData("not-json")]
    public async Task MalformedResultsDoNotBecomeSuccessfulVideos(string response)
    {
        var handler = new RecordingHandler(Json(HttpStatusCode.OK, response));
        var result = await Client(handler).PollAsync(Provider(), "key", "task-1", TestContext.Current.CancellationToken);
        Assert.Equal(VideoJobState.Failed, result.State);
        Assert.Null(result.VideoUrl);
        Assert.NotEmpty(result.Error!);
    }

    /// <summary>回归场景：Images。</summary>
    private static ReferenceImage[] Images(int count) => Enumerable.Range(0, count)
        .Select(index => new ReferenceImage([1, 2, 3], "image/png", $"https://images.example/{index}.png")).ToArray();

    /// <summary>构造带关联 id 的 MCP JSON-RPC 请求对象。</summary>
    private static VideoGenerationRequest Request(IReadOnlyList<ReferenceImage> images) => new(
        Provider(), "test-key", "MiniMax-H3", "animate this product", images,
        "reference", "16:9", "720p", 5, 24, 123, true, false);

    /// <summary>构造当前协议测试使用的渠道配置。</summary>
    private static AiProvider Provider(string baseUrl = "https://api.minimax.cn/v1") => new(
        ProviderId.New(), null, "test", null, null, ProviderType.OpenAiVideo, null,
        baseUrl, true, null, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    /// <summary>组装使用受控 HTTP 响应和日志的协议客户端。</summary>
    private static MiniMaxH3VideoClient Client(RecordingHandler handler) => new(
        new TestHttpClientFactory(handler), NullLogger<MiniMaxH3VideoClient>.Instance);

    /// <summary>创建具有 JSON 媒体类型的模拟上游响应。</summary>
    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <param name="Method">设备关联、协议请求或通知采用的方法。</param>
    /// <param name="Url">资源的交付或访问地址。</param>
    /// <param name="Authorization">测试捕获的 Authorization 头，用于检查凭据是否正确发送或隔离。</param>
    /// <param name="ContentType">测试捕获的请求正文媒体类型。</param>
    /// <param name="Body">测试捕获的原始 HTTP 请求正文。</param>
    private sealed record CapturedRequest(HttpMethod Method, string Url, string? Authorization, string? ContentType, string? Body);

    /// <summary>初始化 RecordingHandler，保留既有依赖、配置校验与资源生命周期。</summary>
    private sealed class RecordingHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        /// <summary>测试按既有顺序返回的响应队列。</summary>
        private readonly Queue<HttpResponseMessage> _responses = new(responses);
        /// <summary>测试按实际发送顺序捕获的 HTTP 请求集合。</summary>
        public List<CapturedRequest> Requests { get; } = [];
        /// <summary>捕获 H3 请求的方法、URL、鉴权、内容类型和正文，再按队列返回预设上游响应。</summary>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(new CapturedRequest(request.Method, request.RequestUri!.ToString(),
                request.Headers.Authorization?.ToString(), request.Content?.Headers.ContentType?.MediaType,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(ct)));
            return _responses.Dequeue();
        }
    }

    /// <summary>初始化 TestHttpClientFactory，保留既有依赖、配置校验与资源生命周期。</summary>
    private sealed class TestHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        /// <summary>当前组件复用的客户端实例。</summary>
        private readonly HttpClient _client = new(handler);
        /// <summary>校验目的地址并创建带既有网络限制的 MCP HTTP 客户端。</summary>
        public HttpClient CreateClient(string name) => _client;
    }
}
