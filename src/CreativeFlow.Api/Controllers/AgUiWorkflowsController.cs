using System.Text.Json;
using System.Text.Json.Nodes;
using AGUI.Abstractions;
using Microsoft.AspNetCore.Mvc;
using CreativeFlow.Api.Http.Handlers;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Abstractions.Security;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Infrastructure.Options;

namespace CreativeFlow.Api.Controllers;

/// <summary>使用标准 AG-UI 输入及事件运行已发布的媒体／通用工作流，复用同一后台队列。</summary>
[ApiController]
[Route("api/agui/v1")]
[Tags("ag-ui-workflows")]
public sealed class AgUiWorkflowsController(
    IWorkflowExternalApiRepository externalApi,
    IWorkflowRunRepository runs,
    IWorkflowRunJournal journal,
    IWorkflowApiKeyService keyService,
    ICanvasAssetRepository assets,
    ISignedMediaUrlService signedUrls,
    ExternalWorkflowRateLimiter rateLimiter,
    ExternalWorkflowApiOptions options,
    ILogger<AgUiWorkflowsController> logger) : ControllerBase
{
    /// <summary>提交 RunAgentInput 并订阅；inputs 放在 forwardedProps.inputs，默认幂等键由 runId 派生。</summary>
    [HttpPost("workflows/{id}/runs", Name = "RunPublishedWorkflowWithAgUi")]
    [IgnoreAntiforgeryToken]
    [Consumes("application/json")]
    [Produces("text/event-stream", "application/json")]
    public async Task<IActionResult> Run([FromRoute] string id, [FromBody] JsonElement payload, CancellationToken ct)
    {
        RunAgentInput? input;
        try { input = payload.Deserialize(AGUIJsonSerializerContext.Default.RunAgentInput); }
        catch (JsonException) { return Invalid("请求必须符合 AG-UI RunAgentInput 格式。"); }
        if (input is null || !ValidId(input.RunId) || !ValidId(input.ThreadId)
            || input.ParentRunId is not null && !ValidId(input.ParentRunId))
            return Invalid("threadId、runId 必须为 1–128 个可见字符，且不能包含空白或控制字符。");
        if (input.Resume is { Count: > 0 } || input.Tools is { Count: > 0 } || input.Context is { Count: > 0 }
            || input.State is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } state
                && !(state.ValueKind == JsonValueKind.Object && !state.EnumerateObject().Any()))
            return Invalid("当前接口执行固定工作流，不接受客户端工具、context、可变 state 或 resume；断线请使用 Last-Event-ID 续读。");
        JsonObject? inputs;
        try { inputs = ReadInputs(payload); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
        { return Invalid("用户消息必须包含合法文本；图片请通过 inputs 中的 assetId 字段传入。"); }
        if (inputs is null) return Invalid("请提供 forwardedProps.inputs 对象，或一条可映射为 question 的用户文本消息。");
        if (!options.Enabled) return StatusCode(404);
        var key = await ExternalWorkflowHandlers.AuthenticateAsync(HttpContext, externalApi, keyService, ct, touch: false);
        if (key is null) return Unauthorized();
        // 先获得订阅额度再入队；超额连接不会创建付费后台任务。
        using var lease = rateLimiter.AcquireStream(key.Id);
        if (lease is null) return Limited();
        if (!Request.Headers.ContainsKey("Idempotency-Key")) Request.Headers["Idempotency-Key"] = "agui:" + input.RunId;
        if (Request.Headers.ContainsKey("Last-Event-ID")
            && await runs.FindByIdempotencyAsync(key.Id, Request.Headers["Idempotency-Key"].ToString().Trim(), ct) is null)
            return Invalid("不能用续读游标创建新任务；请 GET 原 eventsUrl，或重试原 runId 和幂等键。");
        var identity = new JsonObject { ["threadId"] = input.ThreadId, ["runId"] = input.RunId, ["parentRunId"] = input.ParentRunId };
        var result = await ExternalWorkflowHandlers.CreateRunAsync(id,
            new CreateExternalWorkflowRunRequest(inputs) { Stream = true }, HttpContext,
            externalApi, runs, keyService, rateLimiter, assets, options, ct, identity);
        if (result is not ObjectResult { StatusCode: 202, Value: ExternalWorkflowRunCreatedResponse created }) return result;
        return await Stream(created.RunId, ct);
    }

    /// <summary>续读既有 AG-UI 运行，不创建新运行、不重新调用模型；游标来自 SSE id。</summary>
    [HttpGet("runs/{runId}/events", Name = "ReadWorkflowAgUiEvents")]
    [Produces("text/event-stream", "application/json")]
    public async Task<IActionResult> Events([FromRoute] string runId, CancellationToken ct)
    {
        if (!options.Enabled) return StatusCode(404);
        var key = await ExternalWorkflowHandlers.AuthenticateAsync(HttpContext, externalApi, keyService, ct, touch: false);
        if (key is null) return Unauthorized();
        if (!rateLimiter.TryAcquire(key.Id, ExternalWorkflowRateLimitKind.ReadStatus, out var retry))
        { Response.Headers.RetryAfter = Math.Max(1, (int)(retry?.TotalSeconds ?? 5)).ToString(); return StatusCode(429); }
        using var lease = rateLimiter.AcquireStream(key.Id);
        return lease is null ? Limited() : await Stream(runId, ct);
    }

    /// <summary>将已经受理的内部运行交给只读协议订阅器，不创建第二个执行循环。</summary>
    private Task<IActionResult> Stream(string runId, CancellationToken ct) => AgUiWorkflowStreamHandlers.StreamAsync(
        runId, HttpContext, externalApi, runs, journal, keyService, rateLimiter, assets, signedUrls, options, logger, ct);

    /// <summary>订阅额度耗尽时给出重试间隔，在受理之前拒绝请求。</summary>
    private IActionResult Limited() { Response.Headers.RetryAfter = "5"; return StatusCode(429); }
    /// <summary>协议输入错误仍使用现有外部错误契约，不能以 HTTP 成功隐藏受理失败。</summary>
    private IActionResult Invalid(string message) => BadRequest(new ExternalApiErrorBody($"req_{Guid.CreateVersion7():N}",
        new ExternalApiError("invalid_agui_request", message, [])));
    /// <summary>限制关联标识长度并排除控制字符，避免幂等键和事件身份歧义。</summary>
    private static bool ValidId(string? value) => value is { Length: > 0 and <= 128 } && !value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));

    /// <summary>优先使用显式工作流参数；仅允许最后一条纯文本用户消息回退为 question。</summary>
    private static JsonObject? ReadInputs(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return null;
        if (payload.TryGetProperty("forwardedProps", out var forwarded))
            return forwarded.ValueKind == JsonValueKind.Object && forwarded.TryGetProperty("inputs", out var inputs)
                && inputs.ValueKind == JsonValueKind.Object ? JsonNode.Parse(inputs.GetRawText())!.AsObject() : null;
        if (!payload.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array) return null;
        foreach (var message in messages.EnumerateArray().Reverse())
        {
            if (!message.TryGetProperty("role", out var role) || role.ValueKind != JsonValueKind.String || role.GetString() != "user"
                || !message.TryGetProperty("content", out var content)) continue;
            if (content.ValueKind == JsonValueKind.String) return new JsonObject { ["question"] = content.GetString() };
            if (content.ValueKind != JsonValueKind.Array) return null;
            var parts = content.EnumerateArray().ToArray();
            if (parts.Any(part => !part.TryGetProperty("type", out var type) || type.GetString() != "text")) return null;
            return new JsonObject { ["question"] = string.Concat(parts.Select(part => part.GetProperty("text").GetString())) };
        }
        return null;
    }
}
