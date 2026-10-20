using Microsoft.AspNetCore.Mvc;
using CreativeFlow.Api.Http;
using CreativeFlow.Api.Http.Handlers;
using CreativeFlow.Api.Middleware;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Domain.Identity;

namespace CreativeFlow.Api.Controllers;

/// <summary>管理员资产中心；权限、引用检查与用户素材库复用同一后端端口。</summary>
[ApiController]
[Route("api/v1/admin/assets")]
[Tags("admin")]
public sealed class AdminAssetsController(IAssetCenterRepository assets, IAssetStore store) : ControllerBase
{
    [HttpGet("facets", Name = "GetAssetFacets")]
    public async Task<IActionResult> Facets(CancellationToken ct)
    {
        if (HttpContext.GetAuthenticatedAdmin() is null) return Unauthorized();
        Response.Headers.CacheControl = "private, no-store"; return Ok(await assets.FacetsAsync(null, ct));
    }

    [HttpGet("", Name = "ListManagedAssets")]
    public async Task<IActionResult> List([FromQuery] AssetQuery query, CancellationToken ct)
    {
        if (HttpContext.GetAuthenticatedAdmin() is null) return Unauthorized();
        if (!AssetCenterValidation.Query(query)) return Invalid("资产筛选条件无效。");
        Response.Headers.CacheControl = "private, no-store";
        return Ok(await assets.ListAsync(query, null, ct));
    }

    [HttpGet("{id:guid}", Name = "GetManagedAsset")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        if (HttpContext.GetAuthenticatedAdmin() is null) return Unauthorized();
        Response.Headers.CacheControl = "private, no-store";
        if (await AssetContentHandlers.CheckOriginsAsync(id, HttpContext, assets, ct) is { } denied) return denied;
        var value = await assets.GetAsync(id, null, ct); return value is null ? NotFound() : Ok(value);
    }

    [HttpGet("{id:guid}/content", Name = "GetManagedAssetContent")]
    public async Task<IActionResult> Content(Guid id, [FromQuery] bool download, CancellationToken ct)
    {
        if (HttpContext.GetAuthenticatedAdmin() is null) return Unauthorized();
        if (await AssetContentHandlers.CheckOriginsAsync(id, HttpContext, assets, ct) is { } denied) return denied;
        return await AssetContentHandlers.ContentAsync(id, null, false, download, HttpContext, assets, store, ct);
    }

    [HttpGet("{id:guid}/thumbnail", Name = "GetManagedAssetThumbnail")]
    public async Task<IActionResult> Thumbnail(Guid id, [FromServices] IAssetPreviewService previews, CancellationToken ct)
    {
        if (HttpContext.GetAuthenticatedAdmin() is null) return Unauthorized();
        if (await AssetContentHandlers.CheckOriginsAsync(id, HttpContext, assets, ct) is { } denied) return denied;
        return await AssetContentHandlers.ThumbnailAsync(id, null, false, HttpContext, assets, previews, ct);
    }

    [HttpGet("{id:guid}/deletion-preview", Name = "PreviewManagedAssetDeletion")]
    public async Task<IActionResult> Preview(Guid id, CancellationToken ct)
    {
        if (HttpContext.GetAuthenticatedAdmin() is null) return Unauthorized();
        Response.Headers.CacheControl = "private, no-store";
        var value = await assets.PreviewAsync(id, null, ct); return value is null ? NotFound() : Ok(value);
    }

    [HttpPatch("{id:guid}", Name = "UpdateManagedAsset")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateAssetRequest request, CancellationToken ct)
    {
        if (HttpContext.GetAuthenticatedAdmin() is not { } admin) return Unauthorized();
        if (!AssetCenterValidation.Update(request)) return Invalid("名称、标签、版本或操作原因无效。");
        return Outcome(await assets.UpdateAsync(id, request, admin.Id, null, ct));
    }

    [HttpPost("{id:guid}/{operation}", Name = "ManageAssetLifecycle")]
    public async Task<IActionResult> Action(Guid id, [FromRoute(Name = "operation")] string action, [FromBody] AssetActionRequest request, CancellationToken ct)
    {
        if (HttpContext.GetAuthenticatedAdmin() is not { } admin) return Unauthorized();
        if (action is not ("recycle" or "restore" or "purge" or "publish" or "unpublish")) return NotFound();
        if (!AssetCenterValidation.Action(request)) return Invalid("请填写操作原因并重新查询资产版本。");
        if (action == "publish" && await AssetContentHandlers.CheckOriginsAsync(id, HttpContext, assets, ct) is { } denied) return denied;
        return Outcome(await assets.ActAsync(id, action, request, admin.Id, null, ct));
    }

    [HttpPost("batch", Name = "BatchManageAssets")]
    public async Task<IActionResult> Batch([FromBody] AssetBatchRequest request, CancellationToken ct)
    {
        if (HttpContext.GetAuthenticatedAdmin() is not { } admin) return Unauthorized();
        if (!AssetCenterValidation.Batch(request)) return Invalid("请明确选择 1–50 个资产并填写操作原因。");
        return Ok(await assets.BatchAsync(request, admin.Id, null, ct));
    }

    [HttpGet("storage", Name = "GetAssetStorage")]
    public async Task<IActionResult> Storage(CancellationToken ct)
    {
        if (HttpContext.GetAuthenticatedAdmin() is null) return Unauthorized();
        Response.Headers.CacheControl = "private, no-store"; return Ok(await assets.StorageAsync(ct));
    }

    [HttpPut("storage-policy", Name = "SetAssetStoragePolicy")]
    public async Task<IActionResult> Policy([FromBody] AssetStoragePolicy request, CancellationToken ct)
    {
        if (HttpContext.GetAuthenticatedAdmin() is not { } admin) return Unauthorized();
        if (request.RetentionDays is < 0 or > 365 || request.WarningBytes < 0 || request.AccountWarningBytes < 0) return Invalid("保留期限应为 0–365 天，容量阈值不能为负。");
        await assets.SetPolicyAsync(request, admin.Id, ct); return NoContent();
    }

    [HttpPost("upload", Name = "UploadManagedAsset")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Upload(IFormFile file, [FromServices] ICanvasAssetRepository canvasAssets, CancellationToken ct)
    {
        if (HttpContext.GetAuthenticatedAdmin() is not { } admin) return Unauthorized();
        var device = await assets.GetAdminDeviceAsync(admin.Id, ct);
        HttpContext.Items[WorkflowApiRequestContext.DeviceKey] = new DeviceId(device);
        try { return await AiGenerationHandlers.UploadAssetAsync(file, HttpContext, store, canvasAssets, ct); }
        finally { HttpContext.Items.Remove(WorkflowApiRequestContext.DeviceKey); }
    }

    [HttpPost("{id:guid}/last-frame", Name = "ExtractManagedAssetLastFrame")]
    public async Task<IActionResult> LastFrame(Guid id, [FromServices] IVideoFrameService frames, CancellationToken ct)
    {
        if (HttpContext.GetAuthenticatedAdmin() is null) return Unauthorized();
        if (await AssetContentHandlers.CheckOriginsAsync(id, HttpContext, assets, ct) is { } denied) return denied;
        var content = await assets.GetContentAsync(id, null, false, ct); if (content is null || content.State != "active") return NotFound();
        HttpContext.Items[WorkflowApiRequestContext.DeviceKey] = new DeviceId(content.DeviceId);
        try { return await AiGenerationHandlers.ExtractLastFrameAsync(id.ToString(), HttpContext, frames, ct); }
        finally { HttpContext.Items.Remove(WorkflowApiRequestContext.DeviceKey); }
    }

    [HttpPost("download", Name = "DownloadManagedAssets")]
    public async Task<IActionResult> Download([FromBody] DownloadAssetsRequest request, CancellationToken ct)
    {
        if (HttpContext.GetAuthenticatedAdmin() is null) return Unauthorized();
        return await AssetContentHandlers.DownloadAsync(request, null, HttpContext, assets, store, ct);
    }

    private IActionResult Invalid(string message) => BadRequest(new ErrorResponse("invalid_request", [message]));
    private IActionResult Outcome(AssetActionResult result) => result.Status switch
    {
        "not_found" => NotFound(),
        "blocked" or "changed" => Conflict(new ErrorResponse("asset_" + result.Status, [result.Message])),
        "cleanup_failed" => StatusCode(503, new ErrorResponse("asset_cleanup_failed", [result.Message])),
        _ => Ok(result),
    };
}
