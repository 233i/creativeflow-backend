using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Common;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;
using CreativeFlow.Infrastructure.Ai.Protocols;

namespace CreativeFlow.UnitTests.Ai;

public sealed class OpenAiImagesSourceUrlTests
{
    /// <summary>验证延迟下载只发一次生成请求，并保留私有下载地址。</summary>
    [Fact]
    public async Task DeferredUrlIsReturnedWithoutDownloadingOrCallingGenerationAgain()
    {
        var handler = new ImageHandler("https://api.example/generated.png");
        var client = new OpenAiImagesClient(new TestHttpClientFactory(handler), NullLogger<OpenAiImagesClient>.Instance, new ClockStub());
        var result = await client.GenerateAsync(Request() with { DeferDownloads = true }, TestContext.Current.CancellationToken);
        var image = Assert.Single(result.Images);
        Assert.Empty(image.Bytes);
        Assert.Equal("https://api.example/generated.png", image.DownloadUrl);
        Assert.Null(image.SourceUrl);
        Assert.Equal(1, handler.Requests);
    }
    /// <summary>本次操作使用的时间基准。</summary>
    private static readonly DateTimeOffset Now = new(2026, 8, 23, 8, 0, 0, TimeSpan.Zero);

    /// <summary>回归场景：Public Generated Url Is Retained Alongside Downloaded Bytes。</summary>
    [Fact]
    public async Task PublicGeneratedUrlIsRetainedAlongsideDownloadedBytes()
    {
        const string sourceUrl = "https://cdn.example/generated.png?expires=soon";
        var handler = new ImageHandler(sourceUrl);
        var client = new OpenAiImagesClient(
            new TestHttpClientFactory(handler),
            NullLogger<OpenAiImagesClient>.Instance,
            new ClockStub());

        var result = await client.GenerateAsync(Request(), CancellationToken.None);

        var image = Assert.Single(result.Images);
        Assert.Equal(new byte[] { 1, 2, 3 }, image.Bytes);
        Assert.Equal("image/png", image.MediaType);
        Assert.Equal(sourceUrl, image.SourceUrl);
        Assert.Equal(Now.AddMinutes(5), image.SourceUrlExpiresAt);
    }

    /// <summary>回归场景：Provider Authenticated Url Is Not Offered To Another Provider。</summary>
    [Fact]
    public async Task ProviderAuthenticatedUrlIsNotOfferedToAnotherProvider()
    {
        const string sourceUrl = "https://api.example/generated.png";
        var handler = new ImageHandler(sourceUrl);
        var client = new OpenAiImagesClient(
            new TestHttpClientFactory(handler),
            NullLogger<OpenAiImagesClient>.Instance,
            new ClockStub());

        var image = Assert.Single((await client.GenerateAsync(
            Request(), CancellationToken.None)).Images);

        Assert.Null(image.SourceUrl);
        Assert.Null(image.SourceUrlExpiresAt);
        Assert.Equal("Bearer sk-test", handler.DownloadAuthorization);
    }

    /// <summary>回归场景：Request。</summary>
    private static ImageGenerationRequest Request() => new(
        new AiProvider(
            ProviderId.New(),
            BuiltinId: null,
            Name: "provider",
            Description: null,
            Icon: null,
            ProviderType.OpenAiImages,
            ApiKey: null,
            BaseUrl: "https://api.example/v1",
            Enabled: true,
            PresetVersion: null,
            SortOrder: 0,
            CreatedAt: Now,
            UpdatedAt: Now),
        ApiKey: "sk-test",
        ModelKey: "image-model",
        Prompt: "a portrait",
        ReferenceImages: [],
        Mask: null,
        Size: null,
        Quality: null,
        OutputFormat: "png",
        ResponseFormat: "url",
        Count: 1,
        Seed: null);

    /// <summary>初始化 ImageHandler，保留既有依赖、配置校验与资源生命周期。</summary>
    private sealed class ImageHandler(string sourceUrl) : HttpMessageHandler
    {
        /// <summary>协议客户端发出的 HTTP 请求总次数。</summary>
        public int Requests { get; private set; }
        /// <summary>下载图片请求携带的认证头，用于验证不向素材地址泄露渠道凭据。</summary>
        public string? DownloadAuthorization { get; private set; }

        /// <summary>统计请求次数；提交时返回预设图片 URL，下载时记录鉴权头并返回固定 PNG 字节。</summary>
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests++;
            if (request.Method == HttpMethod.Post)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        $$"""{ "data": [{ "url": "{{sourceUrl}}" }] }""",
                        Encoding.UTF8,
                        "application/json"),
                });
            }

            DownloadAuthorization = request.Headers.Authorization?.ToString();
            var content = new ByteArrayContent([1, 2, 3]);
            content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
            });
        }
    }

    /// <summary>初始化 TestHttpClientFactory，保留既有依赖、配置校验与资源生命周期。</summary>
    private sealed class TestHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        /// <summary>当前组件复用的客户端实例。</summary>
        private readonly HttpClient _client = new(handler);

        /// <summary>返回使用合成图片响应处理器的共享客户端，不访问真实上游。</summary>
        public HttpClient CreateClient(string name) => _client;
    }

    private sealed class ClockStub : IClock
    {
        /// <summary>返回图片来源 URL 测试预设的当前时间。</summary>
        public DateTimeOffset UtcNow => Now;
    }
}
