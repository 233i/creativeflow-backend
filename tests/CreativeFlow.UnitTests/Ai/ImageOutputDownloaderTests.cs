using System.Net;
using System.Net.Http.Headers;
using CreativeFlow.Infrastructure.Ai;
using CreativeFlow.UnitTests.Support;

namespace CreativeFlow.UnitTests.Ai;

public sealed class ImageOutputDownloaderTests
{
    /// <summary>验证只有服务商精确同源地址才能收到密钥。</summary>
    [Theory]
    [InlineData("https://api.example/result.png", true)]
    [InlineData("https://api.example:8443/result.png", false)]
    [InlineData("http://api.example/result.png", false)]
    [InlineData("https://cdn.example/result.png", false)]
    public async Task OnlyExactProviderOriginReceivesKey(string url, bool authenticated)
    {
        var handler = new Handler(_ => Success());
        var store = new ImageGenerationHttpFixture.MemoryAssets();
        var downloader = new ImageOutputDownloader(new Clients(handler), store);
        var image = await downloader.DownloadAsync(url, "https://api.example/v1", "test-secret", "image/png", TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 1, 2, 3 }, store.Available[image.StoragePath]);
        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, sent.Method);
        Assert.Equal(authenticated ? "Bearer test-secret" : null, sent.Authorization);
    }

    /// <summary>验证跳转外部 CDN 时丢弃认证头。</summary>
    [Fact]
    public async Task AProviderRedirectToCdnDropsAuthorization()
    {
        var handler = new Handler(uri => uri.Host == "api.example" ? new HttpResponseMessage(HttpStatusCode.Redirect)
        { Headers = { Location = new Uri("https://cdn.example/image.png") } } : Success());
        await new ImageOutputDownloader(new Clients(handler), new ImageGenerationHttpFixture.MemoryAssets()).DownloadAsync("https://api.example/result", "https://api.example/v1",
            "test-secret", "image/png", TestContext.Current.CancellationToken);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("Bearer test-secret", handler.Requests[0].Authorization);
        Assert.Null(handler.Requests[1].Authorization);
        Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
    }

    /// <summary>验证下载错误只发生 GET，不回退到生成。</summary>
    [Theory]
    [InlineData(404)]
    [InlineData(503)]
    public async Task FailedDownloadNeverFallsBackToGeneration(int status)
    {
        var handler = new Handler(_ => new HttpResponseMessage((HttpStatusCode)status));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ImageOutputDownloader(new Clients(handler), new ImageGenerationHttpFixture.MemoryAssets())
            .DownloadAsync("https://cdn.example/result", "https://api.example/v1", "", "image/png", TestContext.Current.CancellationToken));
        Assert.Equal(HttpMethod.Get, Assert.Single(handler.Requests).Method);
    }

    /// <summary>创建最小图片下载成功响应。</summary>
    private static HttpResponseMessage Success() => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new MediaTypeHeaderValue("image/png") } },
    };

    /// <summary>将测试处理器注入指定名称的下载客户端。</summary>
    private sealed class Clients(HttpMessageHandler handler) : IHttpClientFactory
    {
        /// <summary>验证客户端名称并返回不拥有共享处理器的 HttpClient。</summary>
        public HttpClient CreateClient(string name) { Assert.Equal("image-output", name); return new(handler, disposeHandler: false); }
    }

    /// <summary>使用地址驱动的响应函数模拟下载与重定向。</summary>
    private sealed class Handler(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        /// <summary>按顺序记录 HTTP 方法及实际发送的认证头。</summary>
        public List<(HttpMethod Method, string? Authorization)> Requests { get; } = [];
        /// <summary>记录请求后返回测试响应，不访问真实网络。</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.Headers.Authorization?.ToString()));
            return Task.FromResult(respond(request.RequestUri!));
        }
    }
}
