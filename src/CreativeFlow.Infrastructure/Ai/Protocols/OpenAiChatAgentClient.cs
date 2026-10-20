using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Infrastructure.Ai.Protocols;

/// <summary>初始化 OpenAiChatAgentClient 的依赖与实例状态。</summary>
internal sealed class OpenAiChatAgentClient(IHttpClientFactory httpClientFactory) : IAgentModelClient
{
    /// <summary>标识当前 Agent 客户端使用 OpenAI Chat Completions 协议。</summary>
    public ProviderType Protocol => ProviderType.OpenAiChat;

    /// <summary>发送流式聊天历史和工具 schema，按索引累计工具参数分片并转成原 Agent 事件。</summary>
    public async IAsyncEnumerable<AgentModelEvent> StreamAsync(
        AgentModelRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // 工具参数可能跨多个 SSE 分片到达；保留索引、关联 ID 和累积顺序，直到收到完整结束信息。
        var body = new JsonObject
        {
            ["model"] = request.ModelKey,
            ["max_tokens"] = request.MaxOutputTokens,
            ["stream"] = true,
            ["stream_options"] = new JsonObject { ["include_usage"] = true },
            ["messages"] = AgentProtocolJson.OpenAiMessages(request.SystemPrompt, request.Messages),
        };
        if (request.Tools.Count > 0)
        {
            body["tools"] = AgentProtocolJson.OpenAiTools(request.Tools);
            body["parallel_tool_calls"] = false;
        }

        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            $"{request.Provider.BaseUrl.TrimEnd('/')}/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey);

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
        var tools = new Dictionary<int, PendingTool>();
        string? stopReason = null;
        int? inputTokens = null;
        int? outputTokens = null;

        await foreach (var frame in SseReader.ReadAsync(stream, ct))
        {
            if (frame.Data == "[DONE]")
            {
                foreach (var tool in tools.OrderBy(pair => pair.Key).Select(pair => pair.Value))
                {
                    yield return CompleteTool(tool);
                }

                yield return new AgentModelEvent(
                    AgentModelEventKind.Completed,
                    StopReason: stopReason ?? (tools.Count > 0 ? "tool_calls" : "end_turn"),
                    InputTokens: inputTokens,
                    OutputTokens: outputTokens);
                yield break;
            }

            if (frame.Data.Length == 0) continue;
            using var document = JsonDocument.Parse(frame.Data);
            var root = document.RootElement;

            if (root.TryGetProperty("usage", out var usage)
                && usage.ValueKind == JsonValueKind.Object)
            {
                inputTokens = Int(usage, "prompt_tokens") ?? inputTokens;
                outputTokens = Int(usage, "completion_tokens") ?? outputTokens;
            }

            if (!root.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
            {
                continue;
            }

            var choice = choices[0];
            stopReason = String(choice, "finish_reason") ?? stopReason;
            if (!choice.TryGetProperty("delta", out var delta)) continue;

            if (String(delta, "content") is { } text && text.Length > 0)
            {
                yield return new AgentModelEvent(AgentModelEventKind.TextDelta, Text: text);
            }

            if (String(delta, "reasoning_content") is { } thinking && thinking.Length > 0)
            {
                yield return new AgentModelEvent(AgentModelEventKind.ThinkingDelta, Text: thinking);
            }

            if (!delta.TryGetProperty("tool_calls", out var calls)
                || calls.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var call in calls.EnumerateArray())
            {
                if (!call.TryGetProperty("index", out var indexElement)
                    || !indexElement.TryGetInt32(out var index)) continue;

                if (!tools.TryGetValue(index, out var pending))
                {
                    pending = new PendingTool(new StringBuilder(), new StringBuilder(), new StringBuilder());
                    tools[index] = pending;
                }

                if (String(call, "id") is { } id) pending.Id.Append(id);
                if (!call.TryGetProperty("function", out var function)) continue;
                if (String(function, "name") is { } name) pending.Name.Append(name);
                if (String(function, "arguments") is { } arguments) pending.Json.Append(arguments);
            }
        }

        yield return new AgentModelEvent(
            AgentModelEventKind.Failed,
            Error: "The OpenAI-compatible stream ended before [DONE].");
    }

    /// <summary>在流结束或工具边界处组装分段工具参数。</summary>
    private static AgentModelEvent CompleteTool(PendingTool tool)
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

        return new AgentModelEvent(
            AgentModelEventKind.ToolUse,
            ToolUseId: tool.Id.ToString(),
            ToolName: tool.Name.ToString(),
            Json: json);
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
    private sealed record PendingTool(StringBuilder Id, StringBuilder Name, StringBuilder Json);
}
