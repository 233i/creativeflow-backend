using System.Text.Json.Nodes;
using CreativeFlow.Api.Middleware;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Contracts.Agent;
using CreativeFlow.Application.Features.Agent;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Api.Http.Handlers;

internal static class AgentHandlers
{
    /// <summary>提示词允许的最多字符数。</summary>
    private const int MaxPromptLength = 100_000;
    /// <summary>会话标题允许的最多字符数。</summary>
    private const int MaxTitleLength = 200;
    /// <summary>单次 Agent 请求允许的参考图片数量上限。</summary>
    private const int MaxAgentImages = 8;

    /// <summary>创建与当前画布或身份关联的会话。</summary>
    internal static async Task<IActionResult> CreateSessionAsync(
        CreateAgentSessionRequest request,
        HttpContext http,
        ICanvasRepository canvases,
        IAgentRepository agents,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId) return ApiResults.Unauthorized();
        if (!CanvasId.TryParse(request.CanvasId, out var canvasId)) return Invalid("malformed canvas id");
        if (await canvases.FindAsync(deviceId, canvasId, ct) is null) return ApiResults.NotFound();

        if (await agents.FindSessionByCanvasAsync(deviceId, canvasId, ct) is { } existing)
        {
            return ApiResults.Ok(ToResponse(existing));
        }

        var now = DateTimeOffset.UtcNow;
        var title = string.IsNullOrWhiteSpace(request.Title)
            ? null
            : request.Title.Trim()[..Math.Min(request.Title.Trim().Length, MaxTitleLength)];
        var session = new AgentSession(
            AgentSessionId.New(), deviceId, canvasId, title, now, now);
        await agents.InsertSessionAsync(session, ct);
        return ApiResults.Ok(ToResponse(session));
    }

    /// <summary>查找当前画布对应的 Agent 会话。</summary>
    internal static async Task<IActionResult> GetSessionByCanvasAsync(
        string canvasId,
        HttpContext http,
        IAgentRepository agents,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId) return ApiResults.Unauthorized();
        if (!CanvasId.TryParse(canvasId, out var parsed)) return Invalid("malformed canvas id");
        var session = await agents.FindSessionByCanvasAsync(deviceId, parsed, ct);
        return session is null ? ApiResults.NotFound() : ApiResults.Ok(ToResponse(session));
    }

    /// <summary>按会话顺序读取已保存的 Agent 消息。</summary>
    internal static async Task<IActionResult> ListMessagesAsync(
        string id,
        HttpContext http,
        IAgentRepository agents,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId) return ApiResults.Unauthorized();
        if (!AgentSessionId.TryParse(id, out var sessionId)) return Invalid("malformed session id");
        if (await agents.FindSessionAsync(deviceId, sessionId, ct) is null) return ApiResults.NotFound();

        var messages = await agents.ListMessagesAsync(deviceId, sessionId, ct);
        return ApiResults.Ok(new AgentMessageListResponse(
        [
            .. messages.Select(message => new AgentMessageResponse(
                message.MessageId.ToString(), message.RunId?.ToString(), message.Ordinal,
                message.Role, message.ContentJson, message.CreatedAt.ToString("O"))),
        ]));
    }

    /// <summary>清理指定 Agent 会话的历史消息。</summary>
    internal static async Task<IActionResult> ClearMessagesAsync(
        string id,
        HttpContext http,
        IAgentRepository agents,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId) return ApiResults.Unauthorized();
        if (!AgentSessionId.TryParse(id, out var sessionId)) return Invalid("malformed session id");

        var session = await agents.FindSessionAsync(deviceId, sessionId, ct);
        if (session is null) return ApiResults.NotFound();
        if (await agents.FindActiveRunAsync(deviceId, session.CanvasId, ct) is not null)
        {
            return Invalid("stop the active Agent run before clearing its messages");
        }

        await agents.ClearMessagesAsync(deviceId, sessionId, ct);
        return ApiResults.NoContent();
    }

    /// <summary>校验输入并创建持久化运行，随后由既有后台执行机制推进。</summary>
    internal static async Task<IActionResult> CreateRunAsync(
        string id,
        CreateAgentRunRequest request,
        HttpContext http,
        IAgentRepository agents,
        IProviderRepository providers,
        IAgentRunControl control,
        ICanvasAssetRepository assets,
        CancellationToken ct)
    {
        // 先完成设备、会话和输入检查，再写入运行并唤醒宿主，避免无效请求启动模型调用。
        if (http.GetDeviceId() is not { } deviceId) return ApiResults.Unauthorized();
        if (!AgentSessionId.TryParse(id, out var sessionId)) return Invalid("malformed session id");
        var session = await agents.FindSessionAsync(deviceId, sessionId, ct);
        if (session is null) return ApiResults.NotFound();
        if (!ProviderId.TryParse(request.ProviderId, out var providerId)) return Invalid("malformed provider id");
        if (string.IsNullOrWhiteSpace(request.Prompt)) return Invalid("a prompt is required");
        if (request.Prompt.Length > MaxPromptLength) return Invalid("the prompt is too long");
        if (request.ExecutorConnectionId is { Length: > 200 }) return Invalid("the connection id is too long");

        if (await agents.FindActiveRunAsync(deviceId, session.CanvasId, ct) is not null)
        {
            return Invalid("this canvas already has an active Agent run");
        }

        var provider = await providers.FindAsync(providerId, ct);
        var model = provider?.Models.FirstOrDefault(candidate =>
            candidate.ModelKey == request.ModelKey && candidate.Category == ModelCategory.Chat);
        if (provider is null) return ApiResults.NotFound();
        if (!provider.Enabled || model is null || !model.Enabled)
        {
            return Invalid("the selected Agent model is unavailable or disabled");
        }
        var protocol = provider.ResolveProtocol(model);
        if (protocol is not (ProviderType.Anthropic
            or ProviderType.OpenAiChat
            or ProviderType.OpenAiResponses))
        {
            return Invalid($"protocol '{protocol.ToWire()}' does not support Agent runs yet");
        }

        var imageUrls = request.Images ?? [];
        if (imageUrls.Length > MaxAgentImages)
        {
            return Invalid($"at most {MaxAgentImages} images are allowed per Agent run");
        }

        if (imageUrls.Length > 0 && !model.SupportsVision)
        {
            return Invalid("the selected Agent model does not support image input");
        }

        var imageAssets = new List<CanvasAsset>(imageUrls.Length);
        foreach (var imageUrl in imageUrls)
        {
            if (!TryParseAssetUrl(imageUrl, out var assetId))
            {
                return Invalid("Agent images must be uploaded canvas assets");
            }

            var asset = await assets.FindAsync(deviceId, assetId, ct);
            if (asset is null || asset.IsRecycled || asset.Kind != "image")
            {
                return Invalid("one of the Agent images is no longer available");
            }

            imageAssets.Add(asset);
        }

        var now = DateTimeOffset.UtcNow;
        var run = new AgentRun(
            AgentRunId.New(), session.Id, deviceId, session.CanvasId, provider.Id,
            request.ModelKey, AgentRunStatus.Queued, ExecutorConnectionId: null, 0, 0,
            null, null, null, null, null, now, now);
        await agents.InsertRunAsync(run, ct);

        var ordinal = await agents.NextMessageOrdinalAsync(deviceId, session.Id, ct);
        var content = new JsonArray
        {
            (JsonNode)new JsonObject { ["kind"] = "text", ["text"] = request.Prompt },
        };
        for (var index = 0; index < imageAssets.Count; index++)
        {
            content.Add((JsonNode)new JsonObject
            {
                ["kind"] = "image",
                ["imageUrl"] = imageUrls[index],
                ["mediaType"] = imageAssets[index].MediaType,
            });
        }
        await agents.InsertMessageAsync(
            new AgentStoredMessage(
                Guid.CreateVersion7(), session.Id, run.Id, deviceId, ordinal, "user",
                content.ToJsonString(), now),
            ct);

        control.Enqueue(run.Id);
        return ApiResults.Ok(ToResponse(run));
    }

    /// <summary>读取当前仍在执行或等待的 Agent 运行。</summary>
    internal static async Task<IActionResult> GetActiveRunAsync(
        string canvasId,
        HttpContext http,
        IAgentRepository agents,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId) return ApiResults.Unauthorized();
        if (!CanvasId.TryParse(canvasId, out var parsed)) return Invalid("malformed canvas id");
        var run = await agents.FindActiveRunAsync(deviceId, parsed, ct);
        return run is null ? ApiResults.NotFound() : ApiResults.Ok(ToResponse(run));
    }

    /// <summary>读取指定运行及其当前持久化状态。</summary>
    internal static async Task<IActionResult> GetRunAsync(
        string id,
        HttpContext http,
        IAgentRepository agents,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId) return ApiResults.Unauthorized();
        if (!AgentRunId.TryParse(id, out var runId)) return Invalid("malformed run id");
        var run = await agents.FindRunAsync(deviceId, runId, ct);
        return run is null ? ApiResults.NotFound() : ApiResults.Ok(ToResponse(run));
    }

    /// <summary>按持久序号增量读取 Agent 运行事件。</summary>
    internal static async Task<IActionResult> ListEventsAsync(
        string id,
        HttpContext http,
        IAgentRepository agents,
        CancellationToken ct,
        long? afterSeq = null)
    {
        if (http.GetDeviceId() is not { } deviceId) return ApiResults.Unauthorized();
        if (!AgentRunId.TryParse(id, out var runId)) return Invalid("malformed run id");
        if (await agents.FindRunAsync(deviceId, runId, ct) is null) return ApiResults.NotFound();
        var events = await agents.ListEventsAsync(deviceId, runId, afterSeq ?? 0, ct);
        return ApiResults.Ok(new AgentEventListResponse(
        [
            .. events.Select(item => new AgentEventResponse(
                item.RunId.ToString(), item.Sequence, item.Type, item.PayloadJson,
                item.CreatedAt.ToString("O"))),
        ]));
    }

    /// <summary>请求取消指定运行，沿用当前终止状态规则。</summary>
    internal static async Task<IActionResult> CancelRunAsync(
        string id,
        HttpContext http,
        IAgentRepository agents,
        IAgentRunControl control,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId) return ApiResults.Unauthorized();
        if (!AgentRunId.TryParse(id, out var runId)) return Invalid("malformed run id");
        var run = await agents.FindRunAsync(deviceId, runId, ct);
        if (run is null) return ApiResults.NotFound();
        if (run.Status is AgentRunStatus.Succeeded or AgentRunStatus.Failed
            or AgentRunStatus.Cancelled or AgentRunStatus.Interrupted)
        {
            return ApiResults.NoContent();
        }

        control.Cancel(runId);
        var now = DateTimeOffset.UtcNow;
        await agents.UpdateRunAsync(run with
        {
            Status = AgentRunStatus.Cancelled,
            Error = null,
            FinishedAt = now,
            UpdatedAt = now,
        }, ct);
        return ApiResults.NoContent();
    }

    /// <summary>读取当前可用工具目录。</summary>
    internal static IActionResult ListTools(
        HttpContext http)
    {
        if (http.GetDeviceId() is null) return ApiResults.Unauthorized();
        return ApiResults.Ok(new AgentToolCatalogResponse(
        [
            .. AgentToolCatalog.All.Select(tool => new AgentToolCatalogEntryResponse(
                tool.Name,
                tool.Site == AgentToolSite.Canvas ? "canvas" : "server",
                tool.Approval switch
                {
                    AgentApprovalPolicy.Never => "never",
                    AgentApprovalPolicy.Always => "always",
                    AgentApprovalPolicy.DestructiveAction => "destructive_action",
                    _ => throw new ArgumentOutOfRangeException(),
                },
                tool.Description,
                tool.InputSchemaJson,
                tool.TimeoutMilliseconds)),
        ]));
    }

    /// <summary>将业务记录映射为既有 HTTP 响应字段。</summary>
    private static AgentSessionResponse ToResponse(AgentSession session) => new(
        session.Id.ToString(), session.CanvasId.ToString(), session.Title,
        session.CreatedAt.ToString("O"), session.UpdatedAt.ToString("O"));

    /// <summary>将业务记录映射为既有 HTTP 响应字段。</summary>
    private static AgentRunResponse ToResponse(AgentRun run) => new(
        run.Id.ToString(), run.SessionId.ToString(), run.CanvasId.ToString(),
        run.ProviderId?.ToString(), run.ModelKey, AgentRepositoryStatus(run.Status), run.Iteration,
        run.LastSequence, run.Error, run.CreatedAt.ToString("O"), run.UpdatedAt.ToString("O"));

    /// <summary>仅解析站内素材 URL 的标识，不获取任意外部地址。</summary>
    private static bool TryParseAssetUrl(string? url, out Guid assetId)
    {
        const string prefix = "/api/v1/canvas/assets/";
        assetId = default;
        if (url is null || !url.StartsWith(prefix, StringComparison.Ordinal)) return false;

        var idSegment = url[prefix.Length..].Split('?')[0];
        return Guid.TryParse(idSegment, out assetId) && assetId != Guid.Empty;
    }

    /// <summary>将 Agent 运行状态映射为既有持久化字符串。</summary>
    private static string AgentRepositoryStatus(AgentRunStatus status) => status switch
    {
        AgentRunStatus.Queued => "queued",
        AgentRunStatus.Running => "running",
        AgentRunStatus.AwaitingCanvas => "awaiting_canvas",
        AgentRunStatus.AwaitingApproval => "awaiting_approval",
        AgentRunStatus.Succeeded => "succeeded",
        AgentRunStatus.Failed => "failed",
        AgentRunStatus.Cancelled => "cancelled",
        AgentRunStatus.Interrupted => "interrupted",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    /// <summary>将校验失败映射为当前功能既有的错误结果，保留详情和错误分类。</summary>
    private static IActionResult Invalid(string detail) =>
        ApiResults.BadRequest(new ErrorResponse("invalid_request", [detail]));
}
