using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Infrastructure.Ai.Protocols;

/// <summary>初始化 AnthropicAgentClient 的依赖与实例状态。</summary>
internal sealed class AnthropicAgentClient(IHttpClientFactory httpClientFactory) : IAgentModelClient
{
    /// <summary>标识当前 Agent 客户端使用 Anthropic Messages 协议。</summary>
    public ProviderType Protocol => ProviderType.Anthropic;

    /// <summary>按 Anthropic 流式协议发送原历史和工具定义，解析文本、思考、工具参数分片及终止用量事件。</summary>
    public async IAsyncEnumerable<AgentModelEvent> StreamAsync(
        AgentModelRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = request.ModelKey,
            ["max_tokens"] = request.MaxOutputTokens,
            ["stream"] = true,
            ["system"] = request.SystemPrompt,
            ["messages"] = AgentProtocolJson.AnthropicMessages(request.Messages),
        };
        if (request.Tools.Count > 0) body["tools"] = AgentProtocolJson.AnthropicTools(request.Tools);

        // 供应商可能声明为兼容 OpenAI，但该模型将协议覆盖为 Anthropic。
        // 此时已存储基础 URL 通常以 /v1 结尾，而该段由 Anthropic 自行添加到消息路由。
        var baseUrl = AiProvider.NormalizeBaseUrl(request.Provider.BaseUrl, ProviderType.Anthropic);
        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            $"{baseUrl}/v1/messages")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        message.Headers.TryAddWithoutValidation("x-api-key", request.ApiKey);
        message.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");

        var client = httpClientFactory.CreateClient("upstream");
        using var response = await client.SendAsync(
            message, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            yield return new AgentModelEvent(
                AgentModelEventKind.Failed,
                Error: AgentProtocolJson.Error(errorBody, (int)response.StatusCode));
            yield break;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var toolBlocks = new Dictionary<int, PendingTool>();
        string? stopReason = null;
        int? inputTokens = null;
        int? outputTokens = null;

        await foreach (var frame in SseReader.ReadAsync(stream, ct))
        {
            if (frame.Data.Length == 0) continue;

            using var document = JsonDocument.Parse(frame.Data);
            var root = document.RootElement;
            var type = String(root, "type") ?? frame.EventName;

            switch (type)
            {
                case "message_start":
                    if (root.TryGetProperty("message", out var started)
                        && started.TryGetProperty("usage", out var usage))
                    {
                        inputTokens = Int(usage, "input_tokens");
                    }
                    break;

                case "content_block_start":
                    if (!root.TryGetProperty("index", out var startIndex)
                        || !root.TryGetProperty("content_block", out var content)) break;
                    if (String(content, "type") == "tool_use")
                    {
                        toolBlocks[startIndex.GetInt32()] = new PendingTool(
                            String(content, "id") ?? string.Empty,
                            String(content, "name") ?? string.Empty,
                            new StringBuilder());
                    }
                    break;

                case "content_block_delta":
                    if (!root.TryGetProperty("delta", out var delta)) break;
                    var deltaType = String(delta, "type");
                    if (deltaType == "text_delta" && String(delta, "text") is { } text)
                    {
                        yield return new AgentModelEvent(AgentModelEventKind.TextDelta, Text: text);
                    }
                    else if (deltaType == "thinking_delta" && String(delta, "thinking") is { } thinking)
                    {
                        yield return new AgentModelEvent(AgentModelEventKind.ThinkingDelta, Text: thinking);
                    }
                    else if (deltaType == "input_json_delta"
                             && root.TryGetProperty("index", out var deltaIndex)
                             && toolBlocks.TryGetValue(deltaIndex.GetInt32(), out var pending)
                             && String(delta, "partial_json") is { } partial)
                    {
                        pending.Json.Append(partial);
                    }
                    break;

                case "content_block_stop":
                    if (root.TryGetProperty("index", out var stopIndex)
                        && toolBlocks.Remove(stopIndex.GetInt32(), out var tool))
                    {
                        var json = tool.Json.Length == 0 ? "{}" : tool.Json.ToString();
                        try
                        {
                            using var _ = JsonDocument.Parse(json);
                        }
                        catch (JsonException)
                        {
                            json = "{}";
                        }

                        yield return new AgentModelEvent(
                            AgentModelEventKind.ToolUse,
                            ToolUseId: tool.Id,
                            ToolName: tool.Name,
                            Json: json);
                    }
                    break;

                case "message_delta":
                    if (root.TryGetProperty("delta", out var messageDelta))
                    {
                        stopReason = String(messageDelta, "stop_reason") ?? stopReason;
                    }
                    if (root.TryGetProperty("usage", out var deltaUsage))
                    {
                        outputTokens = Int(deltaUsage, "output_tokens") ?? outputTokens;
                    }
                    break;

                case "error":
                    var error = root.TryGetProperty("error", out var errorObject)
                        ? String(errorObject, "message")
                        : null;
                    yield return new AgentModelEvent(
                        AgentModelEventKind.Failed,
                        Error: error ?? "The Anthropic stream reported an error.");
                    yield break;

                case "message_stop":
                    yield return new AgentModelEvent(
                        AgentModelEventKind.Completed,
                        StopReason: stopReason ?? "end_turn",
                        InputTokens: inputTokens,
                        OutputTokens: outputTokens);
                    yield break;
            }
        }

        yield return new AgentModelEvent(
            AgentModelEventKind.Failed,
            Error: "The Anthropic stream ended before message_stop.");
    }

    /// <summary>从协议事件读取字符串字段，保留缺失值处理。</summary>
    private static string? String(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    /// <summary>从协议事件读取整数计数，保留缺失值处理。</summary>
    private static int? Int(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.TryGetInt32(out var number)
            ? number
            : null;

    /// <param name="Id">当前实体或契约项目的唯一标识。</param>
    /// <param name="Name">实体或配置项的名称。</param>
    /// <param name="Json">按既有契约保存或传递的 JSON 内容。</param>
    private sealed record PendingTool(string Id, string Name, StringBuilder Json);
}
