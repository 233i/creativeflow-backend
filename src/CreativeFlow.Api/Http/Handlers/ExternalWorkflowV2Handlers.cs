using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Application.Features.Workflow;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Infrastructure.Options;

namespace CreativeFlow.Api.Http.Handlers;

/// <summary>类型化外部运行的专用绑定；与媒体工作流共享同一外部 HTTP API、队列和响应语义。</summary>
internal static partial class ExternalWorkflowHandlers
{
    /// <summary>使用固定发布图及真实 Key 绑定输入，复用原幂等唯一约束、队列准入和异步调度。</summary>
    private static async Task<IActionResult> CreateTypedRunAsync(PublishedWorkflowCatalogItem catalog, WorkflowApiKey key,
        JsonObject? inputs, string idempotencyKey, string requestId, HttpContext http,
        IWorkflowExternalApiRepository externalApi, IWorkflowRunRepository runs, ICanvasAssetRepository assets,
        ExternalWorkflowApiOptions options, CancellationToken ct, bool stream = false, JsonObject? agui = null)
    {
        // 和媒体图使用同一个入队仓储及唯一幂等键；只改变图/参数绑定，不另起同步执行或队列。
        if (!http.Response.HasStarted) http.Response.Headers.CacheControl = "private, no-store";
        if (!WorkflowV2Handlers.Enabled(http)) return Error(503, requestId, "workflow_temporarily_unavailable", "AI workflow execution is disabled.");
        WorkflowPlan plan;
        try
        {
            var contract = WorkflowV2PublicationService.Contract(catalog.Deployment.GraphJson); plan = contract.Plan;
            if (inputs is null) return Invalid(requestId, "inputs", "required", "inputs must be an object.");
            if (WorkflowV2Values.Bind(contract.Inputs, inputs, out _) is { } error) return Invalid(requestId, "inputs", "invalid", error);

            // 外部图片必须来自当前工作流 API Key 的上传接口，不能借用同设备的任意素材。
            foreach (var field in contract.Inputs.OfType<JsonObject>().Where(field => WorkflowV2Values.Text(field["type"]) == "image"))
            {
                var keyName = WorkflowV2Values.Text(field["key"]);
                if (!inputs.ContainsKey(keyName)) continue; // 由发布图固定的默认图片由 BindAsync 按设备权限校验。
                var value = inputs[keyName] as JsonObject;
                if (value is null || !Guid.TryParse(WorkflowV2Values.Text(value["assetId"]), out var assetId))
                    return Invalid(requestId, $"inputs.{keyName}", "invalid_asset", "The image is unavailable for this workflow.");
                var uploaded = await externalApi.FindExternalAssetAsync(catalog.Deployment.WorkflowId, assetId, ct);
                var owned = await assets.FindAsync(catalog.OwnerDeviceId, assetId, ct);
                if (uploaded is null || uploaded.ApiKeyId != key.Id
                    || uploaded.ExpiresAt is { } expires && expires <= DateTimeOffset.UtcNow
                    || owned is not { Kind: "image", IsRecycled: false })
                    return Invalid(requestId, $"inputs.{keyName}", "invalid_asset", "The image is unavailable for this workflow.");
            }
        }
        catch (WorkflowV2Exception) { return Error(503, requestId, "workflow_temporarily_unavailable", "The published workflow contract is invalid."); }
        JsonObject snapshot;
        var publications = http.RequestServices.GetRequiredService<WorkflowV2PublicationService>();
        try { snapshot = await publications.BindAsync(catalog.Deployment, new("apiKey", key.Id.Value, catalog.OwnerDeviceId.Value), inputs, ct); }
        catch (WorkflowV2Exception exception) { return Error(403, requestId, "workflow_resource_denied", exception.Message); }
        catch (WorkflowHttpException exception) { return Error(403, requestId, "workflow_resource_denied", exception.Message); }
        if (stream) snapshot["stream"] = true;
        if (agui is not null) snapshot["agui"] = agui.DeepClone();
        var now = DateTimeOffset.UtcNow;
        var run = new WorkflowRun(WorkflowRunId.New(), catalog.Deployment.WorkflowId, catalog.OwnerDeviceId,
            catalog.Deployment.GraphJson, null, WorkflowRunStatus.Queued, null, now, now, null, null)
        {
            TriggerType = WorkflowRunTriggerType.Api, DeploymentId = catalog.Deployment.Id, ApiKeyId = key.Id,
            RequestId = requestId, IdempotencyKey = idempotencyKey, RequestHash = HashRequest(catalog.Deployment.Id, inputs, stream, agui),
            InputJson = snapshot.ToJsonString(),
        };
        try { await publications.ValidateRunAsync(run, ct); }
        catch (WorkflowV2Exception exception) { return Error(403, requestId, "workflow_resource_denied", exception.Message); }
        catch (WorkflowHttpException exception) { return Error(403, requestId, "workflow_resource_denied", exception.Message); }
        var nodes = plan.Nodes.Select(node => new WorkflowNodeRun(run.Id, node.Id, node.Type, WorkflowNodeRunStatus.Queued,
            false, null, null, null, null, null)).ToList();
        WorkflowRunInsertResult inserted;
        try { inserted = await runs.InsertExternalAsync(run, nodes, options.MaxQueuedRunsPerWorkflow, ct); }
        catch (WorkflowV2Exception exception) { return Error(403, requestId, "workflow_resource_denied", exception.Message); }
        if (inserted is WorkflowRunInsertResult.QueueFull or WorkflowRunInsertResult.Duplicate)
        {
            var existing = await runs.FindByIdempotencyAsync(key.Id, idempotencyKey, ct);
            if (existing is not null) return await ReplayRunAsync(existing, inputs, requestId, http, externalApi, ct, stream, agui);
            return inserted == WorkflowRunInsertResult.QueueFull
                ? Error(409, requestId, "workflow_queue_full", "The workflow queue is full.")
                : Error(409, requestId, "idempotency_conflict", "The idempotency key is already in use.");
        }
        var consumed = contractImageIds(plan, inputs);
        if (consumed.Count > 0) await externalApi.MarkExternalAssetsConsumedAsync(catalog.Deployment.WorkflowId, consumed, now, ct);
        return ApiResults.Accepted($"/api/external/v1/runs/{run.Id}", ToCreated(run, catalog.Deployment.Version));
    }

    private static IReadOnlyList<Guid> contractImageIds(WorkflowPlan plan, JsonObject? inputs) =>
        plan.Nodes.Single(node => node.Type == "start").Data["fields"] is JsonArray fields && inputs is not null
            ? fields.OfType<JsonObject>().Where(field => WorkflowV2Values.Text(field["type"]) == "image")
                .Select(field => inputs[WorkflowV2Values.Text(field["key"])] as JsonObject)
                .Select(value => Guid.TryParse(WorkflowV2Values.Text(value?["assetId"]), out var id) ? id : Guid.Empty)
                .Where(id => id != Guid.Empty).Distinct().ToArray()
            : [];

    /// <summary>查询和幂等重放均复验 Key 的资源授权，不因已完成或缓存结果而放行。</summary>
    private static async Task<IActionResult?> CheckTypedRunAsync(WorkflowRun run, HttpContext http, string requestId, CancellationToken ct)
    {
        if (!http.Response.HasStarted) http.Response.Headers.CacheControl = "private, no-store";
        if (!WorkflowV2Handlers.Enabled(http)) return Error(503, requestId, "workflow_temporarily_unavailable", "AI workflow execution is disabled.");
        try { await http.RequestServices.GetRequiredService<WorkflowV2PublicationService>().ValidateRunAsync(run, ct); return null; }
        catch (WorkflowV2Exception exception) { return Error(403, requestId, "workflow_resource_denied", exception.Message); }
        catch (WorkflowHttpException exception) { return Error(403, requestId, "workflow_resource_denied", exception.Message); }
    }
}
