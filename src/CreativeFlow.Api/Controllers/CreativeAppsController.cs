using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using CreativeFlow.Api.Http;
using CreativeFlow.Api.Middleware;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Features.CreativeApps;

namespace CreativeFlow.Api.Controllers;

/// <summary>登录客户使用公共媒体应用的入口；从不返回发布者的内部工作流图。</summary>
[ApiController]
[Route("api/v1/creative-apps")]
[Tags("creative-apps")]
public sealed class CreativeAppsController(ICreativeAppRepository apps, CreativeAppService service, IAssetStore files) : ControllerBase
{
    /// <summary>列出登录客户可使用的公共创作应用。</summary>
    [HttpGet(Name = "ListCreativeApps")]
    public Task<IActionResult> List([FromQuery] string? search, [FromQuery] string? kind, [FromQuery] int offset = 0, CancellationToken ct = default) => Execute(async () =>
        Ok(new JsonObject { ["items"] = await apps.ListAsync(search?.Trim()[..Math.Min(search.Trim().Length, 100)], kind is "image" or "video" ? kind : null, Math.Clamp(offset, 0, 100000), ct) }));

    /// <summary>返回当前上架版本的客户表单，不包含内部图。</summary>
    [HttpGet("{id:guid}", Name = "GetCreativeApp")]
    public Task<IActionResult> Get(Guid id, CancellationToken ct) => Execute(async () =>
        await apps.FindAsync(id, null, ct) is { } release ? Ok(CreativeAppContract.Describe(release)) : NotFound());

    /// <summary>按当前上架关系读取封面，不开放原始素材账户。</summary>
    [HttpGet("{id:guid}/cover", Name = "GetCreativeAppCover")]
    public Task<IActionResult> Cover(Guid id, CancellationToken ct) => Execute(async () =>
    {
        var asset = await apps.FindCoverAsync(id, ct); if (asset is null) return NotFound();
        var stream = await files.OpenReadAsync(asset.StoragePath, ct); if (stream is null) return NotFound();
        Response.Headers.CacheControl = "private, max-age=60";
        return File(stream, asset.MediaType);
    });

    /// <summary>受理固定版本的生成请求，立即返回可查询任务。</summary>
    [HttpPost("{id:guid}/runs", Name = "StartCreativeAppRun")]
    [RequestSizeLimit(128 * 1024)]
    public Task<IActionResult> Start(Guid id, [FromBody] JsonObject request, CancellationToken ct) => Execute(async () =>
    {
        var admission = await service.StartAsync(id, HttpContext.GetAccountId()!.Value, HttpContext.GetDeviceId()!.Value, request, ct);
        if (admission.Status is "accepted" or "replayed")
        {
            var response = await service.ReadRunAsync(HttpContext.GetAccountId()!.Value, admission.RunId!.Value.Value, ct);
            return Accepted($"/api/v1/creative-apps/runs/{admission.RunId}", response);
        }
        var (code, message) = admission.Status switch
        {
            "idempotency_conflict" => (409, "此提交标识已经用于不同参数，请查询原任务，或明确发起新生成。"),
            "version_changed" => (409, "应用已更新或下架，请刷新配置后重新确认。"),
            "daily_limit" => (429, "已达到此应用今日提交额度，请明日再试。"),
            _ => (429, "排队任务过多，请等待已有任务完成。"),
        };
        if (code == 429) Response.Headers.RetryAfter = "60";
        return StatusCode(code, new JsonObject { ["error"] = admission.Status, ["message"] = message });
    });

    /// <summary>查询响应丢失的提交结果，不重新生成。</summary>
    [HttpGet("{id:guid}/requests/{key}", Name = "FindCreativeAppRequest")]
    public Task<IActionResult> FindRequest(Guid id, string key, CancellationToken ct) => Execute(async () =>
        key.Length <= 128 && await apps.FindRequestAsync(HttpContext.GetAccountId()!.Value, id, key, ct) is { } run
            ? Ok(await service.ReadRunAsync(HttpContext.GetAccountId()!.Value, run, ct)) : NotFound());

    /// <summary>分页返回当前账户的任务历史。</summary>
    [HttpGet("runs", Name = "ListCreativeAppRuns")]
    public Task<IActionResult> Runs([FromQuery] Guid? appId, [FromQuery] int offset = 0, CancellationToken ct = default) => Execute(async () =>
        Ok(new JsonObject { ["items"] = await apps.ListRunsAsync(HttpContext.GetAccountId()!.Value, appId, Math.Clamp(offset, 0, 100000), ct) }));

    /// <summary>检索本账户的历史图片、视频成品；查询、预览和下载不重新执行工作流。</summary>
    [HttpGet("outputs", Name = "ListCreativeAppOutputs")]
    public Task<IActionResult> Outputs([FromQuery] Guid? appId, [FromQuery] string? search, [FromQuery] string? kind,
        [FromQuery] int days = 0, [FromQuery] int offset = 0, CancellationToken ct = default) => Execute(async () =>
    {
        if (kind is not (null or "" or "image" or "video") || days is not (0 or 7 or 30 or 90) || offset is < 0 or > 100000 || search?.Length > 100)
            return BadRequest(new { error = "invalid_filter", message = "请使用有效的类型、时间范围和分页参数。" });
        return Ok(await apps.ListOutputsAsync(HttpContext.GetAccountId()!.Value, appId, search?.Trim(),
            string.IsNullOrEmpty(kind) ? null : kind, days == 0 ? null : DateTimeOffset.UtcNow.AddDays(-days), offset, ct));
    });

    /// <summary>读取当前账户任务的进度、节点名称和结果。</summary>
    [HttpGet("runs/{id:guid}", Name = "GetCreativeAppRun")]
    public Task<IActionResult> Run(Guid id, CancellationToken ct) => Execute(async () =>
        await service.ReadRunAsync(HttpContext.GetAccountId()!.Value, id, ct) is { } run ? Ok(run) : NotFound());

    /// <summary>请求取消当前账户的任务。</summary>
    [HttpDelete("runs/{id:guid}", Name = "CancelCreativeAppRun")]
    public Task<IActionResult> Cancel(Guid id, CancellationToken ct) => Execute(async () =>
        await apps.CancelAsync(HttpContext.GetAccountId()!.Value, id, ct) ? NoContent() : NotFound());

    /// <summary>只输出经过客户范围过滤的状态快照；重连查询最新状态，不重新运行任务。</summary>
    [HttpGet("runs/{id:guid}/events", Name = "StreamCreativeAppRun")]
    public async Task<IActionResult> Events(Guid id, CancellationToken ct)
    {
        if (HttpContext.GetAccountId() is not { } account || HttpContext.GetDeviceId() is null) return Unauthorized();
        var run = await service.ReadRunAsync(account, id, ct); if (run is null) return NotFound();
        Response.ContentType = "text/event-stream"; Response.Headers.CacheControl = "private, no-cache, no-store"; Response.Headers["X-Accel-Buffering"] = "no";
        string? previous = null;
        // Bound each connection so session revocation is checked again by middleware on reconnect.
        for (var iteration = 0; iteration < 15; iteration++)
        {
            run = await service.ReadRunAsync(account, id, ct); if (run is null) break;
            var payload = run.ToJsonString();
            if (payload != previous) { await Response.WriteAsync($"event: progress\ndata: {payload}\n\n", ct); previous = payload; }
            else await Response.WriteAsync(": heartbeat\n\n", ct);
            await Response.Body.FlushAsync(ct);
            if (CreativeAppContract.Text(run["status"]) is not ("queued" or "running")) break;
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        return new EmptyResult();
    }

    /// <summary>统一检查账户与设备身份，并将可交付的验证错误转换为 HTTP 响应。</summary>
    private async Task<IActionResult> Execute(Func<Task<IActionResult>> action)
    {
        if (HttpContext.GetAccountId() is null || HttpContext.GetDeviceId() is null) return Unauthorized();
        Response.Headers.CacheControl = "private, no-store";
        try { return await action(); }
        catch (CreativeAppException error) { return StatusCode(error.Status, new JsonObject { ["error"] = error.Code, ["message"] = error.Message }); }
    }
}
