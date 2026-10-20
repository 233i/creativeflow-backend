using Microsoft.AspNetCore.Mvc;
using CreativeFlow.Api.Http;
using CreativeFlow.Api.Middleware;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Application.Features.Administration;
using CreativeFlow.Application.Features.Workflow;
using CreativeFlow.Api.Http.Handlers;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;
using System.Text.Json.Nodes;

namespace CreativeFlow.Api.Controllers;

/// <summary>提供独立管理员鉴权的请求列表、详情和产物预览。</summary>
[ApiController]
[Route("api/v1/admin/requests")]
[Tags("admin")]
public sealed class AdminRequestsController(IRequestHistoryRepository repository,
    RequestHistoryService history, IAssetStore assets) : ControllerBase
{
    /// <summary>按画布或工作流来源分页读取用户请求，拒绝无效筛选避免意外扩大查询范围。</summary>
    [HttpGet("", Name = "ListManagedRequests")]
    public async Task<IActionResult> List([FromQuery] string source, [FromQuery] string? accountId,
        [FromQuery] string? search, [FromQuery] string? status, [FromQuery] int offset = 0,
        [FromQuery] int limit = 30, CancellationToken ct = default)
    {
        if (HttpContext.GetAuthenticatedAdmin() is null) return Unauthorized();
        if (!Source(source) || offset < 0 || limit is < 1 or > 100 || search?.Length > 200
            || (!string.IsNullOrEmpty(status) && status is not ("queued" or "pending" or "running" or "succeeded" or "failed" or "cancelled")))
            return BadRequest(new ErrorResponse("invalid_request", ["请求筛选条件无效。"]));
        Guid? user = null;
        if (!string.IsNullOrWhiteSpace(accountId))
        {
            if (!Guid.TryParse(accountId, out var id) || id == Guid.Empty)
                return BadRequest(new ErrorResponse("invalid_request", ["用户标识无效。"]));
            user = id;
        }
        Response.Headers.CacheControl = "private, no-store";
        return Ok(await repository.ListAsync(new(source, user,
            string.IsNullOrWhiteSpace(search) ? null : search.Trim(), string.IsNullOrEmpty(status) ? null : status, offset, limit), ct));
    }

    /// <summary>读取一次请求及工作流节点明细。</summary>
    [HttpGet("{source}/{id:guid}", Name = "GetManagedRequest")]
    public async Task<IActionResult> Get(string source, Guid id, CancellationToken ct)
    {
        if (HttpContext.GetAuthenticatedAdmin() is null) return Unauthorized();
        if (!Source(source)) return NotFound();
        Response.Headers.CacheControl = "private, no-store";
        if (await CheckResourceAccessAsync(source, id, ct) is { } denied) return denied;
        var result = await history.GetAsync(source, id, ct);
        if (await CheckResourceAccessAsync(source, id, ct) is { } revoked) return revoked;
        return result is null ? NotFound() : Ok(result);
    }

    /// <summary>仅预览该请求实际产生且属于执行用户的媒体，支持视频拖动播放。</summary>
    [HttpGet("{source}/{id:guid}/assets/{assetId:guid}", Name = "GetManagedRequestArtifact")]
    public async Task<IActionResult> GetAsset(string source, Guid id, Guid assetId, CancellationToken ct)
    {
        if (HttpContext.GetAuthenticatedAdmin() is null) return Unauthorized();
        if (!Source(source)) return NotFound();
        if (await CheckResourceAccessAsync(source, id, ct) is { } denied) return denied;
        var asset = await history.GetAssetAsync(source, id, assetId, ct);
        if (asset is null) return NotFound();
        // 管理端不缓存用户产物；会话撤销后浏览器必须重新通过管理员鉴权。
        Response.Headers.CacheControl = "private, no-store";
        Response.Headers.XContentTypeOptions = "nosniff";
        if (!asset.MediaType.StartsWith("video/", StringComparison.Ordinal)
            && asset.MediaType is not ("image/png" or "image/jpeg" or "image/webp" or "image/gif")) return NotFound();
        var stream = await assets.OpenReadAsync(asset.StoragePath, ct);
        if (await CheckResourceAccessAsync(source, id, ct) is { } revoked)
        {
            if (stream is not null) await stream.DisposeAsync();
            return revoked;
        }
        return stream is null ? NotFound() : ApiResults.Stream(stream, asset.MediaType, enableRangeProcessing: true);
    }

    /// <summary>旧诊断入口不能绕过通用图的资源撤权；媒体历史仍使用原管理员及素材归属规则。</summary>
    private async Task<IActionResult?> CheckResourceAccessAsync(string source, Guid id, CancellationToken ct)
    {
        if (source != "workflow") return null;
        var document = await repository.FindAsync(source, id, ct);
        if (document is null) return NotFound();
        JsonNode? graph;
        try { graph = JsonNode.Parse(document.InputJson); }
        catch (System.Text.Json.JsonException) { return StatusCode(403); }
        if (graph is not JsonObject snapshot) return StatusCode(403);
        if (snapshot["schemaVersion"]?.ToString() != WorkflowGraphValidator.SchemaVersion.ToString()) return null;
        Response.Headers.CacheControl = "private, no-store";
        if (!Guid.TryParse(document.Summary.ProjectId, out var workflowId)
            || HttpContext.RequestServices.GetService<IWorkflowRunRepository>() is not { } runs) return StatusCode(403);
        var run = await runs.FindAsync(new DeviceId(document.DeviceId), new WorkflowId(workflowId), new WorkflowRunId(id), ct)
            ?? await runs.FindExternalAsync(new WorkflowId(workflowId), new WorkflowRunId(id), ct);
        return run is null ? NotFound() : await WorkflowV2Handlers.CheckResourcesAsync(run, HttpContext, ct);
    }

    /// <summary>只接受明确的两个管理来源。</summary>
    private static bool Source(string source) => source is "canvas" or "workflow";
}
