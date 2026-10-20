using CreativeFlow.Infrastructure.Ai;

namespace CreativeFlow.UnitTests.Ai;

public sealed class VideoDownloadAuthorizationTests
{
    /// <summary>回归场景：Same Origin Provider Content Uses Bearer Token。</summary>
    [Fact]
    public void SameOriginProviderContentUsesBearerToken()
    {
        using var request = VideoJobPoller.BuildDownloadRequest(
            "https://ai-proxy.westmoon.com/v1/videos/task-1/content",
            "https://ai-proxy.westmoon.com/v1",
            "sk-test");

        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("sk-test", request.Headers.Authorization?.Parameter);
    }

    /// <summary>回归场景：Explicit Default Port Is Still The Same Origin。</summary>
    [Fact]
    public void ExplicitDefaultPortIsStillTheSameOrigin()
    {
        using var request = VideoJobPoller.BuildDownloadRequest(
            "https://ai-proxy.westmoon.com:443/v1/videos/task-1/content",
            "https://ai-proxy.westmoon.com/v1",
            "sk-test");

        Assert.NotNull(request.Headers.Authorization);
    }

    /// <summary>回归场景：Cross Origin Content Does Not Receive Provider Token。</summary>
    [Theory]
    [InlineData("https://cdn.westmoon.com/result.mp4")]
    [InlineData("https://ai-proxy.westmoon.com.attacker.example/result.mp4")]
    [InlineData("https://ai-proxy.westmoon.com:8443/result.mp4")]
    [InlineData("http://ai-proxy.westmoon.com/result.mp4")]
    public void CrossOriginContentDoesNotReceiveProviderToken(string downloadUrl)
    {
        using var request = VideoJobPoller.BuildDownloadRequest(
            downloadUrl,
            "https://ai-proxy.westmoon.com/v1",
            "sk-secret");

        Assert.Null(request.Headers.Authorization);
    }
}
