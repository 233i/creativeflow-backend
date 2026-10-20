using System.Text.Json.Nodes;
using CreativeFlow.Domain.Ai;
using static CreativeFlow.Application.Features.Workflow.WorkflowV2Values;

namespace CreativeFlow.Application.Features.Workflow;

/// <summary>编译类型化 DAG 和显式条件控制边；计划由唯一 MAF 按依赖顺序访问。</summary>
public static class WorkflowV2GraphValidator
{
    /// <summary>校验节点定义、稳定端口、唯一来源、连通性和环路；完整验证后才构造执行计划。</summary>
    public static WorkflowValidationResult Validate(JsonNode? graph, bool forExecution = true, string? onlyNodeId = null, int depth = 0)
    {
        var errors = new List<string>();
        if (depth > WorkflowLoopValues.MaxBodyDepth) return new(null, [$"nested workflow depth cannot exceed {WorkflowLoopValues.MaxBodyDepth}"]);
        if (graph is not JsonObject root || Number(root["schemaVersion"]) != WorkflowGraphValidator.SchemaVersion || root["nodes"] is not JsonArray rawNodes || root["edges"] is not JsonArray rawEdges)
            return new(null, ["typed workflow graph requires schemaVersion 1, nodes and edges"]);
        if (root.Any(pair => pair.Key is not ("schemaVersion" or "nodes" or "edges"))) errors.Add("workflow graph contains unknown root fields");
        if (rawNodes.Count is < 2 or > 40 || rawEdges.Count > 100) return new(null, ["workflow graph requires 2–40 nodes and at most 100 edges"]);
        if (onlyNodeId is not null)
        {
            rawNodes = new JsonArray(rawNodes.OfType<JsonObject>().Where(node => Text(node["id"]) == onlyNodeId).Select(node => node.DeepClone()).ToArray());
            rawEdges = [];
            if (rawNodes.Count != 1) return new(null, ["debug target must identify exactly one existing node"]);
        }
        var nodes = new Dictionary<string, WorkflowPlanNode>(StringComparer.Ordinal);
        foreach (var item in rawNodes)
        {
            if (item is not JsonObject node || Text(node["id"]) is not { Length: > 0 and <= 100 } id || node["data"] is not JsonObject data)
            { errors.Add("invalid workflow node"); continue; }
            var type = Text(node["type"]);
            if (node.Any(pair => pair.Key is not ("id" or "type" or "data" or "position"))) errors.Add($"node '{id}' has unknown metadata");
            if (node["position"] is not JsonObject position || Number(position["x"]) is not { } x || Number(position["y"]) is not { } y
                || Math.Abs(x) > 1000000 || Math.Abs(y) > 1000000) errors.Add($"node '{id}' has invalid position");
            if (type is not ("start" or "llm" or "end" or "knowledgeRetrieve" or "knowledgeAnswer" or "condition" or "merge" or "httpRequest" or "parameterExtract" or "intentClassify" or "transform" or "script" or "loop" or "batch")) { errors.Add($"node '{id}' has unsupported workflow type '{type}'"); continue; }
            if (!nodes.TryAdd(id, new(id, type, data, []))) { errors.Add($"duplicate node '{id}'"); continue; }
            var allowed = WorkflowModelTasks.UsesModel(type)
                ? new HashSet<string> { "title", "modelId", "systemPrompt", "prompt", "fields", "maxOutputTokens", "temperature", "timeoutSeconds", "outputMode", "outputFields", "outputTransport", "intents", "retry" }
                : new HashSet<string> { "title", "fields", "retry" };
            if (type == "condition")
            {
                if (WorkflowBranchValues.Validate(data) is { } branchError) errors.Add($"node '{id}': {branchError}");
            }
            else if (type == "transform")
            {
                if (WorkflowTransform.Validate(data) is { } transformError) errors.Add($"node '{id}': {transformError}");
            }
            else if (type == "script")
            {
                allowed = new HashSet<string> { "title", "fields", "outputFields", "language", "code", "timeoutSeconds", "retry" };
                if (WorkflowScriptValues.Validate(data) is { } scriptError) errors.Add($"node '{id}': {scriptError}");
            }
            else if (type == "httpRequest")
            {
                if (HttpRequestDefinition.Validate(data, forExecution) is { } httpError) errors.Add($"node '{id}': {httpError}");
            }
            else if (KnowledgeWorkflowValues.IsKnowledge(type))
            {
                if (KnowledgeWorkflowValues.Validate(type, data, forExecution) is { } knowledgeError) errors.Add($"node '{id}': {knowledgeError}");
            }
            else if (type is "loop" or "batch")
            {
                if (WorkflowLoopValues.Validate(data, type, forExecution, depth) is { } loopError) errors.Add($"node '{id}': {loopError}");
            }
            else if (data.Any(pair => !allowed.Contains(pair.Key))) errors.Add($"node '{id}' has unknown configuration fields");
            if (WorkflowRetryPolicy.Validate(data["retry"]) is { } retryError) errors.Add($"node '{id}': {retryError}");
            if (data["title"] is not null && (data["title"]!.GetValueKind() != System.Text.Json.JsonValueKind.String || Text(data["title"]).Length > 100)) errors.Add($"node '{id}' has invalid title");
            if (ValidateFields(data["fields"], type is "end" or "knowledgeAnswer" or "condition" or "merge") is { } fieldError) errors.Add($"node '{id}': {fieldError}");
            if (!WorkflowModelTasks.UsesModel(type)) continue;
            if (type != "knowledgeAnswer" && WorkflowModelTasks.Validate(nodes[id]) is { } modelTaskError) errors.Add($"node '{id}': {modelTaskError}");
            if (data["modelId"] is not null && data["modelId"]!.GetValueKind() != System.Text.Json.JsonValueKind.String) errors.Add($"node '{id}' modelId must be a string");
            if ((forExecution || Text(data["modelId"]).Length != 0) && !ModelId.TryParse(Text(data["modelId"]), out _)) errors.Add($"node '{id}' needs an explicit text model");
            if (data["systemPrompt"] is not null && (data["systemPrompt"]!.GetValueKind() != System.Text.Json.JsonValueKind.String || Text(data["systemPrompt"]).Length > 4000)) errors.Add($"node '{id}' has invalid system prompt");
            // 模型上限在捕获配置和执行时校验；此处只保证请求可以无损转换为协议使用的整数。
            if (Number(data["maxOutputTokens"]) is not { } tokens || !double.IsFinite(tokens) || tokens < 1 || tokens > int.MaxValue || tokens != Math.Truncate(tokens)) errors.Add($"node '{id}' maxOutputTokens must be a positive 32-bit integer");
            if (Number(data["temperature"]) is not { } temperature || temperature < 0 || temperature > 2) errors.Add($"node '{id}' temperature must be 0–2");
            if (Number(data["timeoutSeconds"]) is not { } timeout || timeout < 1 || timeout > 300 || timeout != Math.Truncate(timeout)) errors.Add($"node '{id}' timeout must be 1–300 seconds");
            if (type == "knowledgeAnswer") continue;
            var fields = (data["fields"] as JsonArray)?.OfType<JsonObject>().Select(field => Text(field["key"])).ToHashSet(StringComparer.Ordinal) ?? [];
            if (data["prompt"] is not JsonArray parts || parts.Count is < 1 or > 32 || !IsBounded(parts)) errors.Add($"node '{id}' needs a bounded prompt template");
            else foreach (var part in parts)
            {
                if (part is not JsonObject segment || segment.Count != 1 || !(
                    segment.ContainsKey("text") && segment["text"]?.GetValueKind() == System.Text.Json.JsonValueKind.String
                    || segment.ContainsKey("input") && fields.Contains(Text(segment["input"])))) errors.Add($"node '{id}' has an invalid prompt segment or variable");
            }
        }
        if (onlyNodeId is null && (nodes.Values.Count(node => node.Type == "start") != 1 || nodes.Values.Count(node => node.Type == "end") != 1)) errors.Add("workflow requires exactly one start and one end node");
        var draftEdgeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in rawEdges)
        {
            if (item is not JsonObject edge || edge.Any(pair => pair.Key is not ("id" or "source" or "target" or "sourceHandle" or "targetHandle"))
                || Text(edge["id"]).Length is < 1 or > 100 || !draftEdgeIds.Add(Text(edge["id"]))
                || !nodes.ContainsKey(Text(edge["source"])) || !nodes.ContainsKey(Text(edge["target"]))
                || !ValidKey(Text(edge["sourceHandle"])) || !(ValidKey(Text(edge["targetHandle"])) || Text(edge["targetHandle"]) == WorkflowBranchValues.ControlPort))
                errors.Add("workflow edge has invalid identifiers, missing nodes or unknown metadata");
        }
        if (errors.Count > 0) return new(null, errors);
        // 草稿允许暂未接线或模型未选；字段形状、参数边界和节点类别仍必须安全可编辑。
        if (!forExecution || onlyNodeId is not null) return new(new(nodes.Values.ToArray()), []);

        var edges = new List<WorkflowPlanEdge>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in rawEdges)
        {
            if (item is not JsonObject edge) { errors.Add("invalid workflow edge"); continue; }
            var id = Text(edge["id"]); var source = Text(edge["source"]); var target = Text(edge["target"]);
            var sourceHandle = Text(edge["sourceHandle"]); var targetHandle = Text(edge["targetHandle"]);
            if (id.Length is < 1 or > 100 || !ids.Add(id) || !nodes.TryGetValue(source, out var from) || !nodes.TryGetValue(target, out var to) || source == target)
            { errors.Add($"invalid edge '{id}' or missing node"); continue; }
            if (targetHandle == WorkflowBranchValues.ControlPort)
            {
                if (!WorkflowBranchValues.IsBranch(from.Type) || to.Type is "start" or "end" || !WorkflowBranchValues.Branches(from.Data).Contains(sourceHandle, StringComparer.Ordinal)
                    || !targets.Add($"{target}\n{targetHandle}")) errors.Add($"edge '{id}' has an invalid or duplicate execution condition");
                else edges.Add(new(id, source, sourceHandle, target, targetHandle, WorkflowValueType.Boolean));
                continue;
            }
            var output = OutputFields(from).OfType<JsonObject>().FirstOrDefault(field => Text(field["key"]) == sourceHandle);
            var input = to.Type == "start" ? null : ((JsonArray)to.Data["fields"]!).OfType<JsonObject>().FirstOrDefault(field => Text(field["key"]) == targetHandle);
            if (output is null || input is null || Text(output["type"]) != Text(input["type"]) || (to.Type != "merge" && !targets.Add($"{target}\n{targetHandle}")))
            { errors.Add($"edge '{id}' has an unknown, incompatible or already connected port"); continue; }
            if (input["required"]!.GetValue<bool>() && !output["required"]!.GetValue<bool>() && !output.ContainsKey("default")) errors.Add($"edge '{id}' cannot supply a required input from an optional output");
            edges.Add(new(id, source, sourceHandle, target, targetHandle, ValueType(Text(output["type"]))));
        }
        foreach (var condition in nodes.Values.Where(node => WorkflowBranchValues.IsBranch(node.Type)))
            foreach (var branch in WorkflowBranchValues.Branches(condition.Data))
                if (!edges.Any(edge => edge.IsControl && edge.Source == condition.Id && edge.SourceHandle == branch)) errors.Add($"condition '{condition.Id}' route '{branch}' must be connected, including the default route");
        foreach (var node in nodes.Values.Where(node => node.Type != "start"))
        {
            var inputs = edges.Where(edge => edge.Target == node.Id).ToArray();
            if (inputs.Length == 0) errors.Add($"node '{node.Id}' must depend on the start node");
            foreach (var field in ((JsonArray)node.Data["fields"]!).OfType<JsonObject>())
                if (field["required"]!.GetValue<bool>() && !inputs.Any(edge => edge.TargetHandle == Text(field["key"])) && !field.ContainsKey("default")) errors.Add($"node '{node.Id}' input '{Text(field["key"])}' is not connected");
        }
        var pending = nodes.Values.ToList(); var ordered = new List<WorkflowPlanNode>();
        var completed = new HashSet<string>(StringComparer.Ordinal);
        while (pending.Count > 0)
        {
            var ready = pending.FirstOrDefault(node => edges.Where(edge => edge.Target == node.Id).All(edge => completed.Contains(edge.Source)));
            if (ready is null) { errors.Add("workflow contains a cycle"); break; }
            pending.Remove(ready); completed.Add(ready.Id);
            ordered.Add(ready with { Inputs = edges.Where(edge => edge.Target == ready.Id).ToArray() });
        }
        // 禁止未连接到结束节点的付费旁路；每次执行的所有节点都应对最终输出有贡献。
        var end = nodes.Values.Single(node => node.Type == "end");
        var ancestors = new HashSet<string>(StringComparer.Ordinal) { end.Id };
        for (var i = 0; i < nodes.Count; i++) foreach (var edge in edges) if (ancestors.Contains(edge.Target)) ancestors.Add(edge.Source);
        if (ancestors.Count != nodes.Count) errors.Add("every workflow node must contribute to the end node");
        if (errors.Count == 0) errors.AddRange(WorkflowBranchFlow.Validate(ordered));
        return errors.Count == 0 ? new(new(ordered), []) : new(null, errors);
    }

    /// <summary>开始与汇合输出声明字段，条件保存稳定路由；执行控制出口独立于业务数据。</summary>
    public static JsonArray OutputFields(WorkflowPlanNode node) => node.Type switch
    { "httpRequest" => HttpRequestDefinition.Outputs(node.Data), "start" or "merge" => (JsonArray)node.Data["fields"]!, "transform" or "script" => (JsonArray)node.Data["outputFields"]!, "loop" or "batch" => WorkflowLoopValues.Outputs(node.Data), "llm" or "parameterExtract" or "intentClassify" => WorkflowModelTasks.Outputs(node), "condition" => WorkflowBranchValues.Outputs(),
        "knowledgeRetrieve" or "knowledgeAnswer" => KnowledgeWorkflowValues.Outputs(node.Type), _ => [] };

    /// <summary>将新版类型映射到共同拓扑端口；不把 JSON 对象降级成旧文本端口。</summary>
    private static WorkflowValueType ValueType(string type) => type switch
    { "string" => WorkflowValueType.String, "number" => WorkflowValueType.Number, "boolean" => WorkflowValueType.Boolean,
        "object" or "contextChunks" or "answer" => WorkflowValueType.Object, "image" => WorkflowValueType.Image, _ => WorkflowValueType.Array };
}
