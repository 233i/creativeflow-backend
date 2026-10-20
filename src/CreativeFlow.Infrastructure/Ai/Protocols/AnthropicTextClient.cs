using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Infrastructure.Ai.Protocols;

/// <summary>
/// Anthropic 消息接口（<c>POST /v1/messages</c>）。
/// </summary>
/// <remarks>
/// 与 OpenAI 结构有三处关键差异：系统提示词是顶层字段而非消息，<c>max_tokens</c> 为必填项，
/// 图片是携带 base64 的 <c>source</c> 对象，而不是数据 URL。
/// </remarks>
internal sealed class AnthropicTextClient(
    IHttpClientFactory httpClientFactory,
    ILogger<AnthropicTextClient> logger) : ITextGenerationClient
{
    /// <summary>
    /// Anthropic 会拒绝缺少 <c>max_tokens</c> 的请求，因此即使调用方不关心，也必须提供值。
    /// 此值应足以容纳改写后的提示词。
    /// </summary>
    private const int DefaultMaxTokens = 4096;

    /// <summary>标识当前文本客户端使用 Anthropic Messages 协议。</summary>
    public ProviderType Protocol => ProviderType.Anthropic;

    /// <summary>将提示词和参考图构造为 Anthropic 消息请求，设置必需的输出预算并提取响应文本。</summary>
    public async Task<TextGenerationResult> GenerateAsync(
        TextGenerationRequest request,
        CancellationToken ct)
    {
        // 供应商可能声明为兼容 OpenAI，但该模型将协议覆盖为 Anthropic。
        // 追加 /v1/messages 前，应按解析后的协议进行规范化。
        var baseUrl = AiProvider.NormalizeBaseUrl(request.Provider.BaseUrl, ProviderType.Anthropic);

        var parts = new JsonArray();
        foreach (var image in request.ReferenceImages)
        {
            parts.Add((JsonNode)new JsonObject
            {
                ["type"] = "image",
                ["source"] = new JsonObject
                {
                    ["type"] = "base64",
                    ["media_type"] = image.MediaType,
                    ["data"] = Convert.ToBase64String(image.Bytes),
                },
            });
        }

        parts.Add((JsonNode)new JsonObject { ["type"] = "text", ["text"] = request.Prompt });

        var body = new JsonObject
        {
            ["model"] = request.ModelKey,
            ["max_tokens"] = request.MaxOutputTokens ?? DefaultMaxTokens,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "user", ["content"] = parts }),
        };

        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            body["system"] = request.SystemPrompt;
        }

        if (request.Temperature is { } temperature)
        {
            body["temperature"] = temperature;
        }

        if (request.OnTextDelta is not null)
            return await TextStreamProtocol.SendAsync(httpClientFactory, request, Protocol, $"{baseUrl}/v1/messages", body,
                message => { message.Headers.TryAddWithoutValidation("x-api-key", request.ApiKey); message.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01"); }, ct);

        return await TextProtocolHelpers.SendAsync(
            httpClientFactory,
            logger,
            $"{baseUrl}/v1/messages",
            body,
            message =>
            {
                message.Headers.TryAddWithoutValidation("x-api-key", request.ApiKey);
                message.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            },
            ExtractText,
            ct, clientName: request.HttpClientName);
    }

    /// <summary>
    /// 拼接响应中的文本块。
    /// </summary>
    /// <remarks>
    /// 内容始终是带类型的块数组；启用思考的模型会交错输出 <c>thinking</c> 和 <c>text</c> 块，
    /// 只有后者属于最终输出。
    /// </remarks>
    private static string? ExtractText(JsonElement root)
    {
        if (!root.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var builder = new StringBuilder();

        foreach (var block in content.EnumerateArray())
        {
            if (block.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String
                && type.GetString() == "text"
                && block.TryGetProperty("text", out var text)
                && text.ValueKind == JsonValueKind.String)
            {
                builder.Append(text.GetString());
            }
        }

        return builder.Length > 0 ? builder.ToString() : null;
    }
}
