using System.Text.Json;
using System.Text.Json.Nodes;
using AGUI.Abstractions;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Contracts.Ai;

namespace CreativeFlow.Api.Http.Handlers;

/// <summary>确定性投影日志到官方事件类型；重放时保持消息、步骤和子帧的顺序及 ID。</summary>
internal sealed class AgUiWorkflowProjection(string internalRunId, JsonObject identity, string graphJson)
{
    /// <summary>按日志重建的公开状态，不能提前使用实时终态覆盖历史中的执行顺序。</summary>
    private readonly JsonObject _snapshot = new() { ["internalRunId"] = internalRunId, ["status"] = "queued", ["progress"] = 0 };
    /// <summary>每个节点最近一次已重放的安全元数据。</summary>
    private readonly Dictionary<string, JsonObject> _nodes = new(StringComparer.Ordinal);
    /// <summary>已发出开始事件的步骤，用于抑制运行中进度更新产生重复开始。</summary>
    private readonly HashSet<string> _steps = new(StringComparer.Ordinal);
    /// <summary>已经开始的公开输出消息，终态时按稳定顺序关闭。</summary>
    private readonly HashSet<string> _messages = new(StringComparer.Ordinal);
    /// <summary>运行提交时固定的节点名称，旧日志缺失名称时也能补齐展示信息。</summary>
    private readonly IReadOnlyDictionary<string, string> _nodeNames = WorkflowNodeDisplayNames.FromGraph(graphJson);
    /// <summary>是否已经重放完成标记；完成后的续读不生成第二个终态。</summary>
    internal bool Finished { get; private set; }

    /// <summary>将一条事务日志展开为固定顺序的标准子帧；最终结果使用本次已复验的投影。</summary>
    internal IReadOnlyList<BaseEvent> Project(WorkflowRunJournalEntry entry, ExternalWorkflowRunResponse state)
    {
        var data = JsonNode.Parse(entry.Data)!.AsObject();
        var timestamp = entry.CreatedAt.ToUnixTimeMilliseconds();
        var result = new List<BaseEvent>();
        switch (entry.Kind)
        {
            case "run.created":
                result.Add(new RunStartedEvent { ThreadId = Text(identity["threadId"]), RunId = Text(identity["runId"]),
                    ParentRunId = identity["parentRunId"]?.GetValue<string>(), ProtocolVersion = "1.0", Timestamp = timestamp });
                result.Add(new CustomEvent { Name = "workflow.accepted", Value = Element(new JsonObject {
                    ["internalRunId"] = internalRunId, ["threadId"] = identity["threadId"]?.DeepClone(), ["runId"] = identity["runId"]?.DeepClone(),
                    ["statusUrl"] = $"/api/external/v1/runs/{internalRunId}", ["eventsUrl"] = $"/api/agui/v1/runs/{internalRunId}/events" }), Timestamp = timestamp });
                break;
            case "run.state":
                foreach (var pair in data) _snapshot[pair.Key] = pair.Value?.DeepClone();
                result.Add(Snapshot(timestamp));
                break;
            case "node.state":
                var id = Text(data["id"]); var status = Text(data["status"]);
                var type = Text(data["type"]);
                var name = Text(data["name"]);
                if (name.Length == 0 || name == type) name = _nodeNames.GetValueOrDefault(id) ?? (type.Length == 0 ? id : type);
                data["name"] = name;
                var previous = _nodes.GetValueOrDefault(id);
                _nodes[id] = data;
                if (status is "running" or "retrying" && _steps.Add(id)) result.Add(StepStarted(id, name, type, timestamp));
                if (status is "succeeded" or "failed" or "cancelled" && Text(previous?["status"]) != status
                    && !(status == "cancelled" && Text(previous?["status"]) == "queued"))
                {
                    // 复用节点也保持成对步骤；跳过分支只记录快照，不假装执行过。
                    if (_steps.Add(id)) result.Add(StepStarted(id, name, type, timestamp));
                    result.Add(StepFinished(id, name, type, timestamp));
                }
                result.Add(Snapshot(timestamp));
                break;
            case "text.delta":
                var messageId = internalRunId + ":" + Text(data["output"]) + ":" + Text(data["path"]);
                if (_messages.Add(messageId)) result.Add(new TextMessageStartEvent { MessageId = messageId, Role = "assistant", Timestamp = timestamp });
                result.Add(new TextMessageContentEvent { MessageId = messageId, Delta = Text(data["delta"]), Timestamp = timestamp });
                break;
            case "run.finished":
                foreach (var completedMessage in _messages.Order(StringComparer.Ordinal))
                    result.Add(new TextMessageEndEvent { MessageId = completedMessage, Timestamp = timestamp });
                var terminal = Text(data["status"]);
                result.Add(terminal == "failed"
                    ? Error("workflow_failed", Text(data["error"]), retryable: false, timestamp)
                    : new RunFinishedEvent { ThreadId = Text(identity["threadId"]), RunId = Text(identity["runId"]), Timestamp = timestamp,
                        Result = terminal == "succeeded" && state.Outputs is not null ? Element(state.Outputs) : null,
                        Outcome = terminal == "cancelled" ? new RunFinishedCancelledOutcome() : new RunFinishedSuccessOutcome() });
                Finished = true;
                break;
        }
        return result;
    }

    /// <summary>根据已重放的节点计算进度与当前节点，不含内部输入和输出。</summary>
    private StateSnapshotEvent Snapshot(long timestamp)
    {
        _snapshot["nodes"] = new JsonArray(_nodes.Values.Select(node => (JsonNode)node.DeepClone()).ToArray());
        _snapshot["progress"] = _nodes.Count == 0 ? 0 : _nodes.Values.Average(node => Text(node["status"]) is "succeeded" or "skipped" ? 1d : node["progress"]?.GetValue<double>() ?? 0);
        var current = _nodes.Values.FirstOrDefault(node => Text(node["status"]) is "running" or "retrying");
        _snapshot["currentNode"] = current is null ? null : new JsonObject
        {
            ["id"] = current["id"]?.DeepClone(), ["name"] = current["name"]?.DeepClone(),
            ["type"] = current["type"]?.DeepClone(),
        };
        return new StateSnapshotEvent { Snapshot = Element(_snapshot), Timestamp = timestamp };
    }

    /// <summary>标准步骤名称直接使用节点名称，节点 ID 作为 metadata 标识供画布定位。</summary>
    private static StepStartedEvent StepStarted(string id, string name, string type, long timestamp) => new()
    {
        StepName = name, Timestamp = timestamp, Metadata = NodeMetadata(id, name, type),
    };

    /// <summary>结束步骤时重复节点元数据，断线重放无需依赖此前的开始事件。</summary>
    private static StepFinishedEvent StepFinished(string id, string name, string type, long timestamp) => new()
    {
        StepName = name, Timestamp = timestamp, Metadata = NodeMetadata(id, name, type),
    };

    /// <summary>节点名称来自固定图快照，不携带提示词、凭据或节点输出。</summary>
    private static JsonElement NodeMetadata(string id, string name, string type) => Element(new JsonObject
    {
        ["nodeId"] = id, ["nodeName"] = name, ["nodeType"] = type,
    });

    /// <summary>区分可重连的传输中断和需清除预览的业务／权限失败。</summary>
    internal static RunErrorEvent Error(string code, string message, bool retryable, long? timestamp = null) => new() {
        Code = code, Message = message, Timestamp = timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        Metadata = Element(new JsonObject { ["retryable"] = retryable, ["clearPreview"] = !retryable }) };
    /// <summary>转换为官方事件需要的独立 JSON 元素，不共享可变快照。</summary>
    internal static JsonElement Element(JsonNode node) => JsonSerializer.SerializeToElement(node);
    /// <summary>日志中的可选文本以空字符串表示缺失；类型错误保持显式失败。</summary>
    private static string Text(JsonNode? node) => node?.GetValue<string>() ?? "";
}
