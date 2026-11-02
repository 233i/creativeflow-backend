using System.Text;
using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Infrastructure.Ai.Protocols;

/// <summary>Seedream 文生图与参考图都使用 JSON generations；复用图片结果解析、下载鉴权与检查点交付。</summary>
internal sealed class SeedreamImagesClient(OpenAiImagesClient transport) : IImageGenerationClient
{
    /// <summary>原生 Seedream 请求协议。</summary>
    public ProviderType Protocol => ProviderType.SeedreamImages;

    /// <summary>按用户请求张数串行生成；每张只发一次，失败停止，已返回的图片保留供保存。</summary>
    public async Task<ImageGenerationResult> GenerateAsync(ImageGenerationRequest request, CancellationToken ct)
    {
        if (Validate(request) is { } error) return ImageGenerationResult.Failure(null, error);
        var images = new List<GeneratedImage>();
        // Pro/Flash 不支持组图或 n；串行单图请求遵循已有 Gemini 的数量及部分成功语义。
        for (var i = 0; i < request.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            using var message = BuildRequest(request);
            var result = await transport.SendAsync(message, request, ct);
            if (!result.Ok) return images.Count > 0 ? ImageGenerationResult.Success(images) : result;
            images.AddRange(result.Images);
        }
        return ImageGenerationResult.Success(images);
    }

    /// <summary>参考素材使用已通过归属校验的字节，不把私有站内地址交给上游访问。</summary>
    internal static HttpRequestMessage BuildRequest(ImageGenerationRequest request)
    {
        var body = new JsonObject
        {
            ["model"] = request.ModelKey,
            ["prompt"] = request.Prompt,
            ["response_format"] = OpenAiImagesClient.ResolveResponseFormat(request.ResponseFormat),
        };
        if (!string.IsNullOrWhiteSpace(request.Size)) body["size"] = request.Size;
        if (SupportsOutputFormat(request.ModelKey))
            body["output_format"] = OpenAiImagesClient.ResolveOutputFormat(request.OutputFormat);
        if (request.ReferenceImages.Count > 0)
        {
            var references = request.ReferenceImages.Select(image => (JsonNode?)JsonValue.Create(
                $"data:{image.MediaType.ToLowerInvariant()};base64,{Convert.ToBase64String(image.Bytes)}")).ToArray();
            body["image"] = references.Length == 1 ? references[0] : new JsonArray(references);
        }
        // quality、seed、n 是其他模型的字段；不要透传旧节点残留值导致新的 400。
        var baseUrl = AiProvider.NormalizeBaseUrl(request.Provider.BaseUrl, ProviderType.SeedreamImages);
        return new(HttpMethod.Post, baseUrl + "/images/generations")
        { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
    }

    /// <summary>仅官方支持该字段的 5.0 Pro/Flash 型号传输输出编码。</summary>
    internal static bool SupportsOutputFormat(string model) =>
        model.Replace('.', '-').Contains("seedream-5-0-pro", StringComparison.OrdinalIgnoreCase)
        || model.Replace('.', '-').Contains("seedream-5-0-flash", StringComparison.OrdinalIgnoreCase);

    /// <summary>使用 2K 合法尺寸保留完整宽高比，适用于 Pro/Flash、Lite 和 4.x 的共同像素区间。</summary>
    internal static string SizeForAspect(string aspect) => aspect switch
    {
        "16:9" => "2816x1584", "9:16" => "1584x2816",
        "4:3" => "2304x1728", "3:4" => "1728x2304",
        "3:2" => "2496x1664", "2:3" => "1664x2496", _ => "2048x2048",
    };

    /// <summary>不适用的图片输入在发出任何收费请求前失败；不修改素材权限或自动忽略参考图。</summary>
    private static string? Validate(ImageGenerationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt)) return "Seedream 图片生成需要填写提示词。";
        if (request.Mask is not null) return "Seedream 图片协议不支持 OpenAI 蒙版，请使用参考图和提示词描述编辑位置。";
        if (request.Count is < 1 or > 4) return "Seedream 单次任务支持生成 1–4 张图片。";
        if (request.ReferenceImages.Count > 8) return "最多支持 8 张参考图。";
        if (SupportsOutputFormat(request.ModelKey) && request.OutputFormat?.Equals("webp", StringComparison.OrdinalIgnoreCase) == true)
            return "Seedream 不支持 WebP 输出，请选择 PNG 或 JPEG。";
        foreach (var image in request.ReferenceImages)
        {
            if (image.Bytes.Length is 0 or > 30 * 1024 * 1024) return "Seedream 参考图必须是非空且不超过 30 MiB 的图片。";
            if (image.MediaType.ToLowerInvariant() is not ("image/png" or "image/jpeg" or "image/webp" or "image/bmp" or "image/tiff" or "image/gif" or "image/heic" or "image/heif"))
                return "Seedream 不支持该参考图格式，请使用 PNG、JPEG 或 WebP 等受支持格式。";
        }
        return null;
    }
}
