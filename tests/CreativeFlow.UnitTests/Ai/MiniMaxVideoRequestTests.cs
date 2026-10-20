using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;
using CreativeFlow.Infrastructure.Ai.Protocols;

namespace CreativeFlow.UnitTests.Ai;

public sealed class MiniMaxVideoRequestTests
{
    /// <summary>回归场景：Uses Nested H3 Metadata For The First Connected Image。</summary>
    [Fact]
    public void UsesNestedH3MetadataForTheFirstConnectedImage()
    {
        var body = MiniMaxVideoClient.BuildRequestBody(Request(
        [
            new ReferenceImage(
                [1, 2, 3],
                "image/png",
                "https://vendor.example/first.png",
                "https://creative.example/first.png"),
            new ReferenceImage(
                [4, 5, 6],
                "image/jpeg",
                "https://vendor.example/ignored.jpg",
                "https://creative.example/ignored.jpg"),
        ]));

        Assert.Equal("MiniMax-H3", (string?)body["model"]);
        Assert.Equal("move forward", (string?)body["prompt"]);
        Assert.Equal(7, (int?)body["duration"]);
        Assert.Equal("https://vendor.example/first.png", (string?)body["input_reference"]);
        Assert.Null(body["first_frame_image"]);
        Assert.Null(body["size"]);

        var metadata = Assert.IsType<System.Text.Json.Nodes.JsonObject>(body["metadata"]);
        Assert.Equal("768P", (string?)metadata["resolution"]);
        Assert.Equal("adaptive", (string?)metadata["ratio"]);
        Assert.Equal("https://vendor.example/first.png", (string?)metadata["first_frame_image"]);
    }

    /// <summary>回归场景：Text To Video Uses The Requested Ratio。</summary>
    [Fact]
    public void TextToVideoUsesTheRequestedRatio()
    {
        var body = MiniMaxVideoClient.BuildRequestBody(Request([]));
        var metadata = Assert.IsType<System.Text.Json.Nodes.JsonObject>(body["metadata"]);

        Assert.Null(metadata["first_frame_image"]);
        Assert.Equal("16:9", (string?)metadata["ratio"]);
    }

    /// <summary>回归场景：Higher Resolution Maps To H3 Two K。</summary>
    [Fact]
    public void HigherResolutionMapsToH3TwoK()
    {
        var body = MiniMaxVideoClient.BuildRequestBody(Request([]) with { Resolution = "1080p" });
        var metadata = Assert.IsType<System.Text.Json.Nodes.JsonObject>(body["metadata"]);

        Assert.Equal("2K", (string?)metadata["resolution"]);
    }

    /// <summary>回归场景：Submit Request Uses Json Instead Of Multipart。</summary>
    [Fact]
    public async Task SubmitRequestUsesJsonInsteadOfMultipart()
    {
        using var message = MiniMaxVideoClient.BuildSubmitRequest(
            "https://example.com/v1",
            Request(
            [
                new ReferenceImage(
                    [9, 8, 7],
                    "image/webp",
                    "https://vendor.example/frame.webp",
                    "https://creative.example/frame.webp"),
            ]));

        Assert.Equal("https://example.com/v1/videos", message.RequestUri?.ToString());
        Assert.Equal("application/json", message.Content?.Headers.ContentType?.MediaType);
        Assert.Contains(
            "https://vendor.example/frame.webp",
            await message.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>构造带关联 id 的 MCP JSON-RPC 请求对象。</summary>
    private static VideoGenerationRequest Request(IReadOnlyList<ReferenceImage> images) => new(
        new AiProvider(
            ProviderId.New(),
            BuiltinId: null,
            Name: "provider",
            Description: null,
            Icon: null,
            ProviderType.OpenAiChat,
            ApiKey: null,
            BaseUrl: "https://example.com/v1",
            Enabled: true,
            PresetVersion: null,
            SortOrder: 0,
            CreatedAt: DateTimeOffset.UnixEpoch,
            UpdatedAt: DateTimeOffset.UnixEpoch),
        ApiKey: "sk-test",
        ModelKey: "MiniMax-H3",
        Prompt: "move forward",
        ReferenceImages: images,
        InputMode: "reference",
        Aspect: "16:9",
        Resolution: "720p",
        DurationSeconds: 7,
        Fps: null,
        Seed: null,
        Watermark: null,
        GenerateAudio: null);
}
