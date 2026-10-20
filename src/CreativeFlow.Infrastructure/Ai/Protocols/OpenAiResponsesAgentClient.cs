using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Infrastructure.Ai.Protocols;

/// <summary>
/// 用于智能体轮次的 OpenAI Responses 流式接口（<c>POST /responses</c>）。
/// </summary>
/// <remarks>
/// Responses 将工具调用表示为顶层输出项，而不是 Chat Completions 消息。
/// 客户端把这些项转换为智能体的协议无关事件；轮次运行器执行工具，
/// 并在下一次迭代发送所得函数调用输出项。
/// </remarks>
internal sealed class OpenAiResponsesAgentClient(IHttpClientFactory httpClientFactory)
    : IAgentModelClient
{
    /// <summary>标识当前 Agent 客户端使用 OpenAI Responses 协议。</summary>
    public ProviderType Protocol => ProviderType.OpenAiResponses;

    /// <summary>发送原历史、系统指令及工具定义，解析 Responses 流中的文本、工具调用和结束事件。</summary>
    public async IAsyncEnumerable<AgentModelEvent> StreamAsync(
        AgentModelRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = request.ModelKey,
            ["input"] = AgentProtocolJson.ResponsesInput(request.Messages),
            ["stream"] = true,
            ["max_output_tokens"] = request.MaxOutputTokens,
        };

        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            body["instructions"] = request.SystemPrompt;
        }

        if (request.Tools.Count > 0)
        {
            body["tools"] = AgentProtocolJson.ResponsesTools(request.Tools);
        }

        if (request.SupportsThinking)
        {
            // 推理摘要可安全重放为仅供显示的智能体思考内容。
            // 加密推理项本身刻意不持久化，也不重建。
            body["reasoning"] = new JsonObject { ["summary"] = "auto" };
        }

        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            $"{request.Provider.BaseUrl.TrimEnd('/')}/responses")
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
        var functionCalls = new Dictionary<string, PendingFunctionCall>(StringComparer.Ordinal);
        var emittedCalls = new HashSet<string>(StringComparer.Ordinal);
        var sawToolCall = false;
        var sawRefusal = false;

        await foreach (var frame in SseReader.ReadAsync(stream, ct))
        {
            if (frame.Data.Length == 0) continue;
            if (frame.Data == "[DONE]") yield break;

            using var document = JsonDocument.Parse(frame.Data);
            var root = document.RootElement;
            var type = String(root, "type") ?? frame.EventName;

            switch (type)
            {
                case "response.output_text.delta":
                    if (String(root, "delta") is { Length: > 0 } text)
                    {
                        yield return new AgentModelEvent(
                            AgentModelEventKind.TextDelta, Text: text);
                    }
                    break;

                case "response.reasoning_summary_text.delta":
                case "response.reasoning_text.delta":
                    if (String(root, "delta") is { Length: > 0 } thinking)
                    {
                        yield return new AgentModelEvent(
                            AgentModelEventKind.ThinkingDelta, Text: thinking);
                    }
                    break;

                case "response.refusal.delta":
                    sawRefusal = true;
                    if (String(root, "delta") is { Length: > 0 } refusal)
                    {
                        yield return new AgentModelEvent(
                            AgentModelEventKind.TextDelta, Text: refusal);
                    }
                    break;

                case "response.output_item.added":
                    if (root.TryGetProperty("item", out var added))
                    {
                        sawToolCall |= UpsertFunctionCall(
                            functionCalls, added, replaceArguments: false) is not null;
                    }
                    break;

                case "response.function_call_arguments.delta":
                {
                    var key = EventItemKey(root);
                    if (key is null) break;
                    var call = GetFunctionCall(functionCalls, key);
                    call.CallId = String(root, "call_id") ?? call.CallId;
                    call.Arguments.Append(String(root, "delta") ?? string.Empty);
                    sawToolCall = true;
                    break;
                }

                case "response.function_call_arguments.done":
                {
                    var key = EventItemKey(root);
                    if (key is null) break;
                    var call = GetFunctionCall(functionCalls, key);
                    call.CallId = String(root, "call_id") ?? call.CallId;
                    if (String(root, "arguments") is { } arguments)
                    {
                        call.SetArguments(arguments);
                    }
                    sawToolCall = true;
                    break;
                }

                case "response.output_item.done":
                    if (root.TryGetProperty("item", out var doneItem)
                        && UpsertFunctionCall(functionCalls, doneItem, replaceArguments: true)
                            is { } doneCall)
                    {
                        sawToolCall = true;
                        if (BuildToolEvent(doneCall, emittedCalls) is { } tool)
                        {
                            yield return tool;
                        }
                    }
                    break;

                case "response.completed":
                {
                    var completed = ResponsePayload(root);
                    if (completed.TryGetProperty("output", out var output)
                        && output.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in output.EnumerateArray())
                        {
                            if (UpsertFunctionCall(
                                    functionCalls, item, replaceArguments: true)
                                is not { } call)
                            {
                                continue;
                            }

                            sawToolCall = true;
                            if (BuildToolEvent(call, emittedCalls) is { } tool)
                            {
                                yield return tool;
                            }
                        }
                    }

                    // 兼容中继可能省略最终输出数组，但仍发送函数调用参数事件。
                    // 完成前应发出这些待处理调用。
                    foreach (var call in functionCalls.Values)
                    {
                        if (BuildToolEvent(call, emittedCalls) is { } tool)
                        {
                            yield return tool;
                        }
                    }

                    var stopReason = sawRefusal
                        ? "refusal"
                        : sawToolCall ? "tool_calls" : "end_turn";
                    yield return Completed(completed, stopReason);
                    yield break;
                }

                case "response.incomplete":
                {
                    var incomplete = ResponsePayload(root);
                    var reason = incomplete.TryGetProperty("incomplete_details", out var details)
                        ? String(details, "reason")
                        : null;
                    yield return Completed(
                        incomplete,
                        reason == "max_output_tokens" ? "max_tokens" : reason ?? "incomplete");
                    yield break;
                }

                case "response.failed":
                    yield return new AgentModelEvent(
                        AgentModelEventKind.Failed,
                        Error: ResponseError(ResponsePayload(root)));
                    yield break;

                case "error":
                    yield return new AgentModelEvent(
                        AgentModelEventKind.Failed,
                        Error: ResponseError(root));
                    yield break;
            }
        }

        yield return new AgentModelEvent(
            AgentModelEventKind.Failed,
            Error: "The Responses stream ended before response.completed.");
    }

    /// <summary>根据 Responses 完成事件构造统一模型结束事件。</summary>
    private static AgentModelEvent Completed(JsonElement response, string stopReason) =>
        new(
            AgentModelEventKind.Completed,
            StopReason: stopReason,
            InputTokens: UsageInt(response, "input_tokens"),
            OutputTokens: UsageInt(response, "output_tokens"));

    /// <summary>从 Responses 使用量字段读取 token 计数。</summary>
    private static int? UsageInt(JsonElement response, string name) =>
        response.TryGetProperty("usage", out var usage)
        && usage.ValueKind == JsonValueKind.Object
        && usage.TryGetProperty(name, out var value)
        && value.TryGetInt32(out var number)
            ? number
            : null;

    /// <summary>从当前 Responses 事件中取出实际响应对象。</summary>
    private static JsonElement ResponsePayload(JsonElement root) =>
        root.TryGetProperty("response", out var response)
            && response.ValueKind == JsonValueKind.Object
            ? response
            : root;

    /// <summary>提取 Responses 流中的失败原因。</summary>
    private static string ResponseError(JsonElement root)
    {
        if (root.TryGetProperty("error", out var error))
        {
            if (error.ValueKind == JsonValueKind.String) return error.GetString() ?? "Responses failed.";
            if (error.ValueKind == JsonValueKind.Object
                && String(error, "message") is { } message)
            {
                return message;
            }
        }

        return String(root, "message") ?? "The Responses stream reported a failure.";
    }

    /// <summary>按上游项标识创建或更新正在拼接的工具调用。</summary>
    private static PendingFunctionCall? UpsertFunctionCall(
        Dictionary<string, PendingFunctionCall> calls,
        JsonElement item,
        bool replaceArguments)
    {
        if (String(item, "type") != "function_call") return null;

        var key = String(item, "id") ?? String(item, "call_id");
        if (key is null) return null;

        var call = GetFunctionCall(calls, key);
        call.CallId = String(item, "call_id") ?? call.CallId;
        call.Name = String(item, "name") ?? call.Name;
        if (String(item, "arguments") is { } arguments)
        {
            if (replaceArguments) call.SetArguments(arguments);
            else if (call.Arguments.Length == 0) call.SetArguments(arguments);
        }
        return call;
    }

    /// <summary>根据当前事件的关联字段查找工具调用累积项。</summary>
    private static PendingFunctionCall GetFunctionCall(
        Dictionary<string, PendingFunctionCall> calls,
        string key)
    {
        if (!calls.TryGetValue(key, out var call))
        {
            call = new PendingFunctionCall(key);
            calls[key] = call;
        }
        return call;
    }

    /// <summary>将已经组装完成的工具调用转换为统一模型事件。</summary>
    private static AgentModelEvent? BuildToolEvent(
        PendingFunctionCall call,
        HashSet<string> emittedCalls)
    {
        if (!emittedCalls.Add(call.Key)) return null;

        return new AgentModelEvent(
            AgentModelEventKind.ToolUse,
            ToolUseId: call.CallId.Length > 0 ? call.CallId : call.Key,
            ToolName: call.Name,
            Json: call.Arguments.Length > 0 ? call.Arguments.ToString() : "{}",
            ProviderItemId: call.Key);
    }

    /// <summary>提取 Responses 事件中用于关联工具项的稳定键。</summary>
    private static string? EventItemKey(JsonElement root) =>
        String(root, "item_id")
        ?? String(root, "id")
        ?? (root.TryGetProperty("output_index", out var outputIndex)
            && outputIndex.TryGetInt32(out var index)
                ? $"output-{index}"
                : null);

    /// <summary>从协议事件读取字符串字段，保留缺失值处理。</summary>
    private static string? String(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    /// <summary>初始化 PendingFunctionCall 的依赖与实例状态。</summary>
    private sealed class PendingFunctionCall(string key)
    {
        /// <summary>当前查找、签名或映射所使用的键。</summary>
        public string Key { get; } = key;
        /// <summary>上游函数调用的关联标识。</summary>
        public string CallId { get; set; } = string.Empty;
        /// <summary>实体或配置项的名称。</summary>
        public string Name { get; set; } = string.Empty;
        /// <summary>调用脚本或工具时传入的参数。</summary>
        public StringBuilder Arguments { get; } = new();

        /// <summary>更新工具调用的参数片段或完整参数。</summary>
        public void SetArguments(string value)
        {
            Arguments.Clear();
            Arguments.Append(value);
        }
    }
}
