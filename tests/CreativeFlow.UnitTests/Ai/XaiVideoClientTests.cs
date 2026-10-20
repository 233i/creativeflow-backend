using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Infrastructure.Ai.Protocols;

namespace CreativeFlow.UnitTests.Ai;

public sealed class XaiVideoClientTests
{
    /// <summary>回归场景：Routin Uses The Xai Generation And Polling Routes。</summary>
    [Fact]
    public async Task RoutinUsesTheXaiGenerationAndPollingRoutes()
    {
        var handler = new RecordingHandler(
            Json(HttpStatusCode.OK, """{ "request_id": "request-1" }"""),
            Json(HttpStatusCode.OK,
                """{ "status": "done", "progress": 100, "video": { "url": "https://cdn.example/result.mp4" } }"""));
        var client = new XaiVideoClient(
            new TestHttpClientFactory(handler), NullLogger<XaiVideoClient>.Instance);

        var submitted = await client.SubmitAsync(Request(
            [new ReferenceImage([1, 2, 3], "image/png")]), CancellationToken.None);
        var polled = await client.PollAsync(
            Provider(), "sk-test", submitted.UpstreamJobId!, CancellationToken.None);

        Assert.True(submitted.Ok);
        Assert.Equal("request-1", submitted.UpstreamJobId);
        Assert.Equal(VideoJobState.Succeeded, polled.State);
        Assert.Equal("https://cdn.example/result.mp4", polled.VideoUrl);
        Assert.Equal(1, polled.Progress);

        Assert.Collection(
            handler.Requests,
            request =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal(
                    "https://api.routin.ai/xai/v1/videos/generations", request.Url);
                Assert.Equal("Bearer sk-test", request.Authorization);
                var body = JsonNode.Parse(request.Body!)!;
                Assert.Equal("grok-imagine-video-1.5-preview", (string?)body["model"]);
                Assert.Equal("16:9", (string?)body["aspect_ratio"]);
                Assert.Null(body["ratio"]);
                Assert.Null(body["content"]);
                Assert.Equal(
                    "data:image/png;base64,AQID", (string?)body["image"]?["url"]);
                Assert.Null(body["reference_images"]);
            },
            request =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal(
                    "https://api.routin.ai/xai/v1/videos/request-1", request.Url);
                Assert.Equal("Bearer sk-test", request.Authorization);
            });
    }

    /// <summary>回归场景：Multiple Images Use Reference Images Without An Image Field。</summary>
    [Fact]
    public void MultipleImagesUseReferenceImagesWithoutAnImageField()
    {
        var body = XaiVideoClient.BuildRequestBody(Request(
        [
            new ReferenceImage([1], "image/png"),
            new ReferenceImage([2], "image/jpeg"),
        ]));

        Assert.Null(body["image"]);
        var references = Assert.IsType<JsonArray>(body["reference_images"]);
        Assert.Equal(2, references.Count);
        Assert.Equal("data:image/png;base64,AQ==", (string?)references[0]?["url"]);
        Assert.Equal("data:image/jpeg;base64,Ag==", (string?)references[1]?["url"]);
        Assert.Equal(7, (int?)body["duration"]);
        Assert.Equal("720p", (string?)body["resolution"]);
    }

    /// <summary>回归场景：Extension Uses The Xai Extension Route And Video Data Url。</summary>
    [Fact]
    public async Task ExtensionUsesTheXaiExtensionRouteAndVideoDataUrl()
    {
        var handler = new RecordingHandler(
            Json(HttpStatusCode.OK, """{ "request_id": "extension-1" }"""));
        var client = new XaiVideoClient(
            new TestHttpClientFactory(handler), NullLogger<XaiVideoClient>.Instance);

        var submitted = await client.ExtendAsync(
            new VideoExtensionRequest(
                Provider(),
                "sk-test",
                "grok-imagine-video",
                "keep walking into the light",
                new ReferenceVideo([1, 2, 3], "video/mp4"),
                6),
            CancellationToken.None);

        Assert.True(submitted.Ok);
        Assert.Equal("extension-1", submitted.UpstreamJobId);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.routin.ai/xai/v1/videos/extensions", request.Url);

        var body = JsonNode.Parse(request.Body!)!;
        Assert.Equal("grok-imagine-video", (string?)body["model"]);
        Assert.Equal("keep walking into the light", (string?)body["prompt"]);
        Assert.Equal(6, (int?)body["duration"]);
        Assert.Equal("data:video/mp4;base64,AQID", (string?)body["video"]?["url"]);
        Assert.Null(body["aspect_ratio"]);
        Assert.Null(body["resolution"]);
    }

    /// <summary>回归场景：Extension Reuses A Provider Url Without Uploading The Video Again。</summary>
    [Fact]
    public async Task ExtensionReusesAProviderUrlWithoutUploadingTheVideoAgain()
    {
        var handler = new RecordingHandler(
            Json(HttpStatusCode.OK, """{ "request_id": "extension-url" }"""));
        var client = new XaiVideoClient(
            new TestHttpClientFactory(handler), NullLogger<XaiVideoClient>.Instance);

        var submitted = await client.ExtendAsync(
            new VideoExtensionRequest(
                Provider(),
                "sk-test",
                "grok-imagine-video",
                "continue down the street",
                new ReferenceVideo(
                    [], "video/mp4", "https://cdn.example/source.mp4"),
                6),
            CancellationToken.None);

        Assert.True(submitted.Ok);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.routin.ai/xai/v1/videos/extensions", request.Url);
        var body = JsonNode.Parse(request.Body!)!;
        Assert.Equal("https://cdn.example/source.mp4", (string?)body["video"]?["url"]);
        Assert.Null(body["video"]?["file_id"]);
    }

    /// <summary>回归场景：Extension Retries A Rejected Vendor Url With The Signed Fallback。</summary>
    [Fact]
    public async Task ExtensionRetriesARejectedVendorUrlWithTheSignedFallback()
    {
        var handler = new RecordingHandler(
            Json(HttpStatusCode.BadRequest,
                """{ "error": { "message": "source video URL expired" } }"""),
            Json(HttpStatusCode.OK, """{ "request_id": "extension-fallback" }"""));
        var client = new XaiVideoClient(
            new TestHttpClientFactory(handler), NullLogger<XaiVideoClient>.Instance);

        var submitted = await client.ExtendAsync(
            new VideoExtensionRequest(
                Provider(),
                "sk-test",
                "grok-imagine-video",
                "continue down the street",
                new ReferenceVideo(
                    [],
                    "video/mp4",
                    "https://vendor.example/source.mp4",
                    "https://creative.example/signed.mp4"),
                6),
            CancellationToken.None);

        Assert.True(submitted.Ok);
        Assert.Equal("extension-fallback", submitted.UpstreamJobId);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(
            "https://vendor.example/source.mp4",
            (string?)JsonNode.Parse(handler.Requests[0].Body!)?["video"]?["url"]);
        Assert.Equal(
            "https://creative.example/signed.mp4",
            (string?)JsonNode.Parse(handler.Requests[1].Body!)?["video"]?["url"]);
    }

    /// <summary>回归场景：Large Extension Input Uploads An Expiring File Before Submitting。</summary>
    [Fact]
    public async Task LargeExtensionInputUploadsAnExpiringFileBeforeSubmitting()
    {
        var handler = new RecordingHandler(
            Json(HttpStatusCode.OK, """{ "id": "file-source" }"""),
            Json(HttpStatusCode.OK, """{ "request_id": "extension-file" }"""));
        var client = new XaiVideoClient(
            new TestHttpClientFactory(handler), NullLogger<XaiVideoClient>.Instance);

        var submitted = await client.ExtendAsync(
            new VideoExtensionRequest(
                Provider(),
                "sk-test",
                "grok-imagine-video",
                "continue down the street",
                new ReferenceVideo(
                    new byte[XaiVideoClient.MaxInlineVideoBytes + 1], "video/mp4"),
                6),
            CancellationToken.None);

        Assert.True(submitted.Ok);
        Assert.Collection(
            handler.Requests,
            upload =>
            {
                Assert.Equal("https://api.routin.ai/xai/v1/files", upload.Url);
                Assert.StartsWith("multipart/form-data", upload.ContentType);
                var expiresAt = upload.Body!.IndexOf("expires_after", StringComparison.Ordinal);
                var fileAt = upload.Body.IndexOf("filename=source.mp4", StringComparison.Ordinal);
                Assert.True(expiresAt >= 0);
                Assert.True(fileAt > expiresAt);
            },
            extension =>
            {
                Assert.Equal(
                    "https://api.routin.ai/xai/v1/videos/extensions", extension.Url);
                var body = JsonNode.Parse(extension.Body!)!;
                Assert.Equal("file-source", (string?)body["video"]?["file_id"]);
                Assert.Null(body["video"]?["url"]);
            });
    }

    /// <summary>回归场景：Extension Rejects The Preview Model Before Calling Upstream。</summary>
    [Fact]
    public async Task ExtensionRejectsThePreviewModelBeforeCallingUpstream()
    {
        var handler = new RecordingHandler();
        var client = new XaiVideoClient(
            new TestHttpClientFactory(handler), NullLogger<XaiVideoClient>.Instance);

        var submitted = await client.ExtendAsync(
            new VideoExtensionRequest(
                Provider(),
                "sk-test",
                "grok-imagine-video-1.5-preview",
                "continue",
                new ReferenceVideo([1], "video/mp4"),
                6),
            CancellationToken.None);

        Assert.False(submitted.Ok);
        Assert.Equal(400, submitted.StatusCode);
        Assert.Empty(handler.Requests);
    }

    /// <summary>回归场景：Terminal Failure Statuses Do Not Keep Polling。</summary>
    [Theory]
    [InlineData(
        "{ \"status\": \"failed\", \"error\": { \"message\": \"provider rejected request\" } }",
        "provider rejected request")]
    [InlineData("{ \"status\": \"expired\" }", "Video request expired.")]
    public void TerminalFailureStatusesDoNotKeepPolling(string response, string expectedError)
    {
        var result = XaiVideoClient.ParsePollResponse(response);

        Assert.Equal(VideoJobState.Failed, result.State);
        Assert.Equal(expectedError, result.Error);
    }

    /// <summary>构造带关联 id 的 MCP JSON-RPC 请求对象。</summary>
    private static VideoGenerationRequest Request(IReadOnlyList<ReferenceImage> images) => new(
        Provider(),
        ApiKey: "sk-test",
        ModelKey: "grok-imagine-video-1.5-preview",
        Prompt: "move forward",
        images,
        InputMode: "reference",
        Aspect: "16:9",
        Resolution: "720p",
        DurationSeconds: 7,
        Fps: null,
        Seed: null,
        Watermark: null,
        GenerateAudio: null);

    /// <summary>构造当前协议测试使用的渠道配置。</summary>
    private static AiProvider Provider() => new(
        ProviderId.New(),
        BuiltinId: "routin-ai",
        Name: "Routin AI",
        Description: null,
        Icon: null,
        ProviderType.OpenAiChat,
        ApiKey: null,
        BaseUrl: "https://api.routin.ai/v1",
        Enabled: true,
        PresetVersion: null,
        SortOrder: 0,
        CreatedAt: DateTimeOffset.UnixEpoch,
        UpdatedAt: DateTimeOffset.UnixEpoch);

    /// <summary>创建具有 JSON 媒体类型的模拟上游响应。</summary>
    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    /// <param name="Method">设备关联、协议请求或通知采用的方法。</param>
    /// <param name="Url">资源的交付或访问地址。</param>
    /// <param name="Authorization">测试捕获的 Authorization 头，用于检查凭据是否正确发送或隔离。</param>
    /// <param name="ContentType">测试捕获的请求正文媒体类型。</param>
    /// <param name="Body">测试捕获的原始 HTTP 请求正文。</param>
    private sealed record CapturedRequest(
        HttpMethod Method,
        string Url,
        string? Authorization,
        string? ContentType,
        string? Body);

    /// <summary>初始化 RecordingHandler，保留既有依赖、配置校验与资源生命周期。</summary>
    private sealed class RecordingHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        /// <summary>测试按既有顺序返回的响应队列。</summary>
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        /// <summary>测试按实际发送顺序捕获的 HTTP 请求集合。</summary>
        public List<CapturedRequest> Requests { get; } = [];

        /// <summary>捕获 xAI 请求的方法、URL、鉴权、内容类型和正文，并依次返回预设响应。</summary>
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri!.ToString(),
                request.Headers.Authorization?.ToString(),
                request.Content?.Headers.ContentType?.ToString(),
                request.Content is null
                    ? null
                    : await request.Content.ReadAsStringAsync(cancellationToken)));
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
