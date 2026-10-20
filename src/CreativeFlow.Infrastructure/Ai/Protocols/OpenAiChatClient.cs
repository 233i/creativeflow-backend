using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Infrastructure.Ai.Protocols;

/// <summary>
/// 兼容 OpenAI 的聊天补全接口（<c>POST /chat/completions</c>）。
/// </summary>
/// <remarks>
/// 使用最广泛的协议——DeepSeek、SiliconFlow、Volcengine、OpenRouter 和大多数中继都接受此结构，
/// 因此基础 URL 来自供应商配置。
/// </remarks>
internal sealed class OpenAiChatClient(
    IHttpClientFactory httpClientFactory,
    ILogger<OpenAiChatClient> logger) : ITextGenerationClient
{
    /// <summary>标识当前文本客户端使用 OpenAI Chat Completions 协议。</summary>
    public ProviderType Protocol => ProviderType.OpenAiChat;

    /// <summary>将系统提示词、用户文本和参考图片转换为聊天请求，并提取上游返回的文本结果。</summary>
    public async Task<TextGenerationResult> GenerateAsync(
        TextGenerationRequest request,
        CancellationToken ct)
    {
        var baseUrl = request.Provider.BaseUrl.TrimEnd('/');
        var messages = new JsonArray();

        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            messages.Add((JsonNode)new JsonObject
            {
                ["role"] = "system",
                ["content"] = request.SystemPrompt,
            });
        }

        // 没有图片时使用普通字符串：尽管官方 API 接受多部分内容数组，
        // 部分兼容 OpenAI 的中继仍会拒绝它。
        JsonNode content;
        if (request.ReferenceImages.Count == 0)
        {
            content = JsonValue.Create(request.Prompt)!;
        }
        else
        {
            var parts = new JsonArray();
            foreach (var image in request.ReferenceImages)
            {
                parts.Add((JsonNode)new JsonObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JsonObject
                    {
                        ["url"] =
                            $"data:{image.MediaType};base64,{Convert.ToBase64String(image.Bytes)}",
                    },
                });
            }

            parts.Add((JsonNode)new JsonObject { ["type"] = "text", ["text"] = request.Prompt });
            content = parts;
        }

        messages.Add((JsonNode)new JsonObject { ["role"] = "user", ["content"] = content });

        var body = new JsonObject
        {
            ["model"] = request.ModelKey,
            ["messages"] = messages,
        };

        if (request.MaxOutputTokens is { } maxTokens) body["max_tokens"] = maxTokens;
        if (request.Temperature is { } temperature) body["temperature"] = temperature;

        if (request.ResponseFormat is not null) body["response_format"] = request.ResponseFormat.DeepClone();

        if (request.OnTextDelta is not null)
            return await TextStreamProtocol.SendAsync(httpClientFactory, request, Protocol, $"{baseUrl}/chat/completions", body,
                message => message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey), ct);

        return await TextProtocolHelpers.SendAsync(
            httpClientFactory,
            logger,
            $"{baseUrl}/chat/completions",
            body,
            message =>
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey),
            ExtractText,
            ct, CaptureMetadata, request.HttpClientName);
    }

    /// <summary>只提取上游报告的用量、请求标识和结束原因；不从文本长度推算 token。</summary>
    private static TextGenerationResult CaptureMetadata(JsonElement root, TextGenerationResult result)
    {
        var usage = new JsonObject();
        if (root.TryGetProperty("usage", out var reported) && reported.ValueKind == JsonValueKind.Object)
            foreach (var key in new[] { "prompt_tokens", "completion_tokens", "total_tokens" })
                if (reported.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var count) && count >= 0) usage[key] = count;
        string? finish = null;
        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0
            && choices[0].TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String) finish = reason.GetString();
        var id = root.TryGetProperty("id", out var identifier) && identifier.ValueKind == JsonValueKind.String ? identifier.GetString() : null;
        return result with { Usage = usage.Count > 0 ? usage : null, FinishReason = finish is { Length: <= 100 } ? finish : null,
            UpstreamRequestId = id is { Length: <= 200 } ? id : null };
    }

    /// <summary>从第一个选项中读取助手消息。</summary>
    private static string? ExtractText(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
        {
            return null;
        }

        var message = choices[0];
        if (!message.TryGetProperty("message", out var inner)) return null;

        if (inner.TryGetProperty("content", out var text) && text.ValueKind == JsonValueKind.String)
        {
            return text.GetString();
        }

        // 推理模型可能将内容作为带类型的部分数组返回。
        if (text.ValueKind == JsonValueKind.Array)
        {
            var builder = new StringBuilder();
            foreach (var part in text.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var partText)
                    && partText.ValueKind == JsonValueKind.String)
                {
                    builder.Append(partText.GetString());
                }
            }

            return builder.Length > 0 ? builder.ToString() : null;
        }

        return null;
    }
}
