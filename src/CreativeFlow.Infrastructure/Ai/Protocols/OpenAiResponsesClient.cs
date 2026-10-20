using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Infrastructure.Ai.Protocols;

/// <summary>
/// OpenAI Responses 接口（<c>POST /responses</c>）。
/// </summary>
/// <remarks>
/// 较新的 OpenAI 接口。它与聊天补全的差异主要在命名：使用 <c>input</c> 而非 <c>messages</c>，
/// 使用 <c>instructions</c> 而非系统消息，并使用 <c>max_output_tokens</c> 以及
/// <c>input_text</c>/<c>input_image</c> 部分类型。响应是扁平的 <c>output</c> 数组，
/// 可在消息之外携带推理项。
/// </remarks>
internal sealed class OpenAiResponsesClient(
    IHttpClientFactory httpClientFactory,
    ILogger<OpenAiResponsesClient> logger) : ITextGenerationClient
{
    /// <summary>标识当前文本客户端使用 OpenAI Responses 协议。</summary>
    public ProviderType Protocol => ProviderType.OpenAiResponses;

    /// <summary>将文本与参考图提交为 Responses 输入，按原内容项规则提取最终文本或上游错误。</summary>
    public async Task<TextGenerationResult> GenerateAsync(
        TextGenerationRequest request,
        CancellationToken ct)
    {
        // Responses 输出可能包含多种内容项，只按现有规则提取文本并保留上游错误与用量信息。
        var baseUrl = request.Provider.BaseUrl.TrimEnd('/');

        var parts = new JsonArray();
        foreach (var image in request.ReferenceImages)
        {
            parts.Add((JsonNode)new JsonObject
            {
                ["type"] = "input_image",
                ["image_url"] =
                    $"data:{image.MediaType};base64,{Convert.ToBase64String(image.Bytes)}",
            });
        }

        parts.Add((JsonNode)new JsonObject
        {
            ["type"] = "input_text",
            ["text"] = request.Prompt,
        });

        var body = new JsonObject
        {
            ["model"] = request.ModelKey,
            ["input"] = new JsonArray(
                new JsonObject { ["role"] = "user", ["content"] = parts }),
        };

        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            body["instructions"] = request.SystemPrompt;
        }

        if (request.MaxOutputTokens is { } maxTokens)
        {
            body["max_output_tokens"] = maxTokens;
        }

        if (request.Temperature is { } temperature)
        {
            body["temperature"] = temperature;
        }

        if (request.OnTextDelta is not null)
            return await TextStreamProtocol.SendAsync(httpClientFactory, request, Protocol, $"{baseUrl}/responses", body,
                message => message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey), ct);

        return await TextProtocolHelpers.SendAsync(
            httpClientFactory,
            logger,
            $"{baseUrl}/responses",
            body,
            message =>
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey),
            ExtractText,
            ct, clientName: request.HttpClientName);
    }

    /// <summary>从当前文本协议响应中按顺序提取正文。</summary>
    private static string? ExtractText(JsonElement root)
    {
        // 部分部署提供扁平化便捷字段；存在时优先使用。
        if (root.TryGetProperty("output_text", out var flattened)
            && flattened.ValueKind == JsonValueKind.String)
        {
            return flattened.GetString();
        }

        if (!root.TryGetProperty("output", out var output)
            || output.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var builder = new StringBuilder();

        foreach (var item in output.EnumerateArray())
        {
            // 推理项位于同一数组中，不包含面向用户的文本。
            if (!item.TryGetProperty("type", out var itemType)
                || itemType.ValueKind != JsonValueKind.String
                || itemType.GetString() != "message")
            {
                continue;
            }

            if (!item.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var part in content.EnumerateArray())
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
