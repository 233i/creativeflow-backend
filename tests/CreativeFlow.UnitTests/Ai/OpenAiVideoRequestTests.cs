using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;
using CreativeFlow.Infrastructure.Ai.Protocols;

namespace CreativeFlow.UnitTests.Ai;

public class OpenAiVideoRequestTests
{
   /// <summary>回归场景：Sd Models Use The Resolution Encoded In Their Model Key。</summary>
    [Theory]
    [InlineData("sd-mini-480p", "720p", "854x480")]
    [InlineData("sd-fast-720p", "480p", "1280x720")]
    [InlineData("sd-2.0-1080p", "720p", "1920x1080")]
    public void SdModelsUseTheResolutionEncodedInTheirModelKey(
        string modelKey,
        string requestedResolution,
        string expectedSize)
    {
        Assert.Equal(
            expectedSize,
            OpenAiVideoClient.SizeFor(modelKey, "16:9", requestedResolution));
    }

   /// <summary>回归场景：Fixed Resolution Still Respects Portrait Aspect。</summary>
    [Fact]
    public void FixedResolutionStillRespectsPortraitAspect()
    {
        Assert.Equal(
            "480x854",
            OpenAiVideoClient.SizeFor("sd-mini-480p", "9:16", "1080p"));
    }

   /// <summary>回归场景：Other Models Keep The Requested Resolution。</summary>
    [Fact]
    public void OtherModelsKeepTheRequestedResolution()
    {
        Assert.Equal("1920x1080", OpenAiVideoClient.SizeFor("sora-2", "16:9", "1080p"));
    }

   /// <summary>回归场景：Routin Uses Content Blocks And Structured Video Parameters。</summary>
    [Fact]
    public void RoutinUsesContentBlocksAndStructuredVideoParameters()
    {
        var body = OpenAiVideoClient.BuildRequestBody(Request(
            Provider("routin-ai"),
            "sd-mini-480p",
            [new ReferenceImage(
                [1, 2, 3],
                "image/png",
                "https://vendor.example/source.png",
                "https://creative.example/signed.png")]));

        Assert.Equal("sd-mini-480p", (string?)body["model"]);
        Assert.Equal("16:9", (string?)body["ratio"]);
        Assert.Equal("480p", (string?)body["resolution"]);
        Assert.Equal(7, (int?)body["duration"]);
        Assert.Equal("move forward", (string?)body["prompt"]);
        Assert.Null(body["size"]);

        var content = Assert.IsType<JsonArray>(body["content"]);
        Assert.Equal("image_url", (string?)content[0]?["type"]);
        Assert.Equal("reference_image", (string?)content[0]?["role"]);
        Assert.Equal(
            "https://vendor.example/source.png",
            (string?)content[0]?["image_url"]?["url"]);
        Assert.Equal("text", (string?)content[1]?["type"]);
        Assert.Equal("move forward", (string?)content[1]?["text"]);

        var fallback = OpenAiVideoClient.BuildRequestBody(Request(
            Provider("routin-ai"),
            "sd-mini-480p",
            [new ReferenceImage(
                [1, 2, 3],
                "image/png",
                "https://vendor.example/source.png",
                "https://creative.example/signed.png")]),
            useFallbackUrls: true);
        Assert.Equal(
            "https://creative.example/signed.png",
            (string?)fallback["content"]?[0]?["image_url"]?["url"]);
    }

   /// <summary>回归场景：Non Routin Providers Keep The Open Ai Request Shape。</summary>
    [Fact]
    public void NonRoutinProvidersKeepTheOpenAiRequestShape()
    {
        var body = OpenAiVideoClient.BuildRequestBody(Request(Provider(null), "sora-2", []));

        Assert.Equal("move forward", (string?)body["prompt"]);
        Assert.Equal("7", (string?)body["seconds"]);
        Assert.Equal("1280x720", (string?)body["size"]);
        Assert.Null(body["content"]);
        Assert.Null(body["resolution"]);
        Assert.Null(body["first_frame_image"]);
    }

   /// <summary>回归场景：Seedance Over Open Ai Preserves Reference Images For The New Api Adapter。</summary>
    [Fact]
    public void SeedanceOverOpenAiPreservesReferenceImagesForTheNewApiAdapter()
    {
        var body = OpenAiVideoClient.BuildRequestBody(Request(
            Provider(null),
            "doubao-seedance-2.0-fast",
            [
                new ReferenceImage(
                    [1],
                    "image/png",
                    "https://vendor.example/first.png",
                    "https://creative.example/first.png"),
                new ReferenceImage(
                    [2],
                    "image/jpeg",
                    "https://vendor.example/last.jpg",
                    "https://creative.example/last.jpg"),
            ]) with
        {
            InputMode = "first-last",
            Seed = 42,
            Watermark = false,
            GenerateAudio = true,
        });

        Assert.Equal("doubao-seedance-2.0-fast", (string?)body["model"]);
        Assert.Equal("move forward", (string?)body["prompt"]);
        Assert.Equal("7", (string?)body["seconds"]);
        Assert.Equal("1280x720", (string?)body["size"]);
        Assert.Equal(
            "https://vendor.example/first.png",
            (string?)body["input_reference"]);

        var metadata = Assert.IsType<JsonObject>(body["metadata"]);
        Assert.Equal("16:9", (string?)metadata["ratio"]);
        Assert.Equal("720p", (string?)metadata["resolution"]);
        Assert.Equal(42, (int?)metadata["seed"]);
        Assert.False((bool?)metadata["watermark"]);
        Assert.True((bool?)metadata["generate_audio"]);

        var content = Assert.IsType<JsonArray>(metadata["content"]);
        Assert.Equal("first_frame", (string?)content[0]?["role"]);
        Assert.Equal("last_frame", (string?)content[1]?["role"]);
        Assert.Equal(
            "https://vendor.example/first.png",
            (string?)content[0]?["image_url"]?["url"]);
        Assert.Equal(
            "https://vendor.example/last.jpg",
            (string?)content[1]?["image_url"]?["url"]);
    }

   /// <summary>构造带关联 id 的 MCP JSON-RPC 请求对象。</summary>
    private static VideoGenerationRequest Request(
        AiProvider provider,
        string modelKey,
        IReadOnlyList<ReferenceImage> images) => new(
        provider,
        ApiKey: "sk-test",
        modelKey,
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
    private static AiProvider Provider(string? builtinId) => new(
        ProviderId.New(),
        builtinId,
        Name: "p",
        Description: null,
        Icon: null,
        ProviderType.OpenAiVideo,
        ApiKey: null,
        BaseUrl: "https://example.com/v1",
        Enabled: true,
        PresetVersion: null,
        SortOrder: 0,
        CreatedAt: DateTimeOffset.UnixEpoch,
        UpdatedAt: DateTimeOffset.UnixEpoch);
}
