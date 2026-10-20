using Microsoft.AspNetCore.Mvc;
using CreativeFlow.Api.Http;
using CreativeFlow.Api.Http.Handlers;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Contracts.Ai;

namespace CreativeFlow.Api.Controllers;

/// <summary>为 API 编辑器复用素材上传、读取和选择，归属由后台资源过滤器确定。</summary>
[ApiController]
[Route("api/v1/admin/workflow-apis/{id}/assets")]
[Tags("admin")]
[TypeFilter(typeof(WorkflowApiAccessFilter), Arguments = new object[] { true })]
public sealed class AdminWorkflowApiAssetsController(
    ICanvasAssetRepository assets,
    IAssetStore assetStore) : ControllerBase
{
    /// <summary>列出当前 API 可复用的图片素材。</summary>
    [HttpGet("", Name = "ListAdminWorkflowApiAssets")]
    public async Task<IActionResult> List([FromQuery] string? kind, CancellationToken ct) =>
        await AiGenerationHandlers.ListAssetsAsync(HttpContext, assets, ct, kind);

    /// <summary>后台编辑器分页选择素材；作用域仍来自被授权的工作流而不是请求参数。</summary>
    [HttpGet("library", Name = "PageAdminWorkflowAssets")]
    public async Task<IActionResult> Library([FromQuery] AssetQuery query, [FromServices] IAssetCenterRepository center, CancellationToken ct)
    {
        if (HttpContext.GetWorkflowDeviceId() is not { } device) return Unauthorized();
        if (!AssetCenterValidation.Query(query) || query.AccountId is not null) return BadRequest();
        if (query.Public) query = query with { Source = null, GroupId = null, Ungrouped = false, Usage = null };
        return Ok(await center.ListAsync(query with { State = "active" }, device.Value, ct));
    }

    /// <summary>显式复用公开素材到工作流资源设备；不借用原用户权限。</summary>
    [HttpPost("public/{assetId:guid}/reuse", Name = "ReusePublicAdminWorkflowAsset")]
    public async Task<IActionResult> Reuse(Guid assetId, [FromBody] ReusePublicAssetRequest request, [FromServices] IAssetCenterRepository center, CancellationToken ct)
    {
        if (HttpContext.GetWorkflowDeviceId() is not { } device) return Unauthorized();
        if (!AssetCenterValidation.Revision(request.Revision)) return BadRequest();
        var asset = await center.ReuseAsync(assetId, request.Revision, device.Value, ct); return asset is null ? Conflict() : Ok(asset);
    }

    [HttpGet("{assetId:guid}/thumbnail", Name = "GetAdminWorkflowAssetThumbnail")]
    public async Task<IActionResult> Thumbnail(Guid assetId, [FromServices] IAssetCenterRepository center, [FromServices] IAssetPreviewService previews, CancellationToken ct)
    {
        if (HttpContext.GetWorkflowDeviceId() is not { } device) return Unauthorized();
        return await AssetContentHandlers.ThumbnailAsync(assetId, device.Value, false, HttpContext, center, previews, ct);
    }

    [HttpGet("public/{assetId:guid}/thumbnail", Name = "GetPublicAdminWorkflowAssetThumbnail")]
    public async Task<IActionResult> PublicThumbnail(Guid assetId, [FromServices] IAssetCenterRepository center, [FromServices] IAssetPreviewService previews, CancellationToken ct)
    {
        if (HttpContext.GetWorkflowDeviceId() is not { } device) return Unauthorized();
        return await AssetContentHandlers.ThumbnailAsync(assetId, device.Value, true, HttpContext, center, previews, ct);
    }

    /// <summary>读取已有素材分组，供共享的只读选择器使用。</summary>
    [HttpGet("groups", Name = "ListAdminWorkflowApiAssetGroups")]
    public async Task<IActionResult> ListGroups(CancellationToken ct) =>
        await AiGenerationHandlers.ListAssetGroupsAsync(HttpContext, assets, ct);

    /// <summary>读取当前 API 资源范围内的媒体，并支持视频 Range 请求。</summary>
    [HttpGet("{assetId}", Name = "GetAdminWorkflowApiAsset")]
    public async Task<IActionResult> Get([FromRoute] string assetId, CancellationToken ct) =>
        await AiGenerationHandlers.GetAssetAsync(assetId, HttpContext, assets, assetStore, ct);

    /// <summary>上传调试参考图，保持既有文件验证和固定素材 URL。</summary>
    [HttpPost("", Name = "UploadAdminWorkflowApiAsset")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Upload(IFormFile file, CancellationToken ct) =>
        await AiGenerationHandlers.UploadAssetAsync(file, HttpContext, assetStore, assets, ct);
}
