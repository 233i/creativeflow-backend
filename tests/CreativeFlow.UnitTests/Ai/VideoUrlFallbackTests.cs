using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;
using CreativeFlow.Infrastructure.Ai.Protocols;

namespace CreativeFlow.UnitTests.Ai;

public sealed class VideoUrlFallbackTests
{
    /// <summary>未实现帧角色的兼容路由不能把首尾帧静默转换成参考图，也不能触发上游计费请求。</summary>
    [Theory]
    [InlineData("routin-ai", "doubao-seedance-2-0-fast-260128")]
    [InlineData(null, "sora-2")]
    public async Task UnsupportedFrameRoutesFailBeforeCallingUpstream(string? builtinId, string modelKey)
    {
        var handler = new RecordingHandler();
        var client = new OpenAiVideoClient(new TestHttpClientFactory(handler), NullLogger<OpenAiVideoClient>.Instance);
        var request = Request(Provider(builtinId, ProviderType.OpenAiVideo, "https://relay.example/v1"), modelKey)
            with { InputMode = "first-last" };
        var result = await client.SubmitAsync(request, CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains("does not support first-last", result.Error);
        Assert.Empty(handler.Bodies);
    }

    /// <summary>回归场景：Seedance Retries Rejected Vendor Url With Signed Asset Url。</summary>
    [Fact]
    public async Task SeedanceRetriesRejectedVendorUrlWithSignedAssetUrl()
    {
        var handler = HandlerWithRejectedUrlThenSuccess();
        var client = new SeedanceVideoClient(
            new TestHttpClientFactory(handler), NullLogger<SeedanceVideoClient>.Instance);

        var result = await client.SubmitAsync(
            Request(SeedanceProvider(), "seedance-2.0-mini"), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("job-fallback", result.UpstreamJobId);
        Assert.Equal(2, handler.Bodies.Count);
        Assert.Equal("https://vendor.example/source.png", SeedanceUrl(handler.Bodies[0]));
        Assert.Equal("https://creative.example/signed.png", SeedanceUrl(handler.Bodies[1]));
    }

    /// <summary>回归场景：Routin Retries Rejected Vendor Url With Signed Asset Url。</summary>
    [Fact]
    public async Task RoutinRetriesRejectedVendorUrlWithSignedAssetUrl()
    {
        var handler = HandlerWithRejectedUrlThenSuccess();
        var client = new OpenAiVideoClient(
            new TestHttpClientFactory(handler), NullLogger<OpenAiVideoClient>.Instance);

        var result = await client.SubmitAsync(
            Request(RoutinProvider(), "sd-2.0-mini-720p"), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("job-fallback", result.UpstreamJobId);
        Assert.Equal(2, handler.Bodies.Count);
        Assert.Equal("https://vendor.example/source.png", RoutinUrl(handler.Bodies[0]));
        Assert.Equal("https://creative.example/signed.png", RoutinUrl(handler.Bodies[1]));
    }

    /// <summary>回归场景：Seedance Over Open Ai Retries Rejected Vendor Url With Signed Asset Url。</summary>
    [Fact]
    public async Task SeedanceOverOpenAiRetriesRejectedVendorUrlWithSignedAssetUrl()
    {
        var handler = HandlerWithRejectedUrlThenSuccess();
        var client = new OpenAiVideoClient(
            new TestHttpClientFactory(handler), NullLogger<OpenAiVideoClient>.Instance);

        var result = await client.SubmitAsync(
            Request(
                Provider(null, ProviderType.OpenAiVideo, "https://api.example/v1"),
                "doubao-seedance-2.0-fast"),
            CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("job-fallback", result.UpstreamJobId);
        Assert.Equal(2, handler.Bodies.Count);
        Assert.Equal("https://vendor.example/source.png", OpenAiSeedanceUrl(handler.Bodies[0]));
        Assert.Equal("https://creative.example/signed.png", OpenAiSeedanceUrl(handler.Bodies[1]));
    }

    /// <summary>回归场景：Mini Max H3 Retries Rejected Source Url With Signed Asset Url。</summary>
    [Fact]
    public async Task MiniMaxH3RetriesRejectedSourceUrlWithSignedAssetUrl()
    {
        var handler = HandlerWithRejectedUrlThenSuccess();
        var client = new MiniMaxVideoClient(
            new TestHttpClientFactory(handler), NullLogger<MiniMaxVideoClient>.Instance);

        var result = await client.SubmitAsync(
            Request(
                Provider(null, ProviderType.MiniMaxVideo, "https://api.example/v1"),
                "MiniMax-H3"),
            CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("job-fallback", result.UpstreamJobId);
        Assert.Equal(2, handler.Bodies.Count);
        Assert.Equal(
            "https://vendor.example/source.png",
            (string?)JsonNode.Parse(handler.Bodies[0])?["metadata"]?["first_frame_image"]);
        Assert.Equal(
            "https://creative.example/signed.png",
            (string?)JsonNode.Parse(handler.Bodies[1])?["metadata"]?["first_frame_image"]);
    }

    /// <summary>回归场景：Url Only Models Reject Missing Public Origin Before Calling Provider。</summary>
    [Fact]
    public async Task UrlOnlyModelsRejectMissingPublicOriginBeforeCallingProvider()
    {
        var handler = new RecordingHandler();
        var factory = new TestHttpClientFactory(handler);
        var imageWithoutUrl = new ReferenceImage([1], "image/png");

        var seedance = await new SeedanceVideoClient(
                factory, NullLogger<SeedanceVideoClient>.Instance)
            .SubmitAsync(
                Request(SeedanceProvider(), "seedance-2.0-mini") with
                {
                    ReferenceImages = [imageWithoutUrl],
                },
                CancellationToken.None);
        var routin = await new OpenAiVideoClient(
                factory, NullLogger<OpenAiVideoClient>.Instance)
            .SubmitAsync(
                Request(RoutinProvider(), "sd-2.0-mini-720p") with
                {
                    ReferenceImages = [imageWithoutUrl],
                },
                CancellationToken.None);
        var seedanceOverOpenAi = await new OpenAiVideoClient(
                factory, NullLogger<OpenAiVideoClient>.Instance)
            .SubmitAsync(
                Request(
                    Provider(null, ProviderType.OpenAiVideo, "https://api.example/v1"),
                    "doubao-seedance-2.0-fast") with
                {
                    ReferenceImages = [imageWithoutUrl],
                },
                CancellationToken.None);
        var minimax = await new MiniMaxVideoClient(
                factory, NullLogger<MiniMaxVideoClient>.Instance)
            .SubmitAsync(
                Request(
                    Provider(null, ProviderType.MiniMaxVideo, "https://api.example/v1"),
                    "MiniMax-H3") with
                {
                    ReferenceImages = [imageWithoutUrl],
                },
                CancellationToken.None);

        Assert.False(seedance.Ok);
        Assert.False(routin.Ok);
        Assert.False(seedanceOverOpenAi.Ok);
        Assert.False(minimax.Ok);
        Assert.Contains("PublicOrigin", seedance.Error);
        Assert.Contains("PublicOrigin", routin.Error);
        Assert.Contains("PublicOrigin", seedanceOverOpenAi.Error);
        Assert.Contains("PublicOrigin", minimax.Error);
        Assert.Empty(handler.Bodies);
    }

    /// <summary>回归场景：Legacy Seedance Does Not Retry An Unchanged Inline Request。</summary>
    [Fact]
    public async Task LegacySeedanceDoesNotRetryAnUnchangedInlineRequest()
    {
        var handler = HandlerWithRejectedUrl();
        var client = new SeedanceVideoClient(
            new TestHttpClientFactory(handler), NullLogger<SeedanceVideoClient>.Instance);

        var result = await client.SubmitAsync(
            Request(SeedanceProvider(), "seedance-1.5-pro"), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Single(handler.Bodies);
    }

    /// <summary>回归场景：Generic Open Ai Video Does Not Retry When Reference Urls Are Not In Its Request。</summary>
    [Fact]
    public async Task GenericOpenAiVideoDoesNotRetryWhenReferenceUrlsAreNotInItsRequest()
    {
        var handler = HandlerWithRejectedUrl();
        var client = new OpenAiVideoClient(
            new TestHttpClientFactory(handler), NullLogger<OpenAiVideoClient>.Instance);

        var result = await client.SubmitAsync(
            Request(Provider(null, ProviderType.OpenAiVideo, "https://api.example/v1"), "sora-2"),
            CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Single(handler.Bodies);
    }

    /// <summary>回归场景：Request。</summary>
    private static VideoGenerationRequest Request(AiProvider provider, string modelKey) => new(
        provider,
        ApiKey: "sk-test",
        modelKey,
        Prompt: "move forward",
        ReferenceImages:
        [
            new ReferenceImage(
                [1],
                "image/png",
                "https://vendor.example/source.png",
                "https://creative.example/signed.png"),
        ],
        InputMode: "reference",
        Aspect: "16:9",
        Resolution: "720p",
        DurationSeconds: 5,
        Fps: null,
        Seed: null,
        Watermark: null,
        GenerateAudio: null);

    /// <summary>构造 Seedance 协议的测试渠道。</summary>
    private static AiProvider SeedanceProvider() => Provider(
        builtinId: null, ProviderType.SeedanceVideo, "https://api.example/v1");

    /// <summary>构造用于兼容视频接口测试的渠道配置。</summary>
    private static AiProvider RoutinProvider() => Provider(
        "routin-ai", ProviderType.OpenAiVideo, "https://api.example/v1");

    /// <summary>构造当前协议测试使用的渠道配置。</summary>
    private static AiProvider Provider(
        string? builtinId,
        ProviderType type,
        string baseUrl) => new(
        ProviderId.New(),
        builtinId,
        Name: "provider",
        Description: null,
        Icon: null,
        type,
        ApiKey: null,
        baseUrl,
        Enabled: true,
        PresetVersion: null,
        SortOrder: 0,
        CreatedAt: DateTimeOffset.UnixEpoch,
        UpdatedAt: DateTimeOffset.UnixEpoch);

    /// <summary>先拒绝首选媒体地址再返回成功，验证既有回退次数与顺序。</summary>
    private static RecordingHandler HandlerWithRejectedUrlThenSuccess() => new(
        RejectedUrlResponse(),
        new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{ "id": "job-fallback" }""",
                Encoding.UTF8,
                "application/json"),
        });

    /// <summary>始终返回媒体地址拒绝响应，验证错误不会被无限重试。</summary>
    private static RecordingHandler HandlerWithRejectedUrl() => new(RejectedUrlResponse());

    /// <summary>创建上游拒绝参考媒体地址的固定错误响应。</summary>
    private static HttpResponseMessage RejectedUrlResponse() => new(HttpStatusCode.BadRequest)
    {
        Content = new StringContent(
            """{ "error": { "message": "image URL expired" } }""",
            Encoding.UTF8,
            "application/json"),
    };

    /// <summary>回归场景：Seedance Url。</summary>
    private static string? SeedanceUrl(string body) =>
        (string?)JsonNode.Parse(body)?["content"]?[0]?["image_url"]?["url"];

    /// <summary>返回测试兼容渠道使用的基础地址。</summary>
    private static string? RoutinUrl(string body) => SeedanceUrl(body);

    /// <summary>回归场景：Open Ai Seedance Url。</summary>
    private static string? OpenAiSeedanceUrl(string body) =>
        (string?)JsonNode.Parse(body)?["metadata"]?["content"]?[0]?["image_url"]?["url"];

    /// <summary>初始化 RecordingHandler，保留既有依赖、配置校验与资源生命周期。</summary>
    private sealed class RecordingHandler(params HttpResponseMessage[] responses)
        : HttpMessageHandler
    {
        /// <summary>测试按既有顺序返回的响应队列。</summary>
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        /// <summary>测试按发送顺序记录的请求正文。</summary>
        public List<string> Bodies { get; } = [];

        /// <summary>记录每次提交正文并依次返回预设响应，用于校验参考 URL 回退的次数和请求变化。</summary>
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
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
