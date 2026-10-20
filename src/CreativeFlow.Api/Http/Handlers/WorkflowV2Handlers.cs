using System.Text.Json;
using System.Text.Json.Nodes;
using CreativeFlow.Api.Middleware;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Identity;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Application.Features.Workflow;
using CreativeFlow.Application.Features.Knowledge;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Api.Http.Handlers;

/// <summary>类型化工作流运行入口；与媒体节点共用同一稳定工作流 API。</summary>
internal static class WorkflowV2Handlers
{
    /// <summary>读取工作流能力开关；未配置时默认关闭。</summary>
    internal static bool Enabled(HttpContext http) => bool.TryParse(
        http.RequestServices.GetRequiredService<IConfiguration>()["Workflow:Enabled"], out var enabled) && enabled;

    /// <summary>向已认证编辑器公布当前可用节点，不展示尚未实现的节点。</summary>
    internal static IActionResult Capabilities(HttpContext http)
    {
        var kinds = Enabled(http) ? new JsonArray("start", "llm", "parameterExtract", "intentClassify", "transform", "loop", "batch", "condition", "merge", "end") : new JsonArray();
        if (Enabled(http) && http.RequestServices.GetService<IWorkflowScriptRunner>()?.Enabled == true) kinds.Add("script");
        if (Enabled(http) && http.RequestServices.GetService<KnowledgeWorkflowService>()?.Enabled == true) { kinds.Add("knowledgeRetrieve"); kinds.Add("knowledgeAnswer"); }
        if (Enabled(http) && http.RequestServices.GetService<HttpRequestService>()?.Enabled == true) kinds.Add("httpRequest");
        return ApiResults.Ok(new JsonObject { ["enabled"] = Enabled(http), ["nodeTypes"] = kinds });
    }

    /// <summary>校验有界输入和模型许可，持久化不可变快照并立即返回任务 ID。</summary>
    internal static async Task<IActionResult> StartAsync(string id, JsonObject request, HttpContext http,
        IWorkflowRepository workflows, IWorkflowRunRepository runs, IWorkflowV2Models models,
        IWorkflowScriptRunner scripts, CancellationToken ct)
    {
        // 输入和模型快照由服务器构造；客户端不能传入 models 或密钥，入队后编辑草稿不会改变本次运行。
        if (http.GetWorkflowDeviceId() is not { } deviceId) return ApiResults.Unauthorized();
        if (!Enabled(http)) return Invalid("AI 工作流尚未启用。");
        if (!WorkflowId.TryParse(id, out var workflowId)) return Invalid("malformed workflow id");
        var workflow = await workflows.FindAsync(deviceId, workflowId, ct);
        if (workflow is null) return ApiResults.NotFound();
        JsonNode? graph;
        try { graph = JsonNode.Parse(workflow.GraphJson); }
        catch (JsonException) { return Invalid("stored workflow graph is invalid JSON"); }
        if (!WorkflowGraphCompiler.IsTypedGraph(graph)) return Invalid("该入口仅用于类型化工作流。");
        if (request.Count != 1 || request["inputs"] is not JsonObject inputs) return Invalid("请求必须包含 inputs 对象。");
        var validation = WorkflowV2GraphValidator.Validate(graph);
        if (validation.Plan is not { } plan || !validation.IsValid)
            return ApiResults.BadRequest(new ErrorResponse("invalid_workflow", [.. validation.Errors]));
        if (WorkflowScriptValues.ContainsScript(plan) && !scripts.Enabled)
            return Invalid("脚本沙箱未启用，不能执行包含脚本节点的工作流。");
        var error = WorkflowV2Values.Bind((JsonArray)plan.Nodes.Single(node => node.Type == "start").Data["fields"]!, inputs, out var bound);
        if (error is not null) return Invalid(error);
        try
        {
            var resolver = http.RequestServices.GetRequiredService<IWorkflowV2AssetResolver>();
            foreach (var field in ((JsonArray)plan.Nodes.Single(node => node.Type == "start").Data["fields"]!).OfType<JsonObject>()
                .Where(field => WorkflowV2Values.Text(field["type"]) == "image")
                .Select(field => WorkflowV2Values.Text(field["key"]))
                .Where(bound.ContainsKey))
                await resolver.ValidateAsync(deviceId, bound[field]!, ct);
        }
        catch (WorkflowV2Exception exception) { return Invalid(exception.Message); }
        JsonObject modelSnapshots;
        JsonObject? knowledgeSnapshot = null;
        JsonObject? httpSnapshot = null;
        try
        {
            if (plan.Nodes.Any(node => KnowledgeWorkflowValues.IsKnowledge(node.Type) || node.Type == "httpRequest"))
            {
                var subject = http.IsAdminWorkflowApi() && http.GetAuthenticatedAdmin() is { } admin
                    ? new KnowledgeSubject("admin", admin.Id, deviceId.Value)
                    : http.RequestServices.GetRequiredService<IAccountContext>().AccountId is { } account
                        ? new KnowledgeSubject("account", account.Value, deviceId.Value) : null;
                if (subject is null) return Invalid("知识或外部请求节点需要登录账号并获得管理员授权。");
                if (plan.Nodes.Any(node => KnowledgeWorkflowValues.IsKnowledge(node.Type)))
                    knowledgeSnapshot = await http.RequestServices.GetRequiredService<KnowledgeWorkflowService>().CaptureAsync(plan, subject, ct);
                if (plan.Nodes.Any(node => node.Type == "httpRequest"))
                    httpSnapshot = await http.RequestServices.GetRequiredService<HttpRequestService>().CaptureAsync(plan, subject, ct);
            }
            modelSnapshots = await models.CaptureAsync(plan, ct);
        }
        catch (WorkflowV2Exception exception) { return Invalid(exception.Message); }
        catch (WorkflowHttpException exception) { return ApiResults.BadRequest(new ErrorResponse(exception.Code, [exception.Message])); }
        var inputSnapshot = new JsonObject { ["schemaVersion"] = WorkflowGraphValidator.SchemaVersion, ["inputs"] = bound, ["models"] = modelSnapshots };
        if (knowledgeSnapshot is not null) inputSnapshot["knowledge"] = knowledgeSnapshot;
        if (httpSnapshot is not null) inputSnapshot["http"] = httpSnapshot;
        var now = DateTimeOffset.UtcNow;
        var run = new WorkflowRun(WorkflowRunId.New(), workflow.Id, deviceId, workflow.GraphJson,
            null, WorkflowRunStatus.Queued, null, now, now, null, null)
        {
            InputJson = inputSnapshot.ToJsonString(),
        };
        var nodes = plan.Nodes.Select(node => new WorkflowNodeRun(run.Id, node.Id, node.Type,
            WorkflowNodeRunStatus.Queued, false, null, null, null, null, null)).ToList();
        bool inserted;
        try { inserted = await runs.InsertAsync(run, nodes, ct); }
        catch (WorkflowV2Exception exception) { return Invalid(exception.Message); }
        if (!inserted)
        {
            var active = await runs.FindActiveAsync(deviceId, workflowId, ct);
            return ApiResults.Conflict(new ErrorResponse("workflow_already_running", active is null ? [] : [active.Id.ToString()]));
        }
        return ApiResults.Accepted($"{http.WorkflowBasePath()}/{id}/runs/{run.Id}", WorkflowHandlers.ToRunResponse(run, nodes));
    }

    /// <summary>使用既有错误响应格式返回安全的校验说明。</summary>
    private static IActionResult Invalid(string detail) => ApiResults.BadRequest(new ErrorResponse("invalid_workflow", [detail]));

    /// <summary>在结果读取和重跑入口再次授权；不对旧媒体或普通 LLM 运行新增知识/HTTP 依赖。</summary>
    internal static async Task<IActionResult?> CheckResourcesAsync(WorkflowRun run, HttpContext http, CancellationToken ct)
    {
        // 独立调试只检查目标节点捕获的资源；整图和发布运行必须复验全部资源才能读取结果。
        var debug = WorkflowNodeDebug.Target(run.InputJson) is not null ? JsonNode.Parse(run.InputJson!) : null;
        var hasKnowledge = debug is null ? KnowledgeWorkflowService.ContainsKnowledge(run.GraphJson) : debug["knowledge"] is not null;
        var hasHttp = debug is null ? HttpRequestService.ContainsRequests(run.GraphJson) : debug["http"] is not null;
        var publishedTyped = run.TriggerType == WorkflowRunTriggerType.Api && WorkflowGraphCompiler.IsTypedGraph(JsonNode.Parse(run.GraphJson));
        var hasImages = WorkflowGraphCompiler.IsTypedGraph(JsonNode.Parse(run.GraphJson)) && JsonNode.Parse(run.GraphJson) is JsonObject graph
            && graph["nodes"] is JsonArray nodes && nodes.OfType<JsonObject>().Any(node => node["data"]?["fields"] is JsonArray fields
                && fields.OfType<JsonObject>().Any(field => WorkflowV2Values.Text(field["type"]) == "image"));
        if (!hasKnowledge && !hasHttp && !publishedTyped && !hasImages) return null;
        http.Response.Headers.CacheControl = "private, no-store";
        try
        {
            var snapshot = JsonNode.Parse(run.InputJson ?? "null") as JsonObject ?? throw new WorkflowV2Exception("资源快照不可用。");
            if (publishedTyped) await http.RequestServices.GetRequiredService<WorkflowV2PublicationService>().ValidateRunAsync(run, ct);
            if (hasKnowledge) await http.RequestServices.GetRequiredService<KnowledgeWorkflowService>().ValidateAsync(snapshot, run.DeviceId.Value, ct);
            if (hasHttp) await http.RequestServices.GetRequiredService<HttpRequestService>().ValidateAsync(snapshot, run.DeviceId.Value, ct);
            if (hasImages)
            {
                var resolver = http.RequestServices.GetRequiredService<IWorkflowV2AssetResolver>();
                var values = snapshot["nodeDebug"]?["inputs"] as JsonObject ?? snapshot["inputs"] as JsonObject ?? [];
                foreach (var image in values.Where(pair => pair.Value is JsonObject image && image.ContainsKey("assetId")).Select(pair => pair.Value!))
                    await resolver.ValidateAsync(run.DeviceId, image, ct);
            }
            return null;
        }
        catch (WorkflowV2Exception exception) { return new ObjectResult(new ErrorResponse("knowledge_access_denied", [exception.Message])) { StatusCode = 403 }; }
        catch (WorkflowHttpException exception) { return new ObjectResult(new ErrorResponse(exception.Code, [exception.Message])) { StatusCode = 403 }; }
        catch (JsonException) { return new StatusCodeResult(403); }
    }
}
