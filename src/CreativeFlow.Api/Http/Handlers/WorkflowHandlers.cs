using System.Text.Json;
using System.Text.Json.Nodes;
using CreativeFlow.Api.Middleware;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Abstractions.Realtime;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Application.Features.Workflow;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Api.Http.Handlers;

/// <summary>独立工作流文档及其服务器端持久化运行。</summary>
internal static class WorkflowHandlers
{
    /// <summary>完整节点图 JSON 允许的最大字节数。</summary>
    private const int MaxGraphBytes = 2 * 1024 * 1024;
    /// <summary>显示名称允许的最多字符数。</summary>
    private const int MaxNameLength = 200;
    /// <summary>新图或模板使用的既有空节点/连线结构。</summary>
    private const string EmptyGraph = """{"nodes":[],"edges":[]}""";

    /// <summary>列出工作流，保留现有筛选、排序与访问范围。</summary>
    internal static async Task<IActionResult> ListAsync(
        HttpContext http,
        IWorkflowRepository workflows,
        CancellationToken ct)
    {
        if (http.GetWorkflowDeviceId() is not { } deviceId) return ApiResults.Unauthorized();

        var list = await workflows.ListAsync(deviceId, ct);
        return ApiResults.Ok(new WorkflowListResponse(
        [
            .. list.Select(item => new WorkflowSummaryResponse(
                item.WorkflowId.ToString(),
                item.Name,
                item.NodeCount,
                item.CreatedAt.ToString("O"),
                item.UpdatedAt.ToString("O"))),
        ]));
    }

    /// <summary>校验输入并创建工作流。</summary>
    internal static async Task<IActionResult> CreateAsync(
        CreateWorkflowRequest request,
        HttpContext http,
        IWorkflowRepository workflows,
        CancellationToken ct)
    {
        if (http.GetWorkflowDeviceId() is not { } deviceId) return ApiResults.Unauthorized();

        var schemaVersion = request.SchemaVersion ?? WorkflowGraphValidator.SchemaVersion;
        if (schemaVersion != WorkflowGraphValidator.SchemaVersion)
        {
            return Invalid("unsupported workflow schema version");
        }

        if (!TryReadGraph(request.Graph, out var graphJson, out var error))
        {
            return Invalid(error);
        }
        // 媒体画布允许先保存不完整草稿；类型化工作流才在保存时校验字段和端口契约。
        if (WorkflowGraphCompiler.IsTypedGraph(request.Graph)
            && WorkflowGraphCompiler.Validate(request.Graph, forExecution: false) is { IsValid: false } draft)
            return Invalid(string.Join("; ", draft.Errors));

        Guid? copiedId = null;
        // 复制来源只是审计关系，不继承源对象的发布、密钥或知识资源授权。
        if (request.CopyOrigin is { } origin)
        {
            if (!http.IsAdminWorkflowApi() || origin.Source is not ("workflow" or "template")
                || !Guid.TryParse(origin.Id, out var sourceId) || sourceId == Guid.Empty)
                return Invalid("invalid workflow copy origin");
            var sources = await http.RequestServices.GetRequiredService<IWorkflowCenterRepository>()
                .ListAsync(new WorkflowCenterQuery(Id: sourceId.ToString(), Source: origin.Source), true, ct);
            if (sources.Items.Length != 1 || sources.Items[0].SchemaVersion != schemaVersion)
                return Invalid("copy origin is not available or has a different graph type");
            copiedId = sourceId;
        }

        var now = DateTimeOffset.UtcNow;
        var workflow = new Workflow(
            WorkflowId.New(),
            deviceId,
            NormalizeName(request.Name),
            schemaVersion,
            graphJson,
            now,
            now) { IsApi = http.IsAdminWorkflowApi(), CopiedFromSource = request.CopyOrigin?.Source, CopiedFromId = copiedId };

        await workflows.InsertAsync(workflow, ct);
        return ApiResults.Created($"{http.WorkflowBasePath()}/{workflow.Id}", ToResponse(workflow));
    }

    /// <summary>读取工作流，保留缺失资源的既有处理。</summary>
    internal static async Task<IActionResult> GetAsync(
        string id,
        HttpContext http,
        IWorkflowRepository workflows,
        CancellationToken ct)
    {
        if (http.GetWorkflowDeviceId() is not { } deviceId) return ApiResults.Unauthorized();
        if (!WorkflowId.TryParse(id, out var workflowId)) return Invalid("malformed workflow id");

        var workflow = await workflows.FindAsync(deviceId, workflowId, ct);
        return workflow is null ? ApiResults.NotFound() : ApiResults.Ok(ToResponse(workflow));
    }

    /// <summary>更新工作流，保留原有访问范围和并发结果。</summary>
    internal static async Task<IActionResult> UpdateAsync(
        string id,
        UpdateWorkflowRequest request,
        HttpContext http,
        IWorkflowRepository workflows,
        CancellationToken ct)
    {
        // 保存完整图时保持现有未知字段和资源归属检查，不能在更新阶段隐式改写运行快照。
        if (http.GetWorkflowDeviceId() is not { } deviceId) return ApiResults.Unauthorized();
        if (!WorkflowId.TryParse(id, out var workflowId)) return Invalid("malformed workflow id");

        var existing = await workflows.FindAsync(deviceId, workflowId, ct);
        if (existing is null) return ApiResults.NotFound();

        var schemaVersion = request.SchemaVersion ?? existing.SchemaVersion;
        if (schemaVersion != existing.SchemaVersion || schemaVersion != WorkflowGraphValidator.SchemaVersion)
        {
            return Invalid("unsupported workflow schema version");
        }

        var graphJson = existing.GraphJson;
        if (request.Graph is not null
            && !TryReadGraph(request.Graph, out graphJson, out var graphError))
        {
            return Invalid(graphError);
        }

        var updatedGraph = JsonNode.Parse(graphJson);
        if (WorkflowGraphCompiler.IsTypedGraph(updatedGraph)
            && WorkflowGraphCompiler.Validate(updatedGraph, forExecution: false) is { IsValid: false } draft)
            return Invalid(string.Join("; ", draft.Errors));
        var updated = existing with
        {
            Name = string.IsNullOrWhiteSpace(request.Name)
                ? existing.Name
                : Truncate(request.Name.Trim()),
            SchemaVersion = schemaVersion,
            GraphJson = graphJson,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        return await workflows.UpdateAsync(updated, ct) ? ApiResults.Ok(ToResponse(updated)) : ApiResults.NotFound();
    }

    /// <summary>删除工作流，保留不存在或不可访问时的返回语义。</summary>
    internal static async Task<IActionResult> DeleteAsync(
        string id,
        HttpContext http,
        IWorkflowRepository workflows,
        IWorkflowRunRepository runs,
        CancellationToken ct)
    {
        if (http.GetWorkflowDeviceId() is not { } deviceId) return ApiResults.Unauthorized();
        if (!WorkflowId.TryParse(id, out var workflowId)) return Invalid("malformed workflow id");

        if (await runs.FindActiveAsync(deviceId, workflowId, ct) is not null)
        {
            return ApiResults.Conflict(new ErrorResponse(
                "workflow_is_running", ["stop the active workflow run before deleting the project"]));
        }

        return await workflows.DeleteAsync(deviceId, workflowId, ct)
            ? ApiResults.NoContent()
            : ApiResults.NotFound();
    }

    /// <summary>按统一工作流入口创建运行；类型化图使用 inputs，媒体图保留画布运行语义。</summary>
    internal static Task<IActionResult> StartAsync(
        string id,
        JsonObject? request,
        HttpContext http,
        IWorkflowRepository workflows,
        IWorkflowRunRepository runs,
        IWorkflowV2Models models,
        IWorkflowScriptRunner scripts,
        CancellationToken ct)
        => StartTypedOrLegacyAsync(id, request, http, workflows, runs, models, scripts, ct);

    /// <summary>解析持久图后选择类型化或媒体入口，保持既有请求协议和设备归属检查。</summary>
    private static async Task<IActionResult> StartTypedOrLegacyAsync(
        string id,
        JsonObject? request,
        HttpContext http,
        IWorkflowRepository workflows,
        IWorkflowRunRepository runs,
        IWorkflowV2Models models,
        IWorkflowScriptRunner scripts,
        CancellationToken ct)
    {
        if (http.GetWorkflowDeviceId() is not { } deviceId || !WorkflowId.TryParse(id, out var workflowId))
            return http.GetWorkflowDeviceId() is null ? ApiResults.Unauthorized() : Invalid("malformed workflow id");
        var workflow = await workflows.FindAsync(deviceId, workflowId, ct);
        if (workflow is null) return ApiResults.NotFound();
        JsonNode? graph;
        try { graph = JsonNode.Parse(workflow.GraphJson); }
        catch (JsonException) { return Invalid("stored workflow graph is invalid JSON"); }
        if (WorkflowGraphCompiler.IsTypedGraph(graph) && request is null)
            return Invalid("类型化工作流请求必须包含 inputs 对象。");
        return WorkflowGraphCompiler.IsTypedGraph(graph)
            ? await WorkflowV2Handlers.StartAsync(id, request!, http, workflows, runs, models, scripts, ct)
            : await StartRunAsync(id, http, workflows, runs, ct);
    }

    /// <summary>验证保存的节点图并创建交互式工作流运行。</summary>
    internal static async Task<IActionResult> StartRunAsync(
        string id,
        HttpContext http,
        IWorkflowRepository workflows,
        IWorkflowRunRepository runs,
        CancellationToken ct)
    {
        // 创建运行前验证完整图；入库后的图快照才是后台执行依据，后续编辑不会改变本次输入。
        if (http.GetWorkflowDeviceId() is not { } deviceId) return ApiResults.Unauthorized();
        if (!WorkflowId.TryParse(id, out var workflowId)) return Invalid("malformed workflow id");

        var workflow = await workflows.FindAsync(deviceId, workflowId, ct);
        if (workflow is null) return ApiResults.NotFound();

        JsonNode? graph;
        try
        {
            graph = JsonNode.Parse(workflow.GraphJson);
        }
        catch (JsonException)
        {
            return Invalid("stored workflow graph is invalid JSON");
        }

        var validation = WorkflowGraphCompiler.Validate(graph);
        if (!validation.IsValid || validation.Plan is null)
        {
            return ApiResults.BadRequest(new ErrorResponse("invalid_workflow", [.. validation.Errors]));
        }

        var now = DateTimeOffset.UtcNow;
        var run = new WorkflowRun(
            WorkflowRunId.New(),
            workflow.Id,
            workflow.DeviceId,
            workflow.GraphJson,
            null,
            WorkflowRunStatus.Queued,
            null,
            now,
            now,
            null,
            null);

        var nodeRuns = validation.Plan.Nodes.Select(node => new WorkflowNodeRun(
            run.Id,
            node.Id,
            node.Type,
            WorkflowNodeRunStatus.Queued,
            false,
            null,
            null,
            null,
            null,
            null)).ToList();

        if (!await runs.InsertAsync(run, nodeRuns, ct))
        {
            var active = await runs.FindActiveAsync(deviceId, workflowId, ct);
            return ApiResults.Conflict(new ErrorResponse(
                "workflow_already_running",
                active is null ? [] : [active.Id.ToString()]));
        }

        return ApiResults.Accepted(
            $"{http.WorkflowBasePath()}/{workflow.Id}/runs/{run.Id}",
            ToRunResponse(run, nodeRuns));
    }

    /// <summary>基于失败运行的快照创建续跑，并复用可恢复的成功输出。</summary>
    internal static async Task<IActionResult> ResumeRunAsync(
        string id,
        string runId,
        HttpContext http,
        IWorkflowRunRepository runs,
        CancellationToken ct)
    {
        // 续跑只能恢复符合原有条件的历史运行，并逐项确认成功节点输出可重用，避免重复调用上游。
        if (http.GetWorkflowDeviceId() is not { } deviceId) return ApiResults.Unauthorized();
        if (!WorkflowId.TryParse(id, out var workflowId)
            || !WorkflowRunId.TryParse(runId, out var sourceRunId))
        {
            return ApiResults.NotFound();
        }

        var source = await runs.FindAsync(deviceId, workflowId, sourceRunId, ct);
        if (source is null) return ApiResults.NotFound();
        if (WorkflowNodeDebug.Target(source.InputJson) is not null) return ResumeUnavailable("单节点调试不能作为整图续跑来源。请在调试面板明确创建新调试。");
        if (await WorkflowV2Handlers.CheckResourcesAsync(source, http, ct) is { } knowledgeDenied) return knowledgeDenied;
        if (source.Status != WorkflowRunStatus.Failed)
        {
            return ApiResults.Conflict(new ErrorResponse(
                "workflow_not_resumable", ["Only a failed workflow run can be continued."]));
        }

        JsonNode? graph;
        try
        {
            graph = JsonNode.Parse(source.GraphJson);
        }
        catch (JsonException)
        {
            return ResumeUnavailable("The failed run's workflow snapshot is invalid.");
        }

        var validation = WorkflowGraphCompiler.Validate(graph);
        if (!validation.IsValid || validation.Plan is null)
        {
            return ResumeUnavailable("The failed run's workflow snapshot is no longer valid.");
        }

        var sourceNodes = (await runs.ListNodesAsync(source.Id, ct))
            .ToDictionary(node => node.NodeId, StringComparer.Ordinal);
        if (WorkflowGraphCompiler.IsTypedGraph(graph))
        {
            if (!WorkflowV2Handlers.Enabled(http)) return ResumeUnavailable("类型化工作流尚未启用。");
            if (sourceNodes.Values.Any(node => (WorkflowModelTasks.UsesModel(node.NodeType) || node.NodeType is "knowledgeRetrieve" or "httpRequest") && node.Status != WorkflowNodeRunStatus.Succeeded && node.StartedAt is not null))
                return ResumeUnavailable("模型或外部接口节点已尝试调用但结果不明确，不能自动继续。请核对远端结果后再明确重新运行；新运行会再次发送请求。");
        }
        var now = DateTimeOffset.UtcNow;
        var continuation = new WorkflowRun(
            WorkflowRunId.New(),
            source.WorkflowId,
            source.DeviceId,
            source.GraphJson,
            source.Id,
            WorkflowRunStatus.Queued,
            null,
            now,
            now,
            null,
            null);

        if (WorkflowGraphCompiler.IsTypedGraph(graph)) continuation = continuation with { InputJson = source.InputJson };
        var continuationNodes = new List<WorkflowNodeRun>(validation.Plan.Nodes.Count);
        foreach (var planNode in validation.Plan.Nodes)
        {
            if (!sourceNodes.TryGetValue(planNode.Id, out var sourceNode)
                || !string.Equals(sourceNode.NodeType, planNode.Type, StringComparison.Ordinal))
            {
                return ResumeUnavailable(
                    $"The stored state for node '{planNode.Id}' cannot be restored.");
            }

            var reuse = sourceNode.Status == WorkflowNodeRunStatus.Succeeded;
            var preserveSkipped = sourceNode.Status == WorkflowNodeRunStatus.Skipped;
            if (reuse && !(WorkflowGraphCompiler.IsTypedGraph(graph) ? WorkflowV2Values.TryRestore(planNode, sourceNode.OutputJson, out _) : CanReuseOutput(sourceNode.OutputJson)))
            {
                return ResumeUnavailable(
                    $"The completed output for node '{planNode.Id}' cannot be restored.");
            }

            continuationNodes.Add(new WorkflowNodeRun(
                continuation.Id,
                planNode.Id,
                planNode.Type,
                preserveSkipped ? WorkflowNodeRunStatus.Skipped : reuse ? WorkflowNodeRunStatus.Succeeded : WorkflowNodeRunStatus.Queued,
                reuse,
                reuse ? 1 : preserveSkipped ? sourceNode.Progress : null,
                reuse ? sourceNode.OutputJson : null,
                null,
                reuse ? sourceNode.StartedAt : null,
                reuse ? sourceNode.FinishedAt : preserveSkipped ? sourceNode.FinishedAt : null)
            {
                Attempt = preserveSkipped ? sourceNode.Attempt : 1,
                MaxAttempts = sourceNode.MaxAttempts,
                SkipReason = preserveSkipped ? sourceNode.SkipReason : null,
            });
        }

        if (!await runs.InsertAsync(continuation, continuationNodes, ct))
        {
            var active = await runs.FindActiveAsync(deviceId, workflowId, ct);
            return ApiResults.Conflict(new ErrorResponse(
                "workflow_already_running",
                active is null ? [] : [active.Id.ToString()]));
        }

        return ApiResults.Accepted(
            $"{http.WorkflowBasePath()}/{workflowId}/runs/{continuation.Id}",
            ToRunResponse(continuation, continuationNodes));
    }

    /// <summary>计算局部重跑范围及可复用节点，供用户预览。</summary>
    internal static async Task<IActionResult> PreviewPartialRerunAsync(
        string id,
        string runId,
        string? fromNodeId,
        HttpContext http,
        IWorkflowRunRepository runs,
        CancellationToken ct)
    {
        // 预览使用与执行一致的准备逻辑，提前报告不能复用的节点状态，且不创建新运行。
        if (http.GetWorkflowDeviceId() is not { } deviceId) return ApiResults.Unauthorized();
        if (!WorkflowId.TryParse(id, out var workflowId)
            || !WorkflowRunId.TryParse(runId, out var sourceRunId))
        {
            return ApiResults.NotFound();
        }

        var source = await runs.FindAsync(deviceId, workflowId, sourceRunId, ct);
        if (source is null) return ApiResults.NotFound();
        if (source.Status is not (WorkflowRunStatus.Succeeded or WorkflowRunStatus.Failed))
        {
            return ApiResults.Conflict(new ErrorResponse(
                "workflow_not_partially_rerunnable",
                ["Only a successful or failed workflow run can be partially rerun."]));
        }

        if (await WorkflowV2Handlers.CheckResourcesAsync(source, http, ct) is { } knowledgeDenied) return knowledgeDenied;

        var sourceNodes = await runs.ListNodesAsync(source.Id, ct);
        if (!TryPreparePartialRerun(
                source,
                sourceNodes,
                fromNodeId,
                out var preparation,
                out var detail))
        {
            return PartialRerunUnavailable(detail);
        }

        return ApiResults.Ok(new WorkflowPartialRerunPreviewResponse(
            source.Id.ToString(),
            preparation.FromNodeId,
            [.. preparation.Plan.Nodes
                .Where(node => !preparation.RerunNodeIds.Contains(node.Id))
                .Select(node => node.Id)],
            [.. preparation.Plan.Nodes
                .Where(node => preparation.RerunNodeIds.Contains(node.Id))
                .Select(node => node.Id)]));
    }

    /// <summary>按已验证的局部重跑计划创建新运行。</summary>
    internal static async Task<IActionResult> PartialRerunAsync(
        string id,
        string runId,
        PartialRerunWorkflowRequest request,
        HttpContext http,
        IWorkflowRunRepository runs,
        CancellationToken ct)
    {
        // 局部重跑基于成功运行的快照创建独立运行；保留范围外的有效输出，重置需要重跑的节点。
        if (http.GetWorkflowDeviceId() is not { } deviceId) return ApiResults.Unauthorized();
        if (!WorkflowId.TryParse(id, out var workflowId)
            || !WorkflowRunId.TryParse(runId, out var sourceRunId))
        {
            return ApiResults.NotFound();
        }

        var source = await runs.FindAsync(deviceId, workflowId, sourceRunId, ct);
        if (source is null) return ApiResults.NotFound();
        if (source.Status is not (WorkflowRunStatus.Succeeded or WorkflowRunStatus.Failed))
        {
            return ApiResults.Conflict(new ErrorResponse(
                "workflow_not_partially_rerunnable",
                ["Only a successful or failed workflow run can be partially rerun."]));
        }

        if (await WorkflowV2Handlers.CheckResourcesAsync(source, http, ct) is { } knowledgeDenied) return knowledgeDenied;

        var sourceNodes = await runs.ListNodesAsync(source.Id, ct);
        if (!TryPreparePartialRerun(
                source,
                sourceNodes,
                request.FromNodeId,
                out var preparation,
                out var detail))
        {
            return PartialRerunUnavailable(detail);
        }

        var now = DateTimeOffset.UtcNow;
        var rerun = new WorkflowRun(
            WorkflowRunId.New(),
            source.WorkflowId,
            source.DeviceId,
            source.GraphJson,
            source.Id,
            WorkflowRunStatus.Queued,
            null,
            now,
            now,
            null,
            null);

        if (WorkflowGraphCompiler.IsTypedGraph(JsonNode.Parse(source.GraphJson)))
        {
            if (!WorkflowV2Handlers.Enabled(http)) return PartialRerunUnavailable("类型化工作流尚未启用。");
            rerun = rerun with { InputJson = source.InputJson };
        }
        var rerunNodes = preparation.Plan.Nodes.Select(planNode =>
        {
            var shouldRerun = preparation.RerunNodeIds.Contains(planNode.Id);
            var sourceNode = preparation.SourceNodes[planNode.Id];
            return new WorkflowNodeRun(
                rerun.Id,
                planNode.Id,
                planNode.Type,
                shouldRerun ? WorkflowNodeRunStatus.Queued : sourceNode.Status,
                !shouldRerun && sourceNode.Status == WorkflowNodeRunStatus.Succeeded,
                shouldRerun ? null : sourceNode.Progress,
                shouldRerun ? null : sourceNode.OutputJson,
                null,
                shouldRerun ? null : sourceNode.StartedAt,
                shouldRerun ? null : sourceNode.FinishedAt)
            {
                Attempt = shouldRerun ? 1 : sourceNode.Attempt,
                MaxAttempts = sourceNode.MaxAttempts,
                SkipReason = shouldRerun ? null : sourceNode.SkipReason,
            };
        }).ToList();

        if (!await runs.InsertAsync(rerun, rerunNodes, ct))
        {
            var active = await runs.FindActiveAsync(deviceId, workflowId, ct);
            return ApiResults.Conflict(new ErrorResponse(
                "workflow_already_running",
                active is null ? [] : [active.Id.ToString()]));
        }

        return ApiResults.Accepted(
            $"{http.WorkflowBasePath()}/{workflowId}/runs/{rerun.Id}",
            ToRunResponse(rerun, rerunNodes));
    }

    /// <summary>将单个失败或排队节点标记为跳过；成功节点和正在执行的外部调用不能被覆盖。</summary>
    internal static async Task<IActionResult> SkipNodeAsync(
        string id,
        string runId,
        string nodeId,
        SkipWorkflowNodeRequest request,
        HttpContext http,
        IWorkflowRunRepository runs,
        CancellationToken ct)
    {
        if (http.GetWorkflowDeviceId() is not { } deviceId) return ApiResults.Unauthorized();
        if (!WorkflowId.TryParse(id, out var workflowId) || !WorkflowRunId.TryParse(runId, out var parsedRunId)
            || string.IsNullOrWhiteSpace(nodeId) || nodeId.Length > 100) return ApiResults.NotFound();
        var reason = string.IsNullOrWhiteSpace(request.Reason) ? "用户手动跳过" : request.Reason.Trim();
        if (reason.Length > 1000) return Invalid("skip reason is too long");
        if (!await runs.SkipNodeAsync(deviceId, workflowId, parsedRunId, nodeId, reason, DateTimeOffset.UtcNow, ct))
            return ApiResults.Conflict(new ErrorResponse("workflow_node_not_skippable", ["节点不存在、已完成、正在执行或运行不在可访问范围内。"]));
        var run = await runs.FindAsync(deviceId, workflowId, parsedRunId, ct);
        if (run is null) return ApiResults.NotFound();
        return ApiResults.Ok(ToRunResponse(run, await runs.ListNodesAsync(run.Id, ct)));
    }

    /// <summary>只返回交互调试历史的状态和时间；读取某次节点正文仍走独立资源复验。</summary>
    internal static async Task<IActionResult> ListRunsAsync(string id, HttpContext http, IWorkflowRunRepository runs, CancellationToken ct)
    {
        if (http.GetWorkflowDeviceId() is not { } deviceId) return ApiResults.Unauthorized();
        if (!WorkflowId.TryParse(id, out var workflowId)) return Invalid("malformed workflow id");
        http.Response.Headers.CacheControl = "private, no-store";
        return ApiResults.Ok(new JsonObject { ["runs"] = await runs.ListInteractiveAsync(deviceId, workflowId, ct) });
    }

    /// <summary>读取工作流最近一次交互式运行。</summary>
    internal static async Task<IActionResult> GetLatestRunAsync(
        string id,
        HttpContext http,
        IWorkflowRunRepository runs,
        CancellationToken ct)
    {
        if (http.GetWorkflowDeviceId() is not { } deviceId) return ApiResults.Unauthorized();
        if (!WorkflowId.TryParse(id, out var workflowId)) return Invalid("malformed workflow id");

        var run = await runs.FindLatestAsync(deviceId, workflowId, ct);
        if (run is null) return ApiResults.NotFound();
        if (await WorkflowV2Handlers.CheckResourcesAsync(run, http, ct) is { } knowledgeDenied) return knowledgeDenied;

        return ApiResults.Ok(ToRunResponse(run, await runs.ListNodesAsync(run.Id, ct)));
    }

    /// <summary>读取指定运行及其当前持久化状态。</summary>
    internal static async Task<IActionResult> GetRunAsync(
        string id,
        string runId,
        HttpContext http,
        IWorkflowRunRepository runs,
        CancellationToken ct,
        bool snapshot = false)
    {
        if (http.GetWorkflowDeviceId() is not { } deviceId) return ApiResults.Unauthorized();
        if (!WorkflowId.TryParse(id, out var workflowId)
            || !WorkflowRunId.TryParse(runId, out var parsedRunId))
        {
            return ApiResults.NotFound();
        }

        var run = await runs.FindAsync(deviceId, workflowId, parsedRunId, ct);
        if (run is null) return ApiResults.NotFound();
        http.Response.Headers.CacheControl = "private, no-store";
        if (await WorkflowV2Handlers.CheckResourcesAsync(run, http, ct) is { } knowledgeDenied) return knowledgeDenied;
        if (snapshot)
        {
            var graph = JsonNode.Parse(run.GraphJson);
            if (await WorkflowV2Handlers.CheckResourcesAsync(run, http, ct) is { } revoked) return revoked;
            return ApiResults.Ok(new JsonObject { ["runId"] = run.Id.ToString(), ["graph"] = graph });
        }
        var nodes = await runs.ListNodesAsync(run.Id, ct);
        if (await WorkflowV2Handlers.CheckResourcesAsync(run, http, ct) is { } deniedAfterRead) return deniedAfterRead;
        return ApiResults.Ok(ToRunResponse(run, nodes));
    }

    /// <summary>请求取消指定运行，沿用当前终止状态规则。</summary>
    internal static async Task<IActionResult> CancelRunAsync(
        string id,
        string runId,
        HttpContext http,
        IWorkflowRunRepository runs,
        IRealtimeNotifier realtime,
        CancellationToken ct)
    {
        if (http.GetWorkflowDeviceId() is not { } deviceId) return ApiResults.Unauthorized();
        if (!WorkflowId.TryParse(id, out var workflowId)
            || !WorkflowRunId.TryParse(runId, out var parsedRunId))
        {
            return ApiResults.NotFound();
        }

        if (!await runs.CancelAsync(deviceId, workflowId, parsedRunId, DateTimeOffset.UtcNow, ct))
        {
            return ApiResults.NotFound();
        }

        try
        {
            await realtime.SendToDeviceAsync(
                deviceId,
                WorkflowRealtimeMethods.WorkflowRunChanged,
                new WorkflowRunChangedEvent(
                    workflowId.ToString(), parsedRunId.ToString(), "cancelled",
                    null, null, null, null, null),
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // 取消状态已经持久化，客户端会通过运行查询恢复。
        }

        return ApiResults.NoContent();
    }

    /// <summary>读取图 JSON 并校验既有 schema 与大小边界。</summary>
    private static bool TryReadGraph(JsonNode? graph, out string json, out string error)
    {
        graph ??= JsonNode.Parse(EmptyGraph);
        if (graph is not JsonObject root
            || root["nodes"] is not JsonArray
            || root["edges"] is not JsonArray)
        {
            json = EmptyGraph;
            error = "workflow graph must contain nodes and edges arrays";
            return false;
        }

        json = graph.ToJsonString();
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxGraphBytes)
        {
            json = EmptyGraph;
            error = $"workflow graph exceeds the {MaxGraphBytes / (1024 * 1024)} MB limit";
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>将业务记录映射为既有 HTTP 响应字段。</summary>
    private static WorkflowResponse ToResponse(Workflow workflow)
    {
        JsonNode? graph;
        try
        {
            graph = JsonNode.Parse(workflow.GraphJson);
        }
        catch (JsonException)
        {
            graph = null;
        }

        return new WorkflowResponse(
            workflow.Id.ToString(),
            workflow.Name,
            workflow.SchemaVersion,
            graph,
            workflow.CreatedAt.ToString("O"),
            workflow.UpdatedAt.ToString("O"))
        {
            CopyOrigin = workflow.CopiedFromSource is { } source && workflow.CopiedFromId is { } origin
                ? new WorkflowCopyOrigin(source, origin.ToString()) : null,
        };
    }

    /// <summary>将运行和节点状态映射为现有工作流响应。</summary>
    internal static WorkflowRunResponse ToRunResponse(
        WorkflowRun run,
        IReadOnlyList<WorkflowNodeRun> nodes) => new(
        run.Id.ToString(),
        run.WorkflowId.ToString(),
        run.ResumedFromRunId?.ToString(),
        RunStatus(run.Status),
        run.Error,
        run.CreatedAt.ToString("O"),
        run.UpdatedAt.ToString("O"),
        run.StartedAt?.ToString("O"),
        run.FinishedAt?.ToString("O"),
        [
            .. nodes.Select(node => new WorkflowNodeRunResponse(
                node.NodeId,
                node.NodeType,
                NodeStatus(node.Status),
                node.IsReused,
                node.Progress,
                ParseOutput(node.OutputJson),
                node.Error,
                node.StartedAt?.ToString("O"),
                node.FinishedAt?.ToString("O"))
            {
                Attempt = node.Attempt,
                MaxAttempts = node.MaxAttempts,
                RetryAt = node.RetryAt?.ToString("O"),
                SkipReason = node.SkipReason,
            }),
        ]) { DebugNodeId = WorkflowNodeDebug.Target(run.InputJson) };

    /// <summary>判断成功节点的已保存输出是否可以安全恢复。</summary>
    private static bool CanReuseOutput(string? json)
    {
        if (json is null) return false;

        try
        {
            if (JsonNode.Parse(json) is not JsonObject output) return false;
            return IsStringArray(output["texts"])
                   && IsStringArray(output["images"])
                   && IsStringArray(output["videos"]);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>判断 JSON 值是否为当前节点输出要求的字符串数组。</summary>
    private static bool IsStringArray(JsonNode? node) =>
        node is JsonArray values
        && values.All(value => value is JsonValue item && item.TryGetValue<string>(out _));

    /// <summary>返回原有无法续跑的冲突响应。</summary>
    private static IActionResult ResumeUnavailable(string detail) =>
        ApiResults.Conflict(new ErrorResponse("workflow_resume_unavailable", [detail]));

    /// <summary>验证源运行快照和节点输出，生成局部重跑范围与复用状态。</summary>
    private static bool TryPreparePartialRerun(
        WorkflowRun source,
        IReadOnlyList<WorkflowNodeRun> sourceNodeRuns,
        string? fromNodeId,
        out PartialRerunPreparation preparation,
        out string detail)
    {
        // 复用范围由源运行的图和起点决定；范围外每个节点都必须有可解析的成功输出或明确跳过状态。
        preparation = default!;
        detail = string.Empty;
        if (WorkflowNodeDebug.Target(source.InputJson) is not null) { detail = "单节点调试不能作为整图重跑来源。"; return false; }
        if (string.IsNullOrWhiteSpace(fromNodeId))
        {
            detail = "A rerun start node is required.";
            return false;
        }

        JsonNode? graph;
        try
        {
            graph = JsonNode.Parse(source.GraphJson);
        }
        catch (JsonException)
        {
            detail = "The workflow run snapshot is invalid.";
            return false;
        }

        var validation = WorkflowGraphCompiler.Validate(graph);
        if (!validation.IsValid || validation.Plan is null)
        {
            detail = "The workflow run snapshot is no longer valid.";
            return false;
        }

        var rerunNodeIds = WorkflowPartialRerunPlanner.FindRerunNodeIds(
            validation.Plan,
            fromNodeId);
        if (rerunNodeIds is null)
        {
            detail = $"Node '{fromNodeId}' does not exist in the successful run.";
            return false;
        }

        var sourceNodes = sourceNodeRuns.ToDictionary(
            node => node.NodeId,
            StringComparer.Ordinal);
        foreach (var planNode in validation.Plan.Nodes)
        {
            if (!sourceNodes.TryGetValue(planNode.Id, out var sourceNode)
                || !string.Equals(sourceNode.NodeType, planNode.Type, StringComparison.Ordinal))
            {
                detail = $"The stored state for node '{planNode.Id}' cannot be restored.";
                return false;
            }

            if (rerunNodeIds.Contains(planNode.Id)) continue;
            // 成功 v2 运行中的未命中分支是合法终态；保留 skipped，不把它伪造成有输出的成功节点。
            if (WorkflowGraphCompiler.IsTypedGraph(graph) && sourceNode.Status == WorkflowNodeRunStatus.Skipped
                && sourceNode.StartedAt is null && sourceNode.OutputJson is null) continue;
            if (sourceNode.Status != WorkflowNodeRunStatus.Succeeded
                || !(WorkflowGraphCompiler.IsTypedGraph(graph) ? WorkflowV2Values.TryRestore(planNode, sourceNode.OutputJson, out _) : CanReuseOutput(sourceNode.OutputJson)))
            {
                detail = $"The completed output for node '{planNode.Id}' cannot be restored.";
                return false;
            }
        }

        preparation = new PartialRerunPreparation(
            fromNodeId,
            validation.Plan,
            rerunNodeIds,
            sourceNodes);
        return true;
    }

    /// <summary>返回原有无法局部重跑的冲突响应。</summary>
    private static IActionResult PartialRerunUnavailable(string detail) =>
        ApiResults.Conflict(new ErrorResponse("workflow_partial_rerun_unavailable", [detail]));

    /// <summary>读取已保存的节点输出 JSON，保持原有容错。</summary>
    private static JsonNode? ParseOutput(string? json)
    {
        if (json is null) return null;
        try { return JsonNode.Parse(json); }
        catch (JsonException) { return null; }
    }

    /// <summary>将运行枚举转换为当前接口状态字符串。</summary>
    private static string RunStatus(WorkflowRunStatus status) => status switch
    {
        WorkflowRunStatus.Queued => "queued",
        WorkflowRunStatus.Running => "running",
        WorkflowRunStatus.Succeeded => "succeeded",
        WorkflowRunStatus.Failed => "failed",
        _ => "cancelled",
    };

    /// <summary>将节点运行枚举映射为既有接口名称。</summary>
    private static string NodeStatus(WorkflowNodeRunStatus status) => status switch
    {
        WorkflowNodeRunStatus.Queued => "queued",
        WorkflowNodeRunStatus.Running => "running",
        WorkflowNodeRunStatus.Retrying => "retrying",
        WorkflowNodeRunStatus.Succeeded => "succeeded",
        WorkflowNodeRunStatus.Failed => "failed",
        WorkflowNodeRunStatus.Cancelled => "cancelled",
        _ => "skipped",
    };

    /// <summary>按既有缺省值和长度规则规范化显示名称。</summary>
    private static string NormalizeName(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "未命名工作流" : Truncate(value.Trim());

    /// <summary>按当前组件的既有上限截断文本。</summary>
    private static string Truncate(string value) =>
        value.Length <= MaxNameLength ? value : value[..MaxNameLength];

    /// <summary>将校验失败映射为当前功能既有的错误结果，保留详情和错误分类。</summary>
    private static IActionResult Invalid(string detail) =>
        ApiResults.BadRequest(new ErrorResponse("invalid_request", [detail]));

    /// <param name="FromNodeId">开始执行或局部重跑的源节点标识。</param>
    /// <param name="Plan">通过校验后可执行的节点或操作计划。</param>
    /// <param name="RerunNodeIds">本次需要重新执行的节点标识集合。</param>
    /// <param name="SourceNodes">原运行保存的节点状态集合。</param>
    private sealed record PartialRerunPreparation(
        string FromNodeId,
        WorkflowPlan Plan,
        IReadOnlySet<string> RerunNodeIds,
        IReadOnlyDictionary<string, WorkflowNodeRun> SourceNodes);
}
