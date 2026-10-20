using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Infrastructure.Ai.Protocols;

/// <summary>
/// 通过 <c>generateContent</c> 进行 Gemini 图片生成。
/// </summary>
/// <remarks>
/// Gemini 没有专用图片端点：图片模型与聊天模型调用方式相同，图片会与文本一同作为
/// <c>inlineData</c> 部分返回。参考图是同一用户轮次中的附加部分，
/// 因此编辑和生成共用一种请求结构，而不是两种。
/// <para>
/// 它也没有 <c>n</c> 参数；需要多张图片时由调用方循环。
/// </para>
/// </remarks>
internal sealed class GeminiImagesClient(
    IHttpClientFactory httpClientFactory,
    ILogger<GeminiImagesClient> logger) : IImageGenerationClient
{
    /// <summary>标识当前图片客户端使用 Gemini generateContent 协议。</summary>
    public ProviderType Protocol => ProviderType.Gemini;

    /// <summary>按请求张数串行生成 Gemini 图片，后续请求失败时保留此前已成功返回的图片。</summary>
    public async Task<ImageGenerationResult> GenerateAsync(
        ImageGenerationRequest request,
        CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient("upstream");
        var baseUrl = request.Provider.BaseUrl.TrimEnd('/');
        var url = $"{baseUrl}/v1beta/models/{request.ModelKey}:generateContent";

        var images = new List<GeneratedImage>();

        // 使用串行而非并行：请求共享同一 API 密钥和配额，突发并发调用最容易触发限流。
        for (var i = 0; i < request.Count; i++)
        {
            var result = await GenerateOneAsync(client, url, request, ct);

            if (!result.Ok)
            {
                // 已生成的任何内容都值得返回；部分结果优于没有结果。
                return images.Count > 0
                    ? ImageGenerationResult.Success(images)
                    : result;
            }

            images.AddRange(result.Images);
        }

        return images.Count > 0
            ? ImageGenerationResult.Success(images)
            : ImageGenerationResult.Failure(null, "Upstream returned no image data.");
    }

    /// <summary>按 Gemini 图片协议完成一次生成并解析返回图片。</summary>
    private async Task<ImageGenerationResult> GenerateOneAsync(
        HttpClient client,
        string url,
        ImageGenerationRequest request,
        CancellationToken ct)
    {
        var parts = new JsonArray();

        // 参考图放在指令之前，这与 Gemini 官方编辑示例的顺序一致。
        foreach (var reference in request.ReferenceImages)
        {
            parts.Add((JsonNode)new JsonObject
            {
                ["inlineData"] = new JsonObject
                {
                    ["mimeType"] = reference.MediaType,
                    ["data"] = Convert.ToBase64String(reference.Bytes),
                },
            });
        }

        parts.Add((JsonNode)new JsonObject { ["text"] = request.Prompt });

        var body = new JsonObject
        {
            ["contents"] = new JsonArray(
                new JsonObject { ["role"] = "user", ["parts"] = parts }),
            // 若无此设置，模型可能返回文字说明而不是图片。
            ["generationConfig"] = new JsonObject
            {
                ["responseModalities"] = new JsonArray("IMAGE", "TEXT"),
            },
        };

        if (request.Seed is { } seed)
        {
            ((JsonObject)body["generationConfig"]!)["seed"] = seed;
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, url);
        message.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        message.Headers.TryAddWithoutValidation("x-goog-api-key", request.ApiKey);

        try
        {
            using var response = await client.SendAsync(message, ct);
            var text = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogInformation(
                    "Gemini image generation failed with {StatusCode} for model {Model}",
                    (int)response.StatusCode,
                    request.ModelKey);

                return ImageGenerationResult.Failure(
                    (int)response.StatusCode, OpenAiImagesClient.ExtractError(text));
            }

            return ImageGenerationResult.Success(ParseImages(text));
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return ImageGenerationResult.Failure(null, "Upstream request timed out.");
        }
        catch (HttpRequestException exception)
        {
            return ImageGenerationResult.Failure(null, exception.Message);
        }
        catch (JsonException)
        {
            return ImageGenerationResult.Failure(null, "Upstream returned malformed JSON.");
        }
    }

    /// <summary>从 Gemini 响应中提取图片字节与媒体类型。</summary>
    private static List<GeneratedImage> ParseImages(string body)
    {
        using var document = JsonDocument.Parse(body);
        var images = new List<GeneratedImage>();

        if (!document.RootElement.TryGetProperty("candidates", out var candidates)
            || candidates.ValueKind != JsonValueKind.Array)
        {
            return images;
        }

        foreach (var candidate in candidates.EnumerateArray())
        {
            if (!candidate.TryGetProperty("content", out var content)
                || !content.TryGetProperty("parts", out var parts)
                || parts.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var part in parts.EnumerateArray())
            {
                // REST API 使用驼峰命名，部分代理使用下划线命名；两者均接受，
                // 避免通过中继调用时静默返回空结果。
                if (!TryGetInlineData(part, out var inline)) continue;

                var data = ReadString(inline, "data");
                if (string.IsNullOrEmpty(data)) continue;

                var mediaType = ReadString(inline, "mimeType")
                    ?? ReadString(inline, "mime_type")
                    ?? "image/png";

                try
                {
                    images.Add(new GeneratedImage(Convert.FromBase64String(data), mediaType));
                }
                catch (FormatException)
                {
                    // 跳过格式错误的部分，保留其余内容。
                }
            }
        }

        return images;
    }

    /// <summary>兼容协议支持的字段形式，读取内联图片数据。</summary>
    private static bool TryGetInlineData(JsonElement part, out JsonElement inline) =>
        part.TryGetProperty("inlineData", out inline)
        || part.TryGetProperty("inline_data", out inline);

    /// <summary>从 JSON 或配置读取字符串字段，保留缺失及类型错误时的原有回退。</summary>
    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
