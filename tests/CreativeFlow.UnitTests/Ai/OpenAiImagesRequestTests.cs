using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;
using CreativeFlow.Infrastructure.Ai.Protocols;

namespace CreativeFlow.UnitTests.Ai;

/// <summary>
/// <see cref="OpenAiImagesClient"/> 组织的请求正文。
/// </summary>
/// <remarks>
/// 针对已构建的 <see cref="HttpRequestMessage"/> 而非网络传输进行断言：关键在于发送哪些字段及其名称。
/// 某个参数若静默未进入正文，只有用户发现控件不起作用时才会暴露。
/// </remarks>
public class OpenAiImagesRequestTests
{
    /// <summary>调用方配置并规范化后的上游基础地址。</summary>
    private const string BaseUrl = "https://example.com/v1";

    /// <summary>回归场景：A Generate Request Asks For Urls And Png By Default。</summary>
    [Fact]
    public async Task AGenerateRequestAsksForUrlsAndPngByDefault()
    {
        // 未表达偏好的调用方会得到画布默认值。URL 最为关键：绝大多数中继以链接响应，
        // 而旧请求正文完全遗漏了该字段。
        var body = await GenerateBodyAsync(Request());

        Assert.Equal("url", (string?)body["response_format"]);
        Assert.Equal("png", (string?)body["output_format"]);
    }

    /// <summary>回归场景：Base64 Is Sent Under The Name The Api Uses。</summary>
    [Fact]
    public async Task Base64IsSentUnderTheNameTheApiUses()
    {
        // "base64" 是应用层名称，API 只识别 b64_json。
        var body = await GenerateBodyAsync(Request(responseFormat: "base64"));

        Assert.Equal("b64_json", (string?)body["response_format"]);
    }

    /// <summary>回归场景：An Output Format Is Normalized Rather Than Passed Through。</summary>
    [Theory]
    [InlineData("jpeg", "jpeg")]
    [InlineData("jpg", "jpeg")]
    [InlineData("WebP", "webp")]
    [InlineData("tiff", "png")]
    [InlineData("", "png")]
    public async Task AnOutputFormatIsNormalizedRatherThanPassedThrough(
        string requested,
        string expected)
    {
        // 使用白名单，使未识别值回退到 API 自身默认值，
        // 而不是返回在用户看来像模型故障的 400。
        var body = await GenerateBodyAsync(Request(outputFormat: requested));

        Assert.Equal(expected, (string?)body["output_format"]);
    }

    /// <summary>回归场景：An Edit Request Carries Quality Format And Response Format。</summary>
    [Fact]
    public async Task AnEditRequestCarriesQualityFormatAndResponseFormat()
    {
        // 此测试覆盖的回归：编辑端点过去收不到这些参数，
        // 因此一接入参考图，节点的所有控件都会失效。
        var fields = await EditFieldsAsync(Request(
            quality: "high",
            outputFormat: "jpg",
            responseFormat: "base64",
            withReference: true));

        Assert.Equal("high", fields["quality"]);
        Assert.Equal("jpeg", fields["output_format"]);
        Assert.Equal("b64_json", fields["response_format"]);
    }

    /// <summary>回归场景：Auto Quality Is Omitted From Both Shapes。</summary>
    [Fact]
    public async Task AutoQualityIsOmittedFromBothShapes()
    {
        // "auto" 在节点中表示“不发送该参数”，并不是 API 接受的值。
        var body = await GenerateBodyAsync(Request(quality: "auto"));
        Assert.Null(body["quality"]);

        var fields = await EditFieldsAsync(Request(quality: "auto", withReference: true));
        Assert.False(fields.ContainsKey("quality"));
    }

    /// <summary>回归场景：Reference Images Still Travel As File Parts。</summary>
    [Fact]
    public void ReferenceImagesStillTravelAsFileParts()
    {
        // 新增字符串部分不能挤掉它们。
        using var message = OpenAiImagesClient.BuildEditRequest(BaseUrl, Request(withReference: true));

        var files = Assert.IsType<MultipartFormDataContent>(message.Content)
            .Where(part => part.Headers.ContentDisposition?.FileName is not null)
            .ToList();

        Assert.Single(files);
        Assert.Equal("image[]", files[0].Headers.ContentDisposition?.Name);
    }

    /// <summary>回归场景：An Inpaint Mask Uses The Dedicated Png Part。</summary>
    [Fact]
    public void AnInpaintMaskUsesTheDedicatedPngPart()
    {
        using var message = OpenAiImagesClient.BuildEditRequest(
            BaseUrl,
            Request(withReference: true, withMask: true));

        var files = Assert.IsType<MultipartFormDataContent>(message.Content)
            .Where(part => part.Headers.ContentDisposition?.FileName is not null)
            .ToList();

        Assert.Equal(2, files.Count);
        var mask = Assert.Single(files, part => part.Headers.ContentDisposition?.Name == "mask");
        Assert.Equal("mask.png", mask.Headers.ContentDisposition?.FileName);
        Assert.Equal("image/png", mask.Headers.ContentType?.MediaType);
    }

    /// <summary>回归场景：The Two Shapes Target Different Endpoints。</summary>
    [Fact]
    public void TheTwoShapesTargetDifferentEndpoints()
    {
        using var generate = OpenAiImagesClient.BuildGenerateRequest(BaseUrl, Request());
        using var edit = OpenAiImagesClient.BuildEditRequest(BaseUrl, Request(withReference: true));

        Assert.Equal($"{BaseUrl}/images/generations", generate.RequestUri?.ToString());
        Assert.Equal($"{BaseUrl}/images/edits", edit.RequestUri?.ToString());
    }

    /// <summary>调用图片客户端并捕获实际发送的请求体，供兼容性断言使用。</summary>
    private static async Task<JsonNode> GenerateBodyAsync(ImageGenerationRequest request)
    {
        using var message = OpenAiImagesClient.BuildGenerateRequest(BaseUrl, request);
        var json = await message.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken);

        return JsonNode.Parse(json)!;
    }

    /// <summary>读取多部分正文中的字符串部分，并忽略参考图文件。</summary>
    private static async Task<Dictionary<string, string>> EditFieldsAsync(
        ImageGenerationRequest request)
    {
        using var message = OpenAiImagesClient.BuildEditRequest(BaseUrl, request);
        var parts = Assert.IsType<MultipartFormDataContent>(message.Content);

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var part in parts)
        {
            var disposition = part.Headers.ContentDisposition;

            // 带文件名的部分是参考图，而不是参数。
            if (disposition?.Name is not { } name || disposition.FileName is not null) continue;

            fields[name] = await part.ReadAsStringAsync(TestContext.Current.CancellationToken);
        }

        return fields;
    }

    /// <summary>回归场景：Request。</summary>
    private static ImageGenerationRequest Request(
        string? quality = null,
        string? outputFormat = null,
        string? responseFormat = null,
        bool withReference = false,
        bool withMask = false) => new(
            Provider(),
            ApiKey: "sk-test",
            ModelKey: "gpt-image-1",
            Prompt: "a cat",
            withReference ? [new ReferenceImage([1, 2, 3], "image/png")] : [],
            Mask: withMask ? new ReferenceImage([4, 5, 6], "image/png") : null,
            Size: "1024x1024",
            quality,
            outputFormat,
            responseFormat,
            Count: 1,
            Seed: null);

    /// <summary>构造当前协议测试使用的渠道配置。</summary>
    private static AiProvider Provider() => new(
        ProviderId.New(),
        BuiltinId: null,
        Name: "p",
        Description: null,
        Icon: null,
        ProviderType.OpenAiImages,
        ApiKey: null,
        BaseUrl: BaseUrl,
        Enabled: true,
        PresetVersion: null,
        SortOrder: 0,
        CreatedAt: DateTimeOffset.UnixEpoch,
        UpdatedAt: DateTimeOffset.UnixEpoch);
}
