using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Infrastructure.Ai.Protocols;

/// <summary>
/// 通过 <c>generateContent</c> 进行 Gemini 文本生成。
/// </summary>
/// <remarks>
/// 使用与 <see cref="GeminiImagesClient"/> 相同的端点——Gemini 没有独立聊天接口，
/// 只有不同响应模态。聊天类模型可能合理解析为 <c>gemini</c> 协议；若无此实现便无处可分派。
/// </remarks>
internal sealed class GeminiTextClient(
    IHttpClientFactory httpClientFactory,
    ILogger<GeminiTextClient> logger) : ITextGenerationClient
{
    /// <summary>标识当前文本客户端使用 Gemini generateContent 协议。</summary>
    public ProviderType Protocol => ProviderType.Gemini;

    /// <summary>将参考图和提示词发送到 Gemini generateContent，显式限定 TEXT 响应并提取候选文本。</summary>
    public async Task<TextGenerationResult> GenerateAsync(
        TextGenerationRequest request,
        CancellationToken ct)
    {
        var baseUrl = AiProvider.NormalizeBaseUrl(request.Provider.BaseUrl, ProviderType.Gemini);
        var parts = new JsonArray();

        foreach (var image in request.ReferenceImages)
        {
            parts.Add((JsonNode)new JsonObject
            {
                ["inlineData"] = new JsonObject
                {
                    ["mimeType"] = image.MediaType,
                    ["data"] = Convert.ToBase64String(image.Bytes),
                },
            });
        }

        parts.Add((JsonNode)new JsonObject { ["text"] = request.Prompt });

        var generationConfig = new JsonObject
        {
            // 仅文本：图片客户端会在此设置 IMAGE；若不设置，支持图片的模型可能用图片响应文本请求。
            ["responseModalities"] = new JsonArray("TEXT"),
        };

        if (request.MaxOutputTokens is { } maxTokens)
        {
            generationConfig["maxOutputTokens"] = maxTokens;
        }

        if (request.Temperature is { } temperature)
        {
            generationConfig["temperature"] = temperature;
        }

        var body = new JsonObject
        {
            ["contents"] = new JsonArray(
                new JsonObject { ["role"] = "user", ["parts"] = parts }),
            ["generationConfig"] = generationConfig,
        };

        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            body["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray(new JsonObject { ["text"] = request.SystemPrompt }),
            };
        }

        if (request.OnTextDelta is not null)
            return await TextStreamProtocol.SendAsync(httpClientFactory, request, Protocol, $"{baseUrl}/v1beta/models/{Uri.EscapeDataString(request.ModelKey)}:generateContent", body,
                message => message.Headers.TryAddWithoutValidation("x-goog-api-key", request.ApiKey), ct);

        return await TextProtocolHelpers.SendAsync(
            httpClientFactory,
            logger,
            $"{baseUrl}/v1beta/models/{request.ModelKey}:generateContent",
            body,
            message => message.Headers.TryAddWithoutValidation("x-goog-api-key", request.ApiKey),
            ExtractText,
            ct, clientName: request.HttpClientName);
    }

    /// <summary>从当前文本协议响应中按顺序提取正文。</summary>
    private static string? ExtractText(JsonElement root)
    {
        if (!root.TryGetProperty("candidates", out var candidates)
            || candidates.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var builder = new StringBuilder();

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
                if (part.TryGetProperty("text", out var text)
                    && text.ValueKind == JsonValueKind.String)
                {
                    builder.Append(text.GetString());
                }
            }
        }

        return builder.Length > 0 ? builder.ToString() : null;
    }
}
