using Microsoft.AspNetCore.Mvc;
using CreativeFlow.Api.Http;
using CreativeFlow.Api.Http.Handlers;
using CreativeFlow.Api.Middleware;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Contracts.Ai;

namespace CreativeFlow.Api.Controllers;

/// <summary>用户素材库分页、公共目录与回收站入口；永远从已验证会话建立所有权。</summary>
[ApiController]
[Route("api/v1/canvas/asset-library")]
[Tags("canvas")]
public sealed class CanvasAssetLibraryController(IAssetCenterRepository assets, IAssetStore store) : ControllerBase
{
    [HttpGet("", Name = "PageCanvasAssetLibrary")]
    public async Task<IActionResult> List([FromQuery] AssetQuery query, CancellationToken ct)
    {
        if (HttpContext.GetDeviceId() is not { } device) return Unauthorized();
        if (!AssetCenterValidation.Query(query) || query.AccountId is not null) return BadRequest();
        if (query.Public) query = query with { Source = null, GroupId = null, Ungrouped = false, Usage = null, State = "active" };
        Response.Headers.CacheControl = "private, no-store"; return Ok(await assets.ListAsync(query, device.Value, ct));
    }

    [HttpGet("{id:guid}", Name = "GetLibraryAsset")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        if (HttpContext.GetDeviceId() is not { } device) return Unauthorized();
        Response.Headers.CacheControl = "private, no-store"; var detail = await assets.GetAsync(id, device.Value, ct); return detail is null ? NotFound() : Ok(detail);
    }

    [HttpGet("{id:guid}/deletion-preview", Name = "PreviewLibraryAssetDeletion")]
    public async Task<IActionResult> Preview(Guid id, CancellationToken ct)
    {
        if (HttpContext.GetDeviceId() is not { } device) return Unauthorized();
        var value = await assets.PreviewAsync(id, device.Value, ct); return value is null ? NotFound() : Ok(value);
    }

    [HttpPost("{id:guid}/{operation}", Name = "ManageLibraryAsset")]
    public async Task<IActionResult> Action(Guid id, [FromRoute(Name = "operation")] string action, [FromBody] AssetActionRequest request, CancellationToken ct)
    {
        if (HttpContext.GetDeviceId() is not { } device) return Unauthorized();
        if (action is not ("restore" or "recycle") || !AssetCenterValidation.Action(request)) return BadRequest();
        var result = await assets.ActAsync(id, action, request, device.Value, device.Value, ct);
        return result.Status == "not_found" ? NotFound() : result.Status is "blocked" or "changed" ? Conflict(new ErrorResponse("asset_" + result.Status, [result.Message])) : Ok(result);
    }

    [HttpPost("public/{id:guid}/reuse", Name = "ReusePublicLibraryAsset")]
    public async Task<IActionResult> Reuse(Guid id, [FromBody] ReusePublicAssetRequest request, CancellationToken ct)
    {
        if (HttpContext.GetDeviceId() is not { } device) return Unauthorized();
        if (!AssetCenterValidation.Revision(request.Revision)) return BadRequest();
        var item = await assets.ReuseAsync(id, request.Revision, device.Value, ct);
        return item is null ? Conflict(new ErrorResponse("asset_changed", ["公共素材已变化或已下架，请重新查询。"])) : Ok(item);
    }

    [HttpGet("public/{id:guid}/content", Name = "ReadPublicLibraryAsset")]
    public async Task<IActionResult> PublicContent(Guid id, [FromQuery] bool download, CancellationToken ct)
    {
        if (HttpContext.GetDeviceId() is not { } device) return Unauthorized();
        return await AssetContentHandlers.ContentAsync(id, device.Value, true, download, HttpContext, assets, store, ct);
    }

    [HttpGet("public/{id:guid}/thumbnail", Name = "ReadPublicLibraryThumbnail")]
    public async Task<IActionResult> Thumbnail(Guid id, [FromServices] IAssetPreviewService previews, CancellationToken ct)
    {
        if (HttpContext.GetDeviceId() is not { } device) return Unauthorized();
        return await AssetContentHandlers.ThumbnailAsync(id, device.Value, true, HttpContext, assets, previews, ct);
    }

    [HttpPost("download", Name = "DownloadLibraryAssets")]
    public async Task<IActionResult> Download([FromBody] DownloadAssetsRequest request, CancellationToken ct)
    {
        if (HttpContext.GetDeviceId() is not { } device) return Unauthorized();
        return await AssetContentHandlers.DownloadAsync(request, device.Value, HttpContext, assets, store, ct);
    }
}
