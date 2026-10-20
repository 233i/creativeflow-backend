using System.Text.Json.Nodes;
using CreativeFlow.Application.Features.Workflow;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Infrastructure.Ai.Protocols;

namespace CreativeFlow.UnitTests.Ai;

/// <summary>验证旧工作流的图片顺序及新增交换配置进入原生和中转请求后的实际角色。</summary>
public sealed class WorkflowVideoInputPolicyTests
{
    /// <summary>缺省或关闭交换时，旧首尾帧和参考图输入顺序保持一致。</summary>
    [Theory]
    [InlineData("reference", false)]
    [InlineData("reference", true)]
    [InlineData("first-last", false)]
    public void KeepsLegacyOrder(string mode, bool reverse)
    {
        string[] images = ["https://image.example/a.png", "https://image.example/b.png"];
        var result = WorkflowVideoInputPolicy.Prepare(mode, reverse, images);
        Assert.Null(result.Error);
        Assert.Same(images, result.Images);
    }

    /// <summary>交换只对恰好两张图片生效，并且不改变上游输出。</summary>
    [Fact]
    public void SwapsTwoFramesWithoutMutatingSources()
    {
        string[] images = ["first", "last"];
        Assert.Equal(["last", "first"], WorkflowVideoInputPolicy.Prepare("first-last", true, images).Images);
        Assert.Equal(["first", "last"], images);
        Assert.Equal(["first"], WorkflowVideoInputPolicy.Prepare("first-last", true, ["first"]).Images);
        Assert.Empty(WorkflowVideoInputPolicy.Prepare("first-last", true, []).Images);
    }

    /// <summary>在素材加载与截断之前拒绝首尾帧超限，参考图仍保留既有交由读取层截断的行为。</summary>
    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    public void RejectsTooManyFramesBeforeReadingAssets(int count)
    {
        var images = Enumerable.Range(0, count).Select(index => index.ToString()).ToArray();
        Assert.NotNull(WorkflowVideoInputPolicy.Prepare("first-last", false, images).Error);
        Assert.Null(WorkflowVideoInputPolicy.Prepare("reference", true, images).Error);
    }

    /// <summary>同一工作流配置在原生 Seedance 与 OpenAI 中转路由中产生一致的帧角色和 URL 顺序。</summary>
    [Theory]
    [InlineData("reference", false)]
    [InlineData("reference", true)]
    [InlineData("first-last", false)]
    [InlineData("first-last", true)]
    public void PreservesRolesThroughNativeAndCompatibilityRequests(string mode, bool reverse)
    {
        string[] urls = ["https://image.example/first.png", "https://image.example/last.png"];
        var prepared = WorkflowVideoInputPolicy.Prepare(mode, reverse, urls);
        var request = Request(mode, prepared.Images);
        var native = Assert.IsType<JsonArray>(SeedanceVideoClient.BuildRequestBody(request)["content"]);
        var compatibility = Assert.IsType<JsonArray>(OpenAiVideoClient.BuildRequestBody(request)["metadata"]?["content"]);
        var expected = mode == "first-last" && reverse ? urls.Reverse().ToArray() : urls;
        for (var index = 0; index < 2; index++)
        {
            var role = mode == "reference" ? "reference_image" : index == 0 ? "first_frame" : "last_frame";
            Assert.Equal(role, (string?)native[index]?["role"]);
            Assert.Equal(role, (string?)compatibility[index]?["role"]);
            Assert.Equal(expected[index], (string?)native[index]?["image_url"]?["url"]);
            Assert.Equal(expected[index], (string?)compatibility[index]?["image_url"]?["url"]);
        }
    }

    /// <summary>构造不访问外网的 Seedance 2.0 Fast 请求，以检查两个协议的真实序列化代码。</summary>
    private static VideoGenerationRequest Request(string mode, IReadOnlyList<string> urls) => new(
        new AiProvider(ProviderId.New(), null, "Relay", null, null, ProviderType.OpenAiVideo,
            null, "https://relay.example/v1", true, null, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
        "test-key", "doubao-seedance-2-0-fast-260128", "move forward",
        urls.Select(url => new ReferenceImage([1], "image/png", url)).ToArray(),
        mode, "16:9", "720p", 5, null, null, null, null);
}
