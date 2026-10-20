using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.Features;
using CreativeFlow.Api.Serialization;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Abstractions.Security;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Infrastructure.Options;

namespace CreativeFlow.Api.Http.Handlers;

internal static partial class ExternalWorkflowHandlers
{
    /// <summary>订阅已有运行；只读持久化状态和公开增量，客户端断开不会取消或重新执行任务。</summary>
    internal static async Task<IActionResult> StreamRunAsync(string runId, HttpContext http,
        IWorkflowExternalApiRepository externalApi, IWorkflowRunRepository runs, IWorkflowRunEventRepository events,
        IWorkflowApiKeyService keyService, ExternalWorkflowRateLimiter rateLimiter, ICanvasAssetRepository assets,
        ISignedMediaUrlService signedUrls, ExternalWorkflowApiOptions options, CancellationToken ct)
    {
        var requestId = NewRequestId();
        var initial = await GetRunAsync(runId, http, externalApi, runs, keyService, rateLimiter, assets, signedUrls, options, ct);
        if (initial is not ObjectResult { Value: ExternalWorkflowRunResponse }) return initial;
        var key = await AuthenticateAsync(http, externalApi, keyService, ct, touch: false);
        if (key is null) return Unauthorized(requestId);
        var id = new WorkflowRunId(Guid.Parse(runId));
        var run = await runs.FindExternalAsync(key.WorkflowId, id, ct);
        if (run is null || run.ApiKeyId != key.Id) return Error(404, requestId, "run_not_found", "The workflow run was not found.");
        var cursor = http.Request.Headers["Last-Event-ID"].ToString();
        var prefix = run.Id + ":";
        long after = 0;
        if (cursor.Length > 0 && (!cursor.StartsWith(prefix, StringComparison.Ordinal)
            || !long.TryParse(cursor.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out after) || after < 0))
            return Invalid(requestId, "Last-Event-ID", "invalid_cursor", "Use the last text event id returned for this run.");
        if (after > await events.LastSequenceAsync(id, ct))
            return Invalid(requestId, "Last-Event-ID", "invalid_cursor", "The event cursor is ahead of this run.");
        using var lease = rateLimiter.AcquireStream(key.Id);
        if (lease is null) return RateLimited(http, requestId, TimeSpan.FromSeconds(5));
        http.Response.ContentType = "text/event-stream; charset=utf-8";
        http.Response.Headers.CacheControl = "private, no-store, no-transform";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        var lastProgress = "";
        var heartbeat = DateTimeOffset.UtcNow;
        try
        {
            await WriteAsync("retry: 2000\n\n");
            while (!ct.IsCancellationRequested)
            {
                var batch = await events.ReadAsync(id, after, ct);
                // 在每批事件读取后重新认证并复核资源，撤权后不继续发送缓存中的正文。
                var checkedResult = await GetRunAsync(runId, http, externalApi, runs, keyService, rateLimiter, assets, signedUrls, options, ct, streamRead: true);
                if (checkedResult is not ObjectResult { Value: ExternalWorkflowRunResponse state })
                {
                    await WriteJsonEventAsync("stream.error", new JsonObject { ["requestId"] = requestId, ["code"] = "stream_access_lost", ["message"] = "运行已不可访问，请停止订阅并清除暂存文本。", ["retryable"] = false }.ToJsonString());
                    return new EmptyResult();
                }
                var progress = JsonSerializer.Serialize(state with { Outputs = null }, ApiJsonContext.Default.ExternalWorkflowRunResponse);
                if (progress != lastProgress)
                {
                    await EventAsync("run.progress", state with { Outputs = null });
                    lastProgress = progress;
                }
                if (batch.Count > 0)
                {
                    // 整批共用一次写入期限，慢连接不能逐帧延长授权窗口。
                    var frames = string.Concat(batch.Select(item => $"id: {prefix}{item.Sequence}\nevent: output.delta\ndata: {item.Data}\n\n"));
                    await WriteAsync(frames);
                    after = batch[^1].Sequence;
                }
                if (state.Status is "succeeded" or "failed" or "cancelled")
                {
                    // 终态与增量追加使用同一运行锁；补齐最后一批后才能交付权威结果。
                    if (after < await events.LastSequenceAsync(id, ct)) continue;
                    await EventAsync(state.Status == "succeeded" ? "run.completed" : "run." + state.Status, state);
                    return new EmptyResult();
                }
                if (DateTimeOffset.UtcNow - heartbeat >= TimeSpan.FromSeconds(15))
                { await WriteAsync(": keep-alive\n\n"); heartbeat = DateTimeOffset.UtcNow; }
                await Task.Delay(500, ct);
            }
        }
        catch (OperationCanceledException) { /* 只结束订阅；持久化 Worker 使用独立的取消标记。 */ }
        catch (IOException) { /* 断网后调用方用最后的事件 ID 续读。 */ }
        catch (Exception exception)
        {
            http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("WorkflowStream")
                .LogWarning(exception, "Workflow event subscription interrupted for {RunId}", runId);
            try { await WriteJsonEventAsync("stream.error", new JsonObject { ["code"] = "stream_unavailable", ["message"] = "订阅暂时中断，可使用最后的事件 ID 重连。", ["retryable"] = true }.ToJsonString()); }
            catch (Exception error) when (error is IOException or OperationCanceledException) { }
        }
        return new EmptyResult();

        Task EventAsync(string name, ExternalWorkflowRunResponse value) => WriteJsonEventAsync(name, JsonSerializer.Serialize(value, ApiJsonContext.Default.ExternalWorkflowRunResponse));
        Task WriteJsonEventAsync(string name, string data) => WriteAsync($"event: {name}\ndata: {data}\n\n");

        async Task WriteAsync(string frame)
        {
            // 慢客户端不能永久占用连接；超时只断开订阅，事件仍保留在数据库。
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            await http.Response.WriteAsync(frame, deadline.Token);
            await http.Response.Body.FlushAsync(deadline.Token);
        }
    }
}
