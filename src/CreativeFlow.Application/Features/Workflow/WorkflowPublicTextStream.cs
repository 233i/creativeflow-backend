using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Application.Features.Workflow;

/// <summary>每次运行独立的公开文本投影；只沿结束字段和原样汇合连线追踪模型输出。</summary>
public sealed class WorkflowPublicTextStream(IWorkflowRunEventRepository events)
{
    private WorkflowRunId? _runId;
    private WorkflowPlan? _plan;

    public void Configure(WorkflowRun run, WorkflowPlan plan)
    {
        _runId = run.TriggerType == WorkflowRunTriggerType.Api && JsonNode.Parse(run.InputJson ?? "null")?["stream"]?.GetValue<bool>() == true ? run.Id : null;
        _plan = plan;
    }

    /// <summary>返回公开文本的接收器；内部节点保持原非流式请求，不保存或推送其正文。</summary>
    public Func<string, CancellationToken, Task>? Receiver(WorkflowPlanNode node)
    {
        if (_runId is not { } runId || _plan is null) return null;
        var targets = Targets(_plan, node);
        if (targets.Count == 0) return null;
        var full = new StringBuilder();
        var delivered = new Dictionary<string, int>(StringComparer.Ordinal);
        return async (delta, ct) =>
        {
            full.Append(delta);
            if (full.Length > 128 * 1024) throw new WorkflowV2Exception("模型流式输出超过大小限制。未自动重试。");
            var raw = full.ToString();
            foreach (var target in targets)
            {
                var current = target.JsonField is null ? raw : JsonStringPreview.Read(raw, target.JsonField);
                if (current is null) continue;
                var key = target.Output + target.Path;
                var offset = delivered.GetValueOrDefault(key);
                if (current.Length <= offset) continue;
                // 一次上游帧可能包含完整响应；每个持久化事件保持有界并避免拆开代理对。
                while (offset < current.Length)
                {
                    var length = Math.Min(2048, current.Length - offset);
                    if (char.IsHighSurrogate(current[offset + length - 1])) length--;
                    if (length == 0) break;
                    await events.AppendAsync(runId, new JsonObject { ["output"] = target.Output, ["path"] = target.Path,
                        ["delta"] = current.Substring(offset, length), ["provisional"] = true }.ToJsonString(), ct);
                    offset += length;
                }
                delivered[key] = offset;
            }
        };
    }

    /// <summary>不跨变换、HTTP、条件或另一个模型传播，避免把最终契约之外的中间文本误发给调用方。</summary>
    internal static IReadOnlyList<StreamTarget> Targets(WorkflowPlan plan, WorkflowPlanNode model)
    {
        var targets = new List<StreamTarget>();
        var nodes = plan.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var end = plan.Nodes.Single(node => node.Type == "end");
        foreach (var field in ((JsonArray)end.Data["fields"]!).OfType<JsonObject>())
        {
            var key = WorkflowV2Values.Text(field["key"]);
            var type = WorkflowV2Values.Text(field["type"]);
            if (type is "string" or "answer") Visit(end, key, key, type);
        }
        return targets.Distinct().ToArray();

        void Visit(WorkflowPlanNode target, string field, string output, string type)
        {
            foreach (var edge in target.Inputs.Where(edge => !edge.IsControl && edge.TargetHandle == field))
            {
                var source = nodes[edge.Source];
                if (source.Id == model.Id)
                {
                    if (model.Type == "knowledgeAnswer" && edge.SourceHandle == "answer" && type == "answer") targets.Add(new(output, "/text", "text"));
                    else if (model.Type == "llm" && !WorkflowModelTasks.IsStructured(model) && edge.SourceHandle == "text" && type == "string") targets.Add(new(output, "", null));
                    else if (WorkflowModelTasks.IsStructured(model) && type == "string") targets.Add(new(output, "", edge.SourceHandle));
                }
                else if (source.Type == "merge") Visit(source, edge.SourceHandle, output, type);
            }
        }
    }

    internal sealed record StreamTarget(string Output, string Path, string? JsonField);
}

/// <summary>只读取 JSON 根对象的指定字符串前缀；完整结果仍由节点原有严格解析器验证。</summary>
internal static class JsonStringPreview
{
    public static string? Read(string json, string field)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            var reader = new Utf8JsonReader(bytes, isFinalBlock: false, state: default);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1 || reader.GetString() != field) continue;
                var position = (int)reader.BytesConsumed;
                if (reader.Read()) return reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                var remainder = Encoding.UTF8.GetString(bytes.AsSpan(position)).TrimStart();
                if (!remainder.StartsWith('"')) return null;
                // 仅关闭已经收到的完整 JSON 转义，保留跨帧反斜杠、Unicode 和代理对。
                var end = 1;
                while (end < remainder.Length)
                {
                    var ch = remainder[end];
                    if (ch == '"' || ch < ' ') break;
                    if (ch == '\\')
                    {
                        if (end + 1 >= remainder.Length) break;
                        if (remainder[end + 1] == 'u')
                        {
                            if (end + 6 > remainder.Length) break;
                            var code = Convert.ToInt32(remainder.Substring(end + 2, 4), 16);
                            if (char.IsHighSurrogate((char)code))
                            {
                                if (end + 12 > remainder.Length || remainder.Substring(end + 6, 2) != "\\u") break;
                                end += 6;
                            }
                            end += 6;
                        }
                        else end += 2;
                    }
                    else if (char.IsHighSurrogate(ch)) { if (end + 1 >= remainder.Length) break; end += 2; }
                    else end++;
                }
                using var document = JsonDocument.Parse(remainder[..end] + '"');
                return document.RootElement.GetString();
            }
        }
        catch (Exception error) when (error is JsonException or FormatException or ArgumentException) { /* 非法前缀不作为正文，终态由原校验器判定。 */ }
        return null;
    }
}
