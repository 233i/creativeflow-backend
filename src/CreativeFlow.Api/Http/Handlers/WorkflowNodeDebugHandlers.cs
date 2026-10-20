using System.Text.Json;
using System.Text.Json.Nodes;
using CreativeFlow.Api.Middleware;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Identity;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Application.Features.Knowledge;
using CreativeFlow.Application.Features.Workflow;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;
using static CreativeFlow.Application.Features.Workflow.WorkflowV2Values;

namespace CreativeFlow.Api.Http.Handlers;

/// <summary>独立节点调试的输入准备和提交入口，读取权限与普通工作流一致，不执行上下游节点。</summary>
internal static class WorkflowNodeDebugHandlers
{
    /// <summary>只读准备历史输入，知识证据只从已授权、成功持久化的节点结果中获取。</summary>
    internal static async Task<IActionResult> InputsAsync(string id, string nodeId, string sourceRunId, HttpContext http,
        IWorkflowRepository workflows, IWorkflowRunRepository runs, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "private, no-store";
        if (http.GetWorkflowDeviceId() is not { } device || !WorkflowId.TryParse(id, out var workflowId)) return ApiResults.NotFound();
        var workflow = await workflows.FindAsync(device, workflowId, ct);
        if (workflow is null || !WorkflowGraphCompiler.IsTypedGraph(JsonNode.Parse(workflow.GraphJson)) || !WorkflowV2Handlers.Enabled(http)) return ApiResults.NotFound();
        try
        {
            var graph = JsonNode.Parse(workflow.GraphJson)!;
            var plan = RequirePlan(graph, nodeId);
            var source = await HistoryAsync(sourceRunId, device, workflowId, graph, plan.Nodes[0], http, runs, ct);
            return ApiResults.Ok(new JsonObject { ["inputs"] = source.Inputs,
                ["readonlyFields"] = new JsonArray(((JsonArray)plan.Nodes[0].Data["fields"]!).OfType<JsonObject>().Where(field => Text(field["type"]) is "contextChunks" or "answer")
                    .Select(field => (JsonNode?)JsonValue.Create(Text(field["key"]))).ToArray()) });
        }
        catch (WorkflowV2Exception error) { return Invalid(error.Message); }
    }

    /// <summary>保存独立快照并立即返回任务 ID；同一 requestId 重发只返回原任务，不二次执行。</summary>
    internal static async Task<IActionResult> StartAsync(string id, string nodeId, JsonObject request, HttpContext http,
        IWorkflowRepository workflows, IWorkflowRunRepository runs, IWorkflowV2Models models,
        IWorkflowScriptRunner scripts, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "private, no-store";
        if (http.GetWorkflowDeviceId() is not { } device || !WorkflowId.TryParse(id, out var workflowId)) return ApiResults.NotFound();
        if (!WorkflowV2Handlers.Enabled(http)) return Invalid("AI 工作流尚未启用。");
        var workflow = await workflows.FindAsync(device, workflowId, ct);
        if (workflow is null || !WorkflowGraphCompiler.IsTypedGraph(JsonNode.Parse(workflow.GraphJson))) return ApiResults.NotFound();
        if (request.Any(pair => pair.Key is not ("requestId" or "inputs" or "sourceRunId")) || request["inputs"] is not JsonObject manual
            || !Guid.TryParse(Text(request["requestId"]), out var requestId) || requestId == Guid.Empty || !IsBounded(manual)) return Invalid("需要 requestId 和有界 inputs；可选 sourceRunId 只能引用同一工作流的历史运行。");
        var runId = WorkflowNodeDebug.RunId(device, workflowId, requestId);
        // 同一设备、工作流和请求键映射到同一个持久任务，重放先验原参数与当前授权。
        /// <summary>幂等重放复验原请求和当前资源授权，不能借相同请求 ID 改变付费参数。</summary>
        async Task<IActionResult?> ExistingAsync()
        {
            var existing = await runs.FindAsync(device, workflowId, runId, ct);
            if (existing is null) return null;
            var metadata = JsonNode.Parse(existing.InputJson ?? "null")?["nodeDebug"];
            if (Text(metadata?["nodeId"]) != nodeId || !JsonNode.DeepEquals(metadata?["request"], request)) return ApiResults.Conflict(new ErrorResponse("debug_idempotency_conflict", ["相同请求 ID 不能用于不同的调试参数。"]));
            if (await WorkflowV2Handlers.CheckResourcesAsync(existing, http, ct) is { } denied) return denied;
            return ApiResults.Ok(WorkflowHandlers.ToRunResponse(existing, await runs.ListNodesAsync(existing.Id, ct)));
        }
        if (await ExistingAsync() is { } found) return found;
        try
        {
            var graph = JsonNode.Parse(workflow.GraphJson)!;
            var plan = RequirePlan(graph, nodeId); var target = plan.Nodes[0];
            if (target.Type == "script" && !scripts.Enabled) return Invalid("脚本沙箱未启用，不能调试脚本节点。");
            var fields = (JsonArray)target.Data["fields"]!;
            var inputs = new JsonObject(); JsonObject? historyKnowledge = null;
            if (request.ContainsKey("sourceRunId"))
            {
                var historical = await HistoryAsync(Text(request["sourceRunId"]), device, workflowId, graph, target, http, runs, ct);
                inputs = historical.Inputs; historyKnowledge = historical.Knowledge;
            }
            foreach (var pair in manual)
            {
                if (fields.OfType<JsonObject>().Any(field => Text(field["key"]) == pair.Key && Text(field["type"]) is "contextChunks" or "answer"))
                    return Invalid("知识证据不能手工输入，请选择有权限的历史运行。");
                inputs[pair.Key] = pair.Value?.DeepClone();
            }
            if (Bind(fields, inputs, out var bound) is { } error) return Invalid(error);
            var assetResolver = http.RequestServices.GetRequiredService<IWorkflowV2AssetResolver>();
            foreach (var field in fields.OfType<JsonObject>().Where(field => Text(field["type"]) == "image").Select(field => Text(field["key"])).Where(bound.ContainsKey))
                await assetResolver.ValidateAsync(device, bound[field]!, ct);
            var snapshot = new JsonObject { ["schemaVersion"] = WorkflowGraphValidator.SchemaVersion, ["inputs"] = new JsonObject(), ["models"] = await models.CaptureAsync(plan, ct),
                ["nodeDebug"] = new JsonObject { ["nodeId"] = nodeId, ["inputs"] = bound, ["request"] = request.DeepClone() } };
            var needsKnowledge = KnowledgeWorkflowValues.IsKnowledge(target.Type) || fields.OfType<JsonObject>().Any(field => Text(field["type"]) is "contextChunks" or "answer");
            if (needsKnowledge || target.Type == "httpRequest")
            {
                var subject = http.IsAdminWorkflowApi() && http.GetAuthenticatedAdmin() is { } admin ? new KnowledgeSubject("admin", admin.Id, device.Value)
                    : http.RequestServices.GetRequiredService<IAccountContext>().AccountId is { } account ? new KnowledgeSubject("account", account.Value, device.Value) : null;
                if (subject is null) return Invalid("知识或外部接口调试需要已授权的登录账号。");
                if (needsKnowledge)
                {
                    var knowledge = http.RequestServices.GetRequiredService<KnowledgeWorkflowService>();
                    snapshot["knowledge"] = target.Type == "knowledgeRetrieve" ? await knowledge.CaptureAsync(plan, subject, ct)
                        : historyKnowledge is not null ? await knowledge.ReauthorizeAsync(historyKnowledge, subject, ct)
                        : throw new WorkflowV2Exception("请先选择包含所需知识证据的历史运行。");
                }
                if (target.Type == "httpRequest") snapshot["http"] = await http.RequestServices.GetRequiredService<HttpRequestService>().CaptureAsync(plan, subject, ct);
            }
            var now = DateTimeOffset.UtcNow;
            var run = new WorkflowRun(runId, workflowId, device, workflow.GraphJson, null, WorkflowRunStatus.Queued, null, now, now, null, null) { InputJson = snapshot.ToJsonString() };
            var nodes = new[] { new WorkflowNodeRun(runId, nodeId, target.Type, WorkflowNodeRunStatus.Queued, false, null, null, null, null, null) };
            if (!await runs.InsertAsync(run, nodes, ct))
            {
                if (await ExistingAsync() is { } raced) return raced;
                var active = await runs.FindActiveAsync(device, workflowId, ct);
                return ApiResults.Conflict(new ErrorResponse("workflow_already_running", active is null ? [] : [active.Id.ToString()]));
            }
            return ApiResults.Accepted($"{http.WorkflowBasePath()}/{id}/runs/{runId}", WorkflowHandlers.ToRunResponse(run, nodes));
        }
        catch (WorkflowV2Exception error) { return Invalid(error.Message); }
        catch (WorkflowHttpException error) { return ApiResults.BadRequest(new ErrorResponse(error.Code, [error.Message])); }
    }

    /// <summary>完整检查选中节点配置，但不要求尚在编辑的其他节点已连接或已选模型。</summary>
    private static WorkflowPlan RequirePlan(JsonNode graph, string nodeId)
    {
        var validation = WorkflowV2GraphValidator.Validate(graph, onlyNodeId: nodeId);
        return validation.IsValid && validation.Plan is { } plan ? plan : throw new WorkflowV2Exception(string.Join("; ", validation.Errors));
    }

    /// <summary>按照当前目标的真实连线读取历史成功输出，不调模型、不重新检索、不信任客户端证据正文。</summary>
    private static async Task<(JsonObject Inputs, JsonObject? Knowledge)> HistoryAsync(string id, DeviceId device, WorkflowId workflow,
        JsonNode graph, WorkflowPlanNode target, HttpContext http, IWorkflowRunRepository runs, CancellationToken ct)
    {
        if (!WorkflowRunId.TryParse(id, out var sourceId) || await runs.FindAsync(device, workflow, sourceId, ct) is not { } source)
            throw new WorkflowV2Exception("历史运行不可访问。");
        if (await WorkflowV2Handlers.CheckResourcesAsync(source, http, ct) is not null) throw new WorkflowV2Exception("历史运行的资源授权已失效。");
        var snapshot = JsonNode.Parse(source.InputJson ?? "null") as JsonObject ?? throw new WorkflowV2Exception("历史输入快照缺失。");
        if (WorkflowNodeDebug.Target(source.InputJson) == target.Id)
        {
            var historicalFields = RequirePlan(JsonNode.Parse(source.GraphJson)!, target.Id).Nodes[0].Data["fields"];
            if (!JsonNode.DeepEquals(historicalFields, target.Data["fields"])) throw new WorkflowV2Exception("调试节点输入定义已变化，请重新填写或选择整图历史。");
            return ((JsonObject)snapshot["nodeDebug"]!["inputs"]!.DeepClone(), snapshot["knowledge"]?.DeepClone() as JsonObject);
        }
        if (target.Type == "start") return ((JsonObject)(snapshot["inputs"]?.DeepClone() ?? new JsonObject()), null);
        var historical = WorkflowV2GraphValidator.Validate(JsonNode.Parse(source.GraphJson), forExecution: false).Plan
            ?? throw new WorkflowV2Exception("历史图快照不可用。");
        var stored = (await runs.ListNodesAsync(source.Id, ct)).ToDictionary(node => node.NodeId);
        var values = new Dictionary<string, JsonObject>(); var skipped = new HashSet<string>(); var edges = new List<WorkflowPlanEdge>();
        foreach (var edge in ((JsonArray)graph["edges"]!).OfType<JsonObject>().Where(edge => Text(edge["target"]) == target.Id && Text(edge["targetHandle"]) != WorkflowBranchValues.ControlPort))
        {
            var from = Text(edge["source"]); var definition = historical.Nodes.FirstOrDefault(node => node.Id == from);
            if (definition is null || !stored.TryGetValue(from, out var node)) throw new WorkflowV2Exception("所选历史尚无对应上游结果。可手工输入普通参数。");
            if (node.Status == WorkflowNodeRunStatus.Skipped) skipped.Add(from);
            else if (node.Status == WorkflowNodeRunStatus.Succeeded && TryRestore(definition, node.OutputJson, out var restored)) values[from] = restored;
            else throw new WorkflowV2Exception("只能读取已完成且类型有效的上游结果。");
            edges.Add(new(Text(edge["id"]), from, Text(edge["sourceHandle"]), target.Id, Text(edge["targetHandle"]), WorkflowValueType.Object));
        }
        return (WorkflowBranchFlow.BindInputs(target with { Inputs = edges }, values, skipped), snapshot["knowledge"]?.DeepClone() as JsonObject);
    }

    /// <summary>调试错误使用统一业务错误结构，前端可以定位配置而无需解析 HTML。</summary>
    private static IActionResult Invalid(string detail) => ApiResults.BadRequest(new ErrorResponse("invalid_node_debug", [detail]));
}
