using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;
using CreativeFlow.Infrastructure.Ai.Protocols;

namespace CreativeFlow.UnitTests.Ai;

/// <summary><see cref="SeedanceVideoClient"/> 发送到上游的内容块。</summary>
public class SeedanceVideoRequestTests
{
    /// <summary>调用方配置并规范化后的上游基础地址。</summary>
    private const string BaseUrl = "https://example.com/v1";

    /// <summary>回归场景：Reference Mode Labels Every Image As Reference Media。</summary>
    [Fact]
    public void ReferenceModeLabelsEveryImageAsReferenceMedia()
    {
        var body = SeedanceVideoClient.BuildRequestBody(Request(
            Reference(1, "image/png")));

        var content = Assert.IsType<JsonArray>(body["content"]);
        var image = Assert.IsType<JsonObject>(content[0]);

        Assert.Equal("image_url", (string?)image["type"]);
        Assert.Equal("reference_image", (string?)image["role"]);
        Assert.Equal(
            "https://vendor.example/1.png",
            (string?)image["image_url"]?["url"]);
    }

    /// <summary>回归场景：Reference Mode Keeps Multiple Images As Reference Media。</summary>
    [Fact]
    public void ReferenceModeKeepsMultipleImagesAsReferenceMedia()
    {
        var body = SeedanceVideoClient.BuildRequestBody(Request(
            Reference(1, "image/png"),
            Reference(2, "image/jpeg")));

        var content = Assert.IsType<JsonArray>(body["content"]);

        Assert.Equal("reference_image", (string?)content[0]?["role"]);
        Assert.Equal("reference_image", (string?)content[1]?["role"]);
        Assert.Equal("text", (string?)content[2]?["type"]);
        Assert.Equal("move forward", (string?)content[2]?["text"]);
    }

    /// <summary>回归场景：First Last Mode Uses Only The Two Frame Roles。</summary>
    [Fact]
    public void FirstLastModeUsesOnlyTheTwoFrameRoles()
    {
        var body = SeedanceVideoClient.BuildRequestBody(RequestWithMode(
            5,
            "first-last",
            Reference(1, "image/png"),
            Reference(2, "image/jpeg")));

        var content = Assert.IsType<JsonArray>(body["content"]);

        Assert.Equal("first_frame", (string?)content[0]?["role"]);
        Assert.Equal("last_frame", (string?)content[1]?["role"]);
        Assert.Equal("text", (string?)content[2]?["type"]);
        Assert.Equal("move forward", (string?)content[2]?["text"]);
    }

    /// <summary>回归场景：An Arbitrary Duration Is Forwarded Unchanged。</summary>
    [Fact]
    public void AnArbitraryDurationIsForwardedUnchanged()
    {
        var body = SeedanceVideoClient.BuildRequestBody(RequestWithMode(37, "reference"));

        Assert.Equal(37, (int?)body["duration"]);
    }

    /// <summary>回归场景：Structured Models Can Switch To The Signed Fallback Url。</summary>
    [Fact]
    public void StructuredModelsCanSwitchToTheSignedFallbackUrl()
    {
        var body = SeedanceVideoClient.BuildRequestBody(
            Request(Reference(1, "image/png")),
            useFallbackUrls: true);

        Assert.Equal(
            "https://creative.example/1.png",
            (string?)body["content"]?[0]?["image_url"]?["url"]);
    }

    /// <summary>回归场景：Seedance One Keeps Inline Data Urls。</summary>
    [Fact]
    public void SeedanceOneKeepsInlineDataUrls()
    {
        var body = SeedanceVideoClient.BuildRequestBody(RequestWithModel(
            "seedance-1.5-pro",
            new ReferenceImage([1, 2, 3], "image/png")));

        Assert.Equal(
            "data:image/png;base64,AQID",
            (string?)body["content"]?[0]?["image_url"]?["url"]);
    }

    /// <summary>回归场景：Reference。</summary>
    private static ReferenceImage Reference(byte value, string mediaType) => new(
        [value],
        mediaType,
        $"https://vendor.example/{value}.png",
        $"https://creative.example/{value}.png");

    /// <summary>回归场景：Request。</summary>
    private static VideoGenerationRequest Request(
        params ReferenceImage[] images) => RequestWithMode(5, "reference", images);

    /// <summary>回归场景：Request With Mode。</summary>
    private static VideoGenerationRequest RequestWithMode(
        int durationSeconds,
        string inputMode,
        params ReferenceImage[] images) => new(
        Provider(),
        ApiKey: "sk-test",
        ModelKey: "seedance-2.0-mini",
        Prompt: "move forward",
        images,
        InputMode: inputMode,
        Aspect: "1:1",
        Resolution: "720p",
        DurationSeconds: durationSeconds,
        Fps: 24,
        Seed: null,
        Watermark: false,
        GenerateAudio: false);

    /// <summary>回归场景：Request With Model。</summary>
    private static VideoGenerationRequest RequestWithModel(
        string modelKey,
        params ReferenceImage[] images) =>
        RequestWithMode(5, "reference", images) with { ModelKey = modelKey };

    /// <summary>构造当前协议测试使用的渠道配置。</summary>
    private static AiProvider Provider() => new(
        ProviderId.New(),
        BuiltinId: null,
        Name: "p",
        Description: null,
        Icon: null,
        ProviderType.SeedanceVideo,
        ApiKey: null,
        BaseUrl: BaseUrl,
        Enabled: true,
        PresetVersion: null,
        SortOrder: 0,
        CreatedAt: DateTimeOffset.UnixEpoch,
        UpdatedAt: DateTimeOffset.UnixEpoch);
}
