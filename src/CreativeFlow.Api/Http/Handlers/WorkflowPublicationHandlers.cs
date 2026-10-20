using System.Text.Json;
using System.Text.Json.Nodes;
using CreativeFlow.Api.Middleware;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Abstractions.Security;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Application.Features.Workflow;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Infrastructure.Options;

namespace CreativeFlow.Api.Http.Handlers;

internal static class WorkflowPublicationHandlers
{
    /// <summary>验证工作流发布并保存不可变的发布快照。</summary>
    internal static async Task<IActionResult> PublishAsync(
        string id,
        PublishWorkflowRequest request,
        HttpContext http,
        IWorkflowRepository workflows,
        IWorkflowExternalApiRepository externalApi,
        ExternalWorkflowApiOptions options,
        CancellationToken ct)
    {
        // 发布先验证可对外调用的输入输出契约，再保存独立版本，避免外部调用跟随草稿变化。
        if (!http.IsAdminWorkflowApi()) return ApiResults.Unauthorized();
        if (!options.Enabled) return ApiResults.NotFound();
        if (http.GetWorkflowDeviceId() is not { } deviceId) return ApiResults.Unauthorized();
        if (!WorkflowId.TryParse(id, out var workflowId)) return Invalid("malformed workflow id");

        var workflow = await workflows.FindAsync(deviceId, workflowId, ct);
        if (workflow is null) return ApiResults.NotFound();
        if (workflow.SchemaVersion != WorkflowGraphValidator.SchemaVersion) return Invalid("unsupported workflow schema version");

        JsonNode? graph;
        try { graph = JsonNode.Parse(workflow.GraphJson); }
        catch (JsonException) { return Invalid("stored workflow graph is invalid JSON"); }

        var typedGraph = WorkflowGraphCompiler.IsTypedGraph(graph);
        if (typedGraph && !WorkflowV2Handlers.Enabled(http)) return Invalid("类型化工作流尚未启用。");
        string inputSchema;
        string outputSchema;
        string? executionSnapshot = null;
        if (typedGraph)
        {
            if (http.GetAuthenticatedAdmin() is not { } admin) return ApiResults.Unauthorized();
            try
            {
                var (plan, inputs, outputs) = WorkflowV2PublicationService.Contract(workflow.GraphJson);
                inputSchema = inputs.ToJsonString(); outputSchema = outputs.ToJsonString();
                executionSnapshot = (await http.RequestServices.GetRequiredService<WorkflowV2PublicationService>()
                    .CaptureAsync(plan, new("admin", admin.Id, deviceId.Value), ct)).ToJsonString();
            }
            catch (WorkflowV2Exception exception) { return Invalid(exception.Message); }
            catch (WorkflowHttpException exception) { return Invalid(exception.Message); }
        }
        else
        {
            var validation = WorkflowExternalContractValidator.Validate(graph);
            if (!validation.IsValid || validation.Contract is null)
                return ApiResults.BadRequest(new ErrorResponse("invalid_external_workflow", [.. validation.Errors]));
            inputSchema = validation.Contract.InputSchemaJson;
            outputSchema = validation.Contract.OutputSchemaJson;
        }

        var description = request.Description?.Trim() ?? string.Empty;
        if (description.Length > 1000) return Invalid("description exceeds 1000 characters");
        var maxConcurrency = request.MaxConcurrency ?? options.DefaultWorkflowConcurrency;
        if (maxConcurrency is < 1 or > 32)
            return Invalid("maxConcurrency must be between 1 and 32");

        var now = DateTimeOffset.UtcNow;
        var deployment = new WorkflowDeployment(
            WorkflowDeploymentId.New(),
            workflow.Id,
            0,
            WorkflowGraphValidator.SchemaVersion,
            workflow.GraphJson,
            inputSchema,
            outputSchema,
            description,
            WorkflowDeploymentStatus.Published,
            maxConcurrency,
            now,
            now,
            null) { ExecutionSnapshotJson = executionSnapshot };

        try
        {
            var published = await externalApi.PublishAsync(deviceId, deployment, ct);
            return published is null ? ApiResults.NotFound() : ApiResults.Ok(ToResponse(published));
        }
        catch (WorkflowV2Exception exception) { return Invalid(exception.Message); }
    }

    /// <summary>读取工作流已有的发布版本。</summary>
    internal static async Task<IActionResult> ListDeploymentsAsync(
        string id,
        HttpContext http,
        IWorkflowExternalApiRepository externalApi,
        ExternalWorkflowApiOptions options,
        CancellationToken ct)
    {
        if (!http.IsAdminWorkflowApi()) return ApiResults.Unauthorized();
        if (!options.Enabled) return ApiResults.NotFound();
        if (http.GetWorkflowDeviceId() is not { } deviceId) return ApiResults.Unauthorized();
        if (!WorkflowId.TryParse(id, out var workflowId)) return Invalid("malformed workflow id");
        var deployments = await externalApi.ListDeploymentsAsync(deviceId, workflowId, ct);
        return ApiResults.Ok(new WorkflowDeploymentListResponse(
            [.. deployments.Select(ToResponse)]));
    }

    /// <summary>停用工作流的对外发布入口。</summary>
    internal static async Task<IActionResult> DisableAsync(
        string id,
        HttpContext http,
        IWorkflowExternalApiRepository externalApi,
        ExternalWorkflowApiOptions options,
        CancellationToken ct)
    {
        if (!http.IsAdminWorkflowApi()) return ApiResults.Unauthorized();
        if (!options.Enabled) return ApiResults.NotFound();
        if (http.GetWorkflowDeviceId() is not { } deviceId) return ApiResults.Unauthorized();
        if (!WorkflowId.TryParse(id, out var workflowId)) return Invalid("malformed workflow id");
        return await externalApi.DisableAsync(deviceId, workflowId, DateTimeOffset.UtcNow, ct)
            ? ApiResults.NoContent()
            : ApiResults.NotFound();
    }

    /// <summary>为工作流创建 API Key，并按原规则仅在签发时返回明文。</summary>
    internal static async Task<IActionResult> CreateApiKeyAsync(
        string id,
        CreateWorkflowApiKeyRequest request,
        HttpContext http,
        IWorkflowRepository workflows,
        IWorkflowExternalApiRepository externalApi,
        IWorkflowApiKeyService keyService,
        ExternalWorkflowApiOptions options,
        CancellationToken ct)
    {
        // 只在后台已验证的资源范围内签发密钥，并继续要求存在有效发布版本。
        if (!http.IsAdminWorkflowApi()) return ApiResults.Unauthorized();
        if (!options.Enabled) return ApiResults.NotFound();
        if (http.GetWorkflowDeviceId() is not { } deviceId) return ApiResults.Unauthorized();
        if (!WorkflowId.TryParse(id, out var workflowId)) return Invalid("malformed workflow id");
        if (await workflows.FindAsync(deviceId, workflowId, ct) is null) return ApiResults.NotFound();
        if (await externalApi.FindPublishedAsync(deviceId, workflowId, ct) is null)
            return ApiResults.Conflict(new ErrorResponse(
                "workflow_not_published", ["publish the workflow before creating an API key"]));

        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > 100) return Invalid("API key name must be between 1 and 100 characters");
        DateTimeOffset? expiresAt = null;
        if (!string.IsNullOrWhiteSpace(request.ExpiresAt))
        {
            if (!DateTimeOffset.TryParse(request.ExpiresAt, out var parsed)
                || parsed <= DateTimeOffset.UtcNow)
                return Invalid("expiresAt must be a future ISO-8601 timestamp");
            expiresAt = parsed.ToUniversalTime();
        }

        var material = keyService.Generate();
        var now = DateTimeOffset.UtcNow;
        var apiKey = new WorkflowApiKey(
            WorkflowApiKeyId.New(), workflowId, name, material.Prefix, material.Hint,
            material.Hash, true, expiresAt, null, now, null);
        await externalApi.InsertApiKeyAsync(apiKey, ct);
        return ApiResults.Created(
            $"{http.WorkflowBasePath()}/{workflowId}/api-keys/{apiKey.Id}",
            ToResponse(apiKey, material.Plaintext));
    }

    /// <summary>列出工作流 API Key 的安全元数据。</summary>
    internal static async Task<IActionResult> ListApiKeysAsync(
        string id,
        HttpContext http,
        IWorkflowExternalApiRepository externalApi,
        ExternalWorkflowApiOptions options,
        CancellationToken ct)
    {
        if (!http.IsAdminWorkflowApi()) return ApiResults.Unauthorized();
        if (!options.Enabled) return ApiResults.NotFound();
        if (http.GetWorkflowDeviceId() is not { } deviceId) return ApiResults.Unauthorized();
        if (!WorkflowId.TryParse(id, out var workflowId)) return Invalid("malformed workflow id");
        var keys = await externalApi.ListApiKeysAsync(deviceId, workflowId, ct);
        return ApiResults.Ok(new WorkflowApiKeyListResponse(
            [.. keys.Select(key => ToResponse(key, null))]));
    }

    /// <summary>撤销指定工作流 API Key。</summary>
    internal static async Task<IActionResult> RevokeApiKeyAsync(
        string id,
        string keyId,
        HttpContext http,
        IWorkflowExternalApiRepository externalApi,
        ExternalWorkflowApiOptions options,
        CancellationToken ct)
    {
        if (!http.IsAdminWorkflowApi()) return ApiResults.Unauthorized();
        if (!options.Enabled) return ApiResults.NotFound();
        if (http.GetWorkflowDeviceId() is not { } deviceId) return ApiResults.Unauthorized();
        if (!WorkflowId.TryParse(id, out var workflowId)
            || !Guid.TryParse(keyId, out var parsedKeyId)) return ApiResults.NotFound();
        return await externalApi.RevokeApiKeyAsync(
            deviceId,
            workflowId,
            new WorkflowApiKeyId(parsedKeyId),
            DateTimeOffset.UtcNow,
            ct)
                ? ApiResults.NoContent()
                : ApiResults.NotFound();
    }

    /// <summary>列出工作流由外部 API 触发的运行。</summary>
    internal static async Task<IActionResult> ListExternalRunsAsync(
        string id,
        HttpContext http,
        IWorkflowRunRepository runs,
        ExternalWorkflowApiOptions options,
        CancellationToken ct)
    {
        if (!http.IsAdminWorkflowApi()) return ApiResults.Unauthorized();
        if (!options.Enabled) return ApiResults.NotFound();
        if (http.GetWorkflowDeviceId() is not { } deviceId) return ApiResults.Unauthorized();
        if (!WorkflowId.TryParse(id, out var workflowId)) return Invalid("malformed workflow id");
        var history = await runs.ListExternalAsync(deviceId, workflowId, 100, ct);
        return ApiResults.Ok(new ExternalWorkflowRunListResponse(
        [
            .. history.Select(item => new ExternalWorkflowRunSummaryResponse(
                item.Run.Id.ToString(),
                item.Run.RequestId ?? string.Empty,
                item.ApiKeyName,
                item.DeploymentVersion,
                RunStatus(item.Run.Status),
                item.Run.CreatedAt.ToString("O"),
                item.Run.StartedAt?.ToString("O"),
                item.Run.FinishedAt?.ToString("O"),
                item.Run.Error)),
        ]));
    }

    /// <summary>后台只读取已通过管理权限范围校验的外部运行；资源授权在载入节点前后复验。</summary>
    internal static async Task<IActionResult> GetExternalRunAsync(string id, string runId, HttpContext http,
        IWorkflowRepository workflows, IWorkflowRunRepository runs, ExternalWorkflowApiOptions options, CancellationToken ct, bool snapshot = false)
    {
        if (!http.IsAdminWorkflowApi() || http.GetWorkflowDeviceId() is not { } deviceId) return ApiResults.Unauthorized();
        if (!options.Enabled || !WorkflowId.TryParse(id, out var workflowId) || !WorkflowRunId.TryParse(runId, out var parsed)) return ApiResults.NotFound();
        if (await workflows.FindAsync(deviceId, workflowId, ct) is null) return ApiResults.NotFound();
        var run = await runs.FindExternalAsync(workflowId, parsed, ct);
        if (run is null) return ApiResults.NotFound();
        http.Response.Headers.CacheControl = "private, no-store";
        if (await WorkflowV2Handlers.CheckResourcesAsync(run, http, ct) is { } denied) return denied;
        if (snapshot)
        {
            var graph = JsonNode.Parse(run.GraphJson);
            if (await WorkflowV2Handlers.CheckResourcesAsync(run, http, ct) is { } revokedSnapshot) return revokedSnapshot;
            return ApiResults.Ok(new JsonObject { ["runId"] = run.Id.ToString(), ["graph"] = graph });
        }
        var nodes = await runs.ListNodesAsync(run.Id, ct);
        if (await WorkflowV2Handlers.CheckResourcesAsync(run, http, ct) is { } revoked) return revoked;
        return ApiResults.Ok(WorkflowHandlers.ToRunResponse(run, nodes));
    }

    /// <summary>将业务记录映射为既有 HTTP 响应字段。</summary>
    internal static WorkflowDeploymentResponse ToResponse(WorkflowDeployment deployment)
    {
        // 旧快照可能保存了未补齐约束的原始数组；只在读取时投影，不改写不可变发布记录。
        var contract = deployment.SchemaVersion == 1 ? WorkflowExternalContractValidator.Validate(Parse(deployment.GraphJson)).Contract : null;
        return new(
            deployment.Id.ToString(),
            deployment.WorkflowId.ToString(),
            deployment.Version,
            deployment.SchemaVersion,
            deployment.Description,
            deployment.Status == WorkflowDeploymentStatus.Published ? "published" : "disabled",
            deployment.MaxConcurrency,
            Parse(contract?.InputSchemaJson ?? deployment.InputSchemaJson),
            Parse(contract?.OutputSchemaJson ?? deployment.OutputSchemaJson),
            deployment.PublishedAt.ToString("O"),
            deployment.DisabledAt?.ToString("O"));
    }

    /// <summary>将业务记录映射为既有 HTTP 响应字段。</summary>
    private static WorkflowApiKeyResponse ToResponse(WorkflowApiKey apiKey, string? plaintext) => new(
        apiKey.Id.ToString(),
        apiKey.Name,
        apiKey.KeyPrefix,
        apiKey.KeyHint,
        apiKey.Enabled,
        apiKey.ExpiresAt?.ToString("O"),
        apiKey.LastUsedAt?.ToString("O"),
        apiKey.CreatedAt.ToString("O"),
        apiKey.RevokedAt?.ToString("O"),
        plaintext);

    /// <summary>按当前协议解析 JSON 响应并应用既有回退规则。</summary>
    private static JsonNode? Parse(string json)
    {
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

    /// <summary>将校验失败映射为当前功能既有的错误结果，保留详情和错误分类。</summary>
    private static IActionResult Invalid(string detail) =>
        ApiResults.BadRequest(new ErrorResponse("invalid_request", [detail]));
}
