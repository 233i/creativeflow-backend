using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using AGUI.Abstractions;
using AGUI.Formatting;
using Microsoft.AspNetCore.Http.Features;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Abstractions.Security;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Infrastructure.Options;

namespace CreativeFlow.Api.Http.Handlers;

/// <summary>只读日志的 AG-UI 订阅器：官方编码、有界写入、精确续读及每批数据读取后的资源复核。</summary>
internal static class AgUiWorkflowStreamHandlers
{
    /// <summary>验证运行及游标，在调用方已持有订阅额度时写出日志投影；断开不修改任务。</summary>
    internal static async Task<IActionResult> StreamAsync(string runId, HttpContext http,
        IWorkflowExternalApiRepository externalApi, IWorkflowRunRepository runs, IWorkflowRunJournal journal,
        IWorkflowApiKeyService keyService, ExternalWorkflowRateLimiter rateLimiter, ICanvasAssetRepository assets,
        ISignedMediaUrlService signedUrls, ExternalWorkflowApiOptions options, ILogger logger, CancellationToken ct)
    {
        var initial = await Check();
        if (initial is not ObjectResult { Value: ExternalWorkflowRunResponse state }) return initial;
        var key = await ExternalWorkflowHandlers.AuthenticateAsync(http, externalApi, keyService, ct, touch: false);
        var id = new WorkflowRunId(Guid.Parse(runId));
        var run = key is null ? null : await runs.FindExternalAsync(key.WorkflowId, id, ct);
        if (run is null || run.ApiKeyId != key!.Id || JsonNode.Parse(run.InputJson ?? "{}")?["agui"] is not JsonObject identity)
            return new NotFoundResult();
        var projection = new AgUiWorkflowProjection(runId, identity, run.GraphJson);
        var cursor = http.Request.Headers["Last-Event-ID"].ToString();
        long sequence = 0; int part = 0;
        if (cursor.Length > 0)
        {
            var pieces = cursor.Split(':');
            if (pieces.Length != 3 || pieces[0] != runId
                || !long.TryParse(pieces[1], NumberStyles.None, CultureInfo.InvariantCulture, out sequence) || sequence <= 0
                || !int.TryParse(pieces[2], NumberStyles.None, CultureInfo.InvariantCulture, out part) || part <= 0)
                return InvalidCursor();
        }
        long after = 0;
        var pending = new List<(string Id, BaseEvent Event)>();
        // 重建协议状态，但只发送游标之后的子帧，消息开始／结束和快速步骤不会重复或遗漏。
        while (after < sequence)
        {
            var history = await journal.ReadAsync(id, after, ct);
            if (history.Count == 0) return InvalidCursor();
            foreach (var item in history)
            {
                var frames = projection.Project(item, state);
                after = item.Sequence;
                if (after != sequence) continue;
                if (part > frames.Count) return InvalidCursor();
                AddFrames(item, frames, part); break;
            }
        }
        http.Response.ContentType = SseEventStreamFormatter.ServerSentEventsMediaType + "; charset=utf-8";
        http.Response.Headers.CacheControl = "private, no-store, no-transform";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        http.Response.Headers["X-Workflow-Run-ID"] = runId;
        http.Response.Headers["X-Workflow-Events-URL"] = $"/api/agui/v1/runs/{runId}/events";
        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        var heartbeat = DateTimeOffset.UtcNow;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                IReadOnlyList<WorkflowRunJournalEntry> batch = projection.Finished ? [] : await journal.ReadAsync(id, after, ct);
                var verified = await Check(); // 读取正文之后检查授权，缓存批次不能绕过撤权。
                if (verified is not ObjectResult { Value: ExternalWorkflowRunResponse current })
                { await Write([("", AgUiWorkflowProjection.Error("stream_access_lost", "运行已不可访问，请清除暂存文本并停止重连。", false))]); return new EmptyResult(); }
                foreach (var item in batch)
                { AddFrames(item, projection.Project(item, current), 0); after = item.Sequence; }
                if (pending.Count > 0) { await Write(pending); pending.Clear(); }
                if (projection.Finished) return new EmptyResult();
                if (DateTimeOffset.UtcNow - heartbeat >= TimeSpan.FromSeconds(15))
                { await WriteComment(); heartbeat = DateTimeOffset.UtcNow; }
                await Task.Delay(250, ct);
            }
        }
        catch (OperationCanceledException) { /* 断开或慢连接只结束订阅，Worker 继续运行。 */ }
        catch (IOException) { /* 事件仍在日志中，客户端可按最后的 id 续读。 */ }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "AG-UI subscription interrupted for {RunId}", runId);
            try { await Write([("", AgUiWorkflowProjection.Error("stream_unavailable", "订阅暂时中断，请使用最后的事件 ID 重连。", true))]); }
            catch (Exception error) when (error is IOException or OperationCanceledException) { }
        }
        return new EmptyResult();

        /// <summary>复用原发布版本、Key 及知识／凭据授权的动态校验和输出签名。</summary>
        Task<IActionResult> Check() => ExternalWorkflowHandlers.GetRunAsync(runId, http, externalApi, runs, keyService,
            rateLimiter, assets, signedUrls, options, ct, streamRead: true);
        /// <summary>在开启响应之前拒绝跨运行、超前及不存在的子帧游标。</summary>
        IActionResult InvalidCursor() => new BadRequestObjectResult(new ExternalApiErrorBody($"req_{Guid.CreateVersion7():N}",
            new ExternalApiError("invalid_cursor", "Last-Event-ID 必须是本运行已交付的完整事件 ID。", [])));
        /// <summary>用日志序号和稳定子帧号构造精确去重游标。</summary>
        void AddFrames(WorkflowRunJournalEntry entry, IReadOnlyList<BaseEvent> frames, int skip)
        { for (var index = skip; index < frames.Count; index++) pending.Add(($"{runId}:{entry.Sequence}:{index + 1}", frames[index])); }
        /// <summary>整批共享十五秒写入期限，慢订阅者不能逐帧延长权限缓存窗口。</summary>
        async Task Write(IReadOnlyList<(string Id, BaseEvent Event)> frames)
        {
            using var buffer = new MemoryStream();
            foreach (var frame in frames)
            {
                if (frame.Id.Length > 0) await buffer.WriteAsync(Encoding.UTF8.GetBytes("id: " + frame.Id + "\n"), ct);
                await new SseEventStreamFormatter().WriteAsync(One(frame.Event), buffer, ct);
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            await http.Response.Body.WriteAsync(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), deadline.Token);
            await http.Response.Body.FlushAsync(deadline.Token);
        }
        /// <summary>通过无游标 SSE 注释保活，不污染 AG-UI 业务事件和续读位置。</summary>
        async Task WriteComment()
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(15));
            await http.Response.WriteAsync(": keep-alive\n\n", deadline.Token); await http.Response.Body.FlushAsync(deadline.Token);
        }
    }

    /// <summary>为官方 formatter 提供单事件流，保留其事件类型和 JSON 编码规则。</summary>
    private static async IAsyncEnumerable<BaseEvent> One(BaseEvent value) { yield return value; await Task.CompletedTask; }
}
