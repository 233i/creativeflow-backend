using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Abstractions.Security;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Application.Features.Workflow;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Infrastructure.Options;

namespace CreativeFlow.Api.Http.Handlers;

internal static partial class ExternalWorkflowHandlers
{
    /// <summary>外部图片上传允许的 MIME 类型集合。</summary>
    private static readonly string[] AllowedImageTypes =
        ["image/png", "image/jpeg", "image/webp"];

    /// <summary>列出对外公开的已发布工作流目录。</summary>
    internal static async Task<IActionResult> ListCatalogAsync(
        IWorkflowExternalApiRepository externalApi,
        ExternalWorkflowApiOptions options,
        CancellationToken ct)
    {
        if (!options.Enabled) return ApiResults.NotFound();
        var items = await externalApi.ListPublishedAsync(100, ct);
        return ApiResults.Ok(new ExternalWorkflowCatalogResponse(
            [.. items.Select(ToCatalogResponse)]));
    }

    /// <summary>读取指定已发布工作流的公开契约。</summary>
    internal static async Task<IActionResult> GetCatalogItemAsync(
        string id,
        IWorkflowExternalApiRepository externalApi,
        ExternalWorkflowApiOptions options,
        CancellationToken ct)
    {
        if (!options.Enabled) return ApiResults.NotFound();
        if (!WorkflowId.TryParse(id, out var workflowId)) return ApiResults.NotFound();
        var item = await externalApi.FindPublishedAsync(workflowId, ct);
        return item is null ? ApiResults.NotFound() : ApiResults.Ok(ToCatalogResponse(item));
    }

    /// <summary>按外部工作流的输入限制验证并保存图片。</summary>
    internal static async Task<IActionResult> UploadImageAsync(
        IFormFile file,
        HttpContext http,
        IWorkflowExternalApiRepository externalApi,
        IWorkflowApiKeyService keyService,
        ExternalWorkflowRateLimiter rateLimiter,
        IAssetStore assetStore,
        ICanvasAssetRepository assets,
        ExternalWorkflowApiOptions options,
        CancellationToken ct)
    {
        // 外部上传采用 API Key 对应的工作流范围；尺寸和媒体检查通过后才保存可引用素材。
        var requestId = NewRequestId();
        if (!options.Enabled) return Error(404, requestId, "workflow_api_disabled", "External workflow API is disabled.");
        var apiKey = await AuthenticateAsync(http, externalApi, keyService, ct);
        if (apiKey is null) return Unauthorized(requestId);
        if (!rateLimiter.TryAcquire(
                apiKey.Id, ExternalWorkflowRateLimitKind.UploadImage, out var retryAfter))
            return RateLimited(http, requestId, retryAfter);
        var catalog = await externalApi.FindPublishedAsync(apiKey.WorkflowId, ct);
        if (catalog is null) return Error(404, requestId, "workflow_not_published", "The workflow is not published.");

        if (file.Length == 0) return Invalid(requestId, "file", "empty", "The uploaded image is empty.");
        if (file.Length > options.MaxImageBytes)
            return Error(413, requestId, "image_too_large", "The image exceeds the upload limit.");
        var mediaType = file.ContentType?.Split(';')[0].Trim().ToLowerInvariant() ?? string.Empty;
        if (!AllowedImageTypes.Contains(mediaType, StringComparer.Ordinal))
            return Error(400, requestId, "unsupported_media_type", "Only PNG, JPEG and WebP images are accepted.");

        await using var input = file.OpenReadStream();
        if (!WorkflowExternalImageValidator.TryInspect(
                input,
                mediaType,
                options.MaxImageDimension,
                options.MaxImagePixels,
                out _,
                out var imageError, ct))
            return Error(400, requestId, "invalid_image", imageError);

        var stored = await assetStore.SaveAsync(input, mediaType, ExtensionFor(mediaType), options.MaxImageBytes, ct);
        var now = DateTimeOffset.UtcNow;
        var assetId = Guid.CreateVersion7();
        await assets.InsertAsync(new CanvasAsset(
            assetId,
            catalog.OwnerDeviceId,
            "image",
            stored.MediaType,
            stored.StoragePath,
            stored.ByteSize,
            null,
            now), ct);
        var expires = now.AddHours(options.UploadedAssetLifetimeHours);
        await externalApi.InsertExternalAssetAsync(new WorkflowExternalAsset(
            assetId, apiKey.WorkflowId, apiKey.Id, now, expires, null), ct);

        return ApiResults.Ok(new ExternalImageUploadResponse(
            assetId.ToString(), stored.MediaType, stored.ByteSize, now.ToString("O"), expires.ToString("O")));
    }

    /// <summary>校验输入并创建持久化运行，随后由既有后台执行机制推进。</summary>
    internal static async Task<IActionResult> CreateRunAsync(
        string id,
        CreateExternalWorkflowRunRequest request,
        HttpContext http,
        IWorkflowExternalApiRepository externalApi,
        IWorkflowRunRepository runs,
        IWorkflowApiKeyService keyService,
        ExternalWorkflowRateLimiter rateLimiter,
        ICanvasAssetRepository assets,
        ExternalWorkflowApiOptions options,
        CancellationToken ct, JsonObject? agui = null)
    {
        var requestId = NewRequestId();
        if (!options.Enabled) return Error(404, requestId, "workflow_api_disabled", "External workflow API is disabled.");
        var apiKey = await AuthenticateAsync(http, externalApi, keyService, ct);
        if (apiKey is null) return Unauthorized(requestId);
        if (!rateLimiter.TryAcquire(
                apiKey.Id, ExternalWorkflowRateLimitKind.CreateRun, out var retryAfter))
            return RateLimited(http, requestId, retryAfter);
        if (!WorkflowId.TryParse(id, out var workflowId))
            return Error(404, requestId, "workflow_not_published", "The workflow is not published.");
        if (apiKey.WorkflowId != workflowId)
            return Error(403, requestId, "workflow_access_denied", "This API key cannot access the workflow.");

        var idempotencyKey = http.Request.Headers["Idempotency-Key"].ToString().Trim();
        if (idempotencyKey.Length is 0 or > 200)
            return Invalid(requestId, "Idempotency-Key", "required", "A 1-200 character Idempotency-Key header is required.");

        var existing = await runs.FindByIdempotencyAsync(apiKey.Id, idempotencyKey, ct);
        if (existing is not null)
            return await ReplayRunAsync(existing, request.Inputs, requestId, http, externalApi, ct, request.Stream, agui);

        var catalog = await externalApi.FindPublishedAsync(workflowId, ct);
        if (catalog is null)
            return Error(404, requestId, "workflow_not_published", "The workflow is not published.");

        if (WorkflowGraphCompiler.IsTypedGraph(Parse(catalog.Deployment.GraphJson)))
            return await CreateTypedRunAsync(catalog, apiKey, request.Inputs, idempotencyKey, requestId, http, externalApi, runs, assets, options, ct, request.Stream, agui);

        var requestHash = HashRequest(catalog.Deployment.Id, request.Inputs, request.Stream, agui);

        var contractResult = WorkflowExternalContractValidator.Validate(
            Parse(catalog.Deployment.GraphJson));
        if (!contractResult.IsValid || contractResult.Contract is null)
            return Error(503, requestId, "workflow_temporarily_unavailable", "The published workflow contract is invalid.");

        var imageUrls = new Dictionary<string, string>(StringComparer.Ordinal);
        var consumedAssetIds = new List<Guid>();
        foreach (var pair in WorkflowExternalContractValidator.ReadImageAssetIds(
                     contractResult.Contract, request.Inputs))
        {
            if (!Guid.TryParse(pair.Value, out var assetId)
                || await externalApi.FindExternalAssetAsync(workflowId, assetId, ct) is null
                || await assets.FindAsync(catalog.OwnerDeviceId, assetId, ct) is not { Kind: "image", IsRecycled: false } asset
                || !asset.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                return Invalid(requestId, $"inputs.{pair.Key}", "invalid_asset", "The image is unavailable for this workflow.");
            }
            imageUrls[pair.Key] = $"/api/v1/canvas/assets/{assetId}";
            consumedAssetIds.Add(assetId);
        }

        var binding = WorkflowExternalContractValidator.Bind(
            catalog.Deployment.GraphJson, request.Inputs, imageUrls);
        if (!binding.IsValid || binding.GraphJson is null || binding.InputJson is null)
        {
            return new ObjectResult(new ExternalApiErrorBody(
                requestId,
                new ExternalApiError(
                    "invalid_input",
                    "External workflow inputs are invalid.",
                    [.. binding.Errors.Select(detail => new ExternalApiErrorDetail("inputs", "invalid", detail))])))
            { StatusCode = StatusCodes.Status400BadRequest };
        }

        var graph = Parse(binding.GraphJson);
        var graphValidation = WorkflowGraphValidator.Validate(graph);
        if (!graphValidation.IsValid || graphValidation.Plan is null)
            return Error(503, requestId, "workflow_temporarily_unavailable", "The published workflow cannot be executed.");

        var now = DateTimeOffset.UtcNow;
        var run = new WorkflowRun(
            WorkflowRunId.New(),
            workflowId,
            catalog.OwnerDeviceId,
            binding.GraphJson,
            null,
            WorkflowRunStatus.Queued,
            null,
            now,
            now,
            null,
            null)
        {
            TriggerType = WorkflowRunTriggerType.Api,
            DeploymentId = catalog.Deployment.Id,
            ApiKeyId = apiKey.Id,
            RequestId = requestId,
            IdempotencyKey = idempotencyKey,
            RequestHash = requestHash,
            InputJson = WithAgUi(binding.InputJson, agui),
        };
        var nodeRuns = graphValidation.Plan.Nodes.Select(node => new WorkflowNodeRun(
            run.Id, node.Id, node.Type, WorkflowNodeRunStatus.Queued, false,
            null, null, null, null, null)).ToList();

        var insertResult = await runs.InsertExternalAsync(
            run, nodeRuns, options.MaxQueuedRunsPerWorkflow, ct);
        if (insertResult is WorkflowRunInsertResult.QueueFull or WorkflowRunInsertResult.Duplicate)
        {
            // 等待准入时，另一个请求可能已用原发布版本创建运行；所有重放分支都按该运行比较。
            existing = await runs.FindByIdempotencyAsync(apiKey.Id, idempotencyKey, ct);
            if (existing is not null)
                return await ReplayRunAsync(existing, request.Inputs, requestId, http, externalApi, ct, request.Stream, agui);
            return insertResult == WorkflowRunInsertResult.QueueFull
                ? Error(409, requestId, "workflow_queue_full", "The workflow queue is full.")
                : Error(409, requestId, "idempotency_conflict", "The idempotency key is already in use.");
        }

        await externalApi.MarkExternalAssetsConsumedAsync(
            workflowId, consumedAssetIds, now, ct);
        return ApiResults.Accepted(
            $"/api/external/v1/runs/{run.Id}",
            ToCreated(run, catalog.Deployment.Version));
    }

    /// <summary>按首次受理时固定的发布版本验证重试，并返回同一运行及原版本号。</summary>
    private static async Task<IActionResult> ReplayRunAsync(
        WorkflowRun run,
        JsonObject? inputs,
        string requestId,
        HttpContext http,
        IWorkflowExternalApiRepository externalApi,
        CancellationToken ct, bool stream = false, JsonObject? agui = null)
    {
        if (run.DeploymentId is not { } deploymentId)
            return Error(503, requestId, "workflow_temporarily_unavailable", "The run has no deployment metadata.");
        if (!CryptographicOperations.FixedTimeEquals(HashRequest(deploymentId, inputs, stream, agui), run.RequestHash ?? []))
            return Error(409, requestId, "idempotency_conflict", "The idempotency key was already used with different inputs.");
        var original = await externalApi.FindDeploymentAsync(deploymentId, ct);
        if (original is not null && WorkflowGraphCompiler.IsTypedGraph(Parse(original.Deployment.GraphJson)) && await CheckTypedRunAsync(run, http, requestId, ct) is { } denied) return denied;
        return original is null
            ? Error(503, requestId, "workflow_temporarily_unavailable", "The run deployment is unavailable.")
            : ApiResults.Accepted($"/api/external/v1/runs/{run.Id}", ToCreated(run, original.Deployment.Version));
    }

    /// <summary>读取指定运行及其当前持久化状态。</summary>
    internal static async Task<IActionResult> GetRunAsync(
        string runId,
        HttpContext http,
        IWorkflowExternalApiRepository externalApi,
        IWorkflowRunRepository runs,
        IWorkflowApiKeyService keyService,
        ExternalWorkflowRateLimiter rateLimiter,
        ICanvasAssetRepository assets,
        ISignedMediaUrlService signedUrls,
        ExternalWorkflowApiOptions options,
        CancellationToken ct, bool streamRead = false)
    {
        // 运行绑定创建时的发布版本；即使当前工作流重新发布，也要按原版本构造状态与输出。
        var requestId = NewRequestId();
        if (!options.Enabled) return Error(404, requestId, "workflow_api_disabled", "External workflow API is disabled.");
        var apiKey = await AuthenticateAsync(http, externalApi, keyService, ct, touch: !streamRead);
        if (apiKey is null) return Unauthorized(requestId);
        if (!streamRead && !rateLimiter.TryAcquire(
                apiKey.Id, ExternalWorkflowRateLimitKind.ReadStatus, out var retryAfter))
            return RateLimited(http, requestId, retryAfter);
        if (!WorkflowRunId.TryParse(runId, out var parsedRunId))
            return Error(404, requestId, "run_not_found", "The workflow run was not found.");
        var run = await runs.FindExternalAsync(apiKey.WorkflowId, parsedRunId, ct);
        if (run is null)
            return Error(404, requestId, "run_not_found", "The workflow run was not found.");
        if (run.DeploymentId is not { } deploymentId)
            return Error(503, requestId, "workflow_temporarily_unavailable", "The run has no deployment metadata.");
        var catalog = await externalApi.FindDeploymentAsync(deploymentId, ct);
        if (catalog is null)
            return Error(503, requestId, "workflow_temporarily_unavailable", "The run deployment is unavailable.");

        var typedGraph = WorkflowGraphCompiler.IsTypedGraph(Parse(catalog.Deployment.GraphJson));
        if (typedGraph)
        {
            if (run.ApiKeyId != apiKey.Id) return Error(404, requestId, "run_not_found", "The workflow run was not found.");
            if (await CheckTypedRunAsync(run, http, requestId, ct) is { } denied) return denied;
        }
        var nodes = await runs.ListNodesAsync(run.Id, ct);
        JsonObject? outputs;
        try
        {
            outputs = run.Status != WorkflowRunStatus.Succeeded ? null : typedGraph
                ? WorkflowV2PublicationService.Outputs(catalog.Deployment, nodes)
                : await BuildOutputsAsync(catalog, nodes, assets, signedUrls, ct);
        }
        catch (WorkflowV2Exception) { return Error(503, requestId, "invalid_run_output", "The run output does not match the published contract."); }
        if (typedGraph && await CheckTypedRunAsync(run, http, requestId, ct) is { } revoked) return revoked;
        var current = nodes.FirstOrDefault(node => node.Status == WorkflowNodeRunStatus.Running);
        var progress = nodes.Count == 0
            ? 0
            : nodes.Average(NodeProgress);

        return ApiResults.Ok(new ExternalWorkflowRunResponse(
            run.RequestId ?? requestId,
            run.Id.ToString(),
            run.WorkflowId.ToString(),
            catalog.Deployment.Version,
            RunStatus(run.Status),
            progress,
            current is null ? null : new ExternalWorkflowCurrentNodeResponse(
                current.NodeId, current.NodeType, WorkflowNodeDisplayNames.Resolve(run.GraphJson, current.NodeId, current.NodeType)),
            run.CreatedAt.ToString("O"),
            run.StartedAt?.ToString("O"),
            run.FinishedAt?.ToString("O"),
            run.Error,
            outputs));
    }

    /// <summary>请求取消指定运行，沿用当前终止状态规则。</summary>
    internal static async Task<IActionResult> CancelRunAsync(
        string runId,
        HttpContext http,
        IWorkflowExternalApiRepository externalApi,
        IWorkflowRunRepository runs,
        IWorkflowApiKeyService keyService,
        ExternalWorkflowRateLimiter rateLimiter,
        ExternalWorkflowApiOptions options,
        CancellationToken ct)
    {
        var requestId = NewRequestId();
        if (!options.Enabled) return Error(404, requestId, "workflow_api_disabled", "External workflow API is disabled.");
        var apiKey = await AuthenticateAsync(http, externalApi, keyService, ct);
        if (apiKey is null) return Unauthorized(requestId);
        if (!rateLimiter.TryAcquire(
                apiKey.Id, ExternalWorkflowRateLimitKind.CreateRun, out var retryAfter))
            return RateLimited(http, requestId, retryAfter);
        if (!WorkflowRunId.TryParse(runId, out var parsedRunId))
            return Error(404, requestId, "run_not_found", "The workflow run was not found.");
        var existing = await runs.FindExternalAsync(apiKey.WorkflowId, parsedRunId, ct);
        if (existing is null)
            return Error(404, requestId, "run_not_found", "The workflow run was not found.");
        if (WorkflowGraphCompiler.IsTypedGraph(Parse(existing.GraphJson)) && existing.ApiKeyId != apiKey.Id)
            return Error(404, requestId, "run_not_found", "The workflow run was not found.");
        if (existing.Status is WorkflowRunStatus.Queued or WorkflowRunStatus.Running)
            await runs.CancelExternalAsync(apiKey.WorkflowId, parsedRunId, DateTimeOffset.UtcNow, ct);
        return ApiResults.NoContent();
    }

    /// <summary>验证外部工作流 Bearer Key 的前缀、摘要、期限与撤销状态。</summary>
    internal static async Task<WorkflowApiKey?> AuthenticateAsync(
        HttpContext http,
        IWorkflowExternalApiRepository externalApi,
        IWorkflowApiKeyService keyService,
        CancellationToken ct, bool touch = true)
    {
        var authorization = http.Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        var plaintext = authorization[7..].Trim();
        var prefix = keyService.ReadPrefix(plaintext);
        if (prefix is null) return null;
        var apiKey = await externalApi.FindApiKeyByPrefixAsync(prefix, ct);
        if (apiKey is null || !apiKey.Enabled || apiKey.RevokedAt is not null
            || apiKey.ExpiresAt <= DateTimeOffset.UtcNow
            || !keyService.Verify(plaintext, apiKey.KeyHash)) return null;
        if (touch) await externalApi.TouchApiKeyAsync(apiKey.Id, DateTimeOffset.UtcNow, ct);
        return apiKey;
    }

    /// <summary>从成功节点输出投影外部结果，并签发对应媒体读取地址。</summary>
    private static async Task<JsonObject> BuildOutputsAsync(
        PublishedWorkflowCatalogItem catalog,
        IReadOnlyList<WorkflowNodeRun> nodes,
        ICanvasAssetRepository assets,
        ISignedMediaUrlService signedUrls,
        CancellationToken ct)
    {
        // 只投影发布契约声明的节点输出，不能把运行的全部内部素材直接暴露给外部调用方。
        var result = new JsonObject();
        if (Parse(catalog.Deployment.OutputSchemaJson) is not JsonArray schema) return result;
        var byNode = nodes.ToDictionary(node => node.NodeId, StringComparer.Ordinal);
        foreach (var item in schema.OfType<JsonObject>())
        {
            var key = ReadString(item["key"]);
            var type = ReadString(item["type"]);
            var nodeId = ReadString(item["nodeId"]);
            if (key.Length == 0 || !byNode.TryGetValue(nodeId, out var node)
                || Parse(node.OutputJson) is not JsonObject output) continue;
            var urls = output[type == "video" ? "videos" : "images"] as JsonArray;
            var values = new JsonArray();
            if (urls is not null)
            {
                foreach (var url in urls.Select(ReadString))
                {
                    if (!TryAssetId(url, out var assetId)) continue;
                    var asset = await assets.FindAsync(catalog.OwnerDeviceId, assetId, ct);
                    if (asset is null || asset.Kind != type) continue;
                    var signedUrl = signedUrls.CreateSignedUrl(asset);
                    values.Add(new JsonObject
                    {
                        ["assetId"] = asset.AssetId.ToString(),
                        ["mediaType"] = asset.MediaType,
                        ["url"] = signedUrl,
                        ["expiresAt"] = SignedExpiry(signedUrl),
                    });
                }
            }
            result[key] = values;
        }
        return result;
    }

    /// <summary>将发布快照投影为公开工作流目录条目。</summary>
    private static ExternalWorkflowCatalogItemResponse ToCatalogResponse(
        PublishedWorkflowCatalogItem item)
    {
        if (WorkflowGraphCompiler.IsTypedGraph(Parse(item.Deployment.GraphJson)))
            return new(item.Deployment.WorkflowId.ToString(), item.WorkflowName, item.Deployment.Description,
                item.Deployment.Version, Parse(item.Deployment.InputSchemaJson), Parse(item.Deployment.OutputSchemaJson),
                item.Deployment.PublishedAt.ToString("O"));
        // 从发布图恢复实际约束，让旧版本目录与新版本遵守同样的参数规则。
        var contract = WorkflowExternalContractValidator.Validate(Parse(item.Deployment.GraphJson)).Contract;
        return new(
            item.Deployment.WorkflowId.ToString(),
            item.WorkflowName,
            item.Deployment.Description,
            item.Deployment.Version,
            PublicInputs(contract?.InputSchemaJson ?? item.Deployment.InputSchemaJson),
            PublicOutputs(contract?.OutputSchemaJson ?? item.Deployment.OutputSchemaJson),
            item.Deployment.PublishedAt.ToString("O"));
    }

    /// <summary>返回对外输入字段，隐藏内部图绑定信息。</summary>
    private static JsonArray PublicInputs(string schemaJson)
    {
        var result = new JsonArray();
        if (Parse(schemaJson) is not JsonArray inputs) return result;
        foreach (var input in inputs.OfType<JsonObject>())
        {
            var item = new JsonObject
            {
                ["key"] = input["key"]?.DeepClone(),
                ["label"] = input["label"]?.DeepClone(),
                ["description"] = input["description"]?.DeepClone(),
                ["type"] = input["type"]?.DeepClone(),
                ["required"] = input["required"]?.DeepClone(),
                ["constraints"] = input["constraints"]?.DeepClone(),
            };
            if (input.ContainsKey("default") && ReadString(input["type"]) != "image")
                item["default"] = input["default"]?.DeepClone();
            result.Add(item);
        }
        return result;
    }

    /// <summary>返回对外输出字段，隐藏内部图绑定信息。</summary>
    private static JsonArray PublicOutputs(string schemaJson)
    {
        var result = new JsonArray();
        if (Parse(schemaJson) is not JsonArray outputs) return result;
        foreach (var output in outputs.OfType<JsonObject>())
        {
            result.Add(new JsonObject
            {
                ["key"] = output["key"]?.DeepClone(),
                ["label"] = output["label"]?.DeepClone(),
                ["type"] = output["type"]?.DeepClone(),
            });
        }
        return result;
    }

    /// <summary>构造外部运行已受理的响应及关联发布版本。</summary>
    private static ExternalWorkflowRunCreatedResponse ToCreated(WorkflowRun run, int version) => new(
        run.RequestId ?? NewRequestId(),
        run.Id.ToString(),
        run.WorkflowId.ToString(),
        version,
        RunStatus(run.Status),
        $"/api/external/v1/runs/{run.Id}");

    /// <summary>对发布版本及规范化输入计算幂等比较摘要。</summary>
    private static byte[] HashRequest(WorkflowDeploymentId deploymentId, JsonObject? inputs, bool stream = false, JsonObject? agui = null)
    {
        var canonical = Canonicalize(inputs ?? []);
        // 默认模式保持旧摘要，已有幂等键在升级后仍可重放。
        return SHA256.HashData(Encoding.UTF8.GetBytes($"{deploymentId}\n{canonical}" + (stream ? "\nstream:true" : "")
            + (agui is null ? "" : "\nagui:" + Canonicalize(agui))));
    }

    /// <summary>只为 AG-UI 请求保存协议身份，普通外部请求继续使用原快照和摘要。</summary>
    private static string WithAgUi(string inputJson, JsonObject? agui)
    {
        if (agui is null) return inputJson;
        var snapshot = JsonNode.Parse(inputJson)!.AsObject();
        snapshot["agui"] = agui.DeepClone(); snapshot["stream"] = true;
        return snapshot.ToJsonString();
    }

    /// <summary>稳定排序 JSON 对象字段，保留数组顺序用于幂等比较。</summary>
    private static string Canonicalize(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject value => "{" + string.Join(",", value
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => JsonSerializer.Serialize(pair.Key) + ":" + Canonicalize(pair.Value))) + "}",
        JsonArray value => "[" + string.Join(",", value.Select(Canonicalize)) + "]",
        _ => node.ToJsonString(),
    };

    /// <summary>将节点状态投影为既有的运行进度贡献。</summary>
    private static double NodeProgress(WorkflowNodeRun node) => node.Status switch
    {
        WorkflowNodeRunStatus.Succeeded or WorkflowNodeRunStatus.Skipped => 1,
        WorkflowNodeRunStatus.Failed or WorkflowNodeRunStatus.Cancelled => node.Progress ?? 0,
        _ => node.Progress ?? 0,
    };

    /// <summary>将运行枚举转换为当前接口状态字符串。</summary>
    private static string RunStatus(WorkflowRunStatus status) => status switch
    {
        WorkflowRunStatus.Queued => "queued",
        WorkflowRunStatus.Running => "running",
        WorkflowRunStatus.Succeeded => "succeeded",
        WorkflowRunStatus.Failed => "failed",
        _ => "cancelled",
    };

    /// <summary>为外部请求创建带既有前缀的关联标识。</summary>
    private static string NewRequestId() => $"req_{Guid.CreateVersion7():N}";

    /// <summary>使用外部工作流错误协议返回 API Key 无效响应，并携带 requestId。</summary>
    private static IActionResult Unauthorized(string requestId) =>
        Error(401, requestId, "invalid_api_key", "The API key is missing, expired or invalid.");

    /// <summary>将校验失败映射为当前功能既有的错误结果，保留详情和错误分类。</summary>
    private static IActionResult Invalid(string requestId, string field, string code, string message) =>
        new BadRequestObjectResult(new ExternalApiErrorBody(
            requestId,
            new ExternalApiError("invalid_input", "External parameters are invalid.",
                [new ExternalApiErrorDetail(field, code, message)])));

    /// <summary>按当前功能的既有错误契约包装错误信息。</summary>
    private static IActionResult Error(int status, string requestId, string code, string message) =>
        new ObjectResult(new ExternalApiErrorBody(
            requestId, new ExternalApiError(code, message, []))) { StatusCode = status };

    /// <summary>返回原有限流错误并附加 Retry-After。</summary>
    private static IActionResult RateLimited(
        HttpContext http,
        string requestId,
        TimeSpan? retryAfter)
    {
        if (retryAfter is { } duration)
            http.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(duration.TotalSeconds)).ToString();
        return Error(429, requestId, "rate_limited", "The API key request limit was exceeded.");
    }

    /// <summary>按当前协议解析 JSON 响应并应用既有回退规则。</summary>
    private static JsonNode? Parse(string? json)
    {
        if (json is null) return null;
        try { return JsonNode.Parse(json); }
        catch (JsonException) { return null; }
    }

    /// <summary>从 JSON 或配置读取字符串字段，保留缺失及类型错误时的原有回退。</summary>
    private static string ReadString(JsonNode? value) =>
        value is JsonValue json && json.TryGetValue<string>(out var text) ? text : string.Empty;

    /// <summary>仅从既有站内素材路径解析素材标识。</summary>
    private static bool TryAssetId(string url, out Guid assetId)
    {
        const string prefix = "/api/v1/canvas/assets/";
        assetId = default;
        return url.StartsWith(prefix, StringComparison.Ordinal)
               && Guid.TryParse(url[prefix.Length..].Split('?')[0], out assetId);
    }

    /// <summary>从已签名媒体地址读取失效时间。</summary>
    private static string? SignedExpiry(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        var pair = uri.Query.TrimStart('?').Split('&')
            .Select(part => part.Split('=', 2))
            .FirstOrDefault(parts => parts.Length == 2 && parts[0] == "exp");
        return pair is { Length: 2 } && long.TryParse(pair[1], out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds).ToString("O")
            : null;
    }

    /// <summary>将媒体类型映射为当前支持的文件扩展名，并保留未知类型的默认值。</summary>
    private static string ExtensionFor(string mediaType) => mediaType switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/webp" => ".webp",
        _ => ".bin",
    };
}
