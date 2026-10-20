using Microsoft.AspNetCore.Mvc;
using CreativeFlow.Api.Http.Handlers;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Api.Http;

namespace CreativeFlow.Api.Controllers;

/// <summary>装配素材库的 HTTP 入口及其用例/端口依赖。</summary>
[ApiController]
[Route("api/v1/canvas/assets")]
[Tags("canvas")]
public sealed class CanvasAssetsController(
    ICanvasAssetRepository assets,
    IAssetStore assetStore,
    IVideoFrameService frames) : ControllerBase
{
    /// <summary>读取当前范围内的素材分组及数量。</summary>
    [HttpGet("groups", Name = "ListCanvasAssetGroups")]
    public async Task<IActionResult> ListGroups(CancellationToken ct) =>
        await AiGenerationHandlers.ListAssetGroupsAsync(HttpContext, assets, ct);

    /// <summary>创建素材分组，并沿用重名处理规则。</summary>
    [HttpPost("groups", Name = "CreateCanvasAssetGroup")]
    public async Task<IActionResult> CreateGroup(
        [FromBody] CreateCanvasAssetGroupRequest request,
        CancellationToken ct) =>
        await AiGenerationHandlers.CreateAssetGroupAsync(request, HttpContext, assets, ct);

    /// <summary>修改素材分组名称。</summary>
    [HttpPatch("groups/{id}", Name = "RenameCanvasAssetGroup")]
    public async Task<IActionResult> RenameGroup(
        [FromRoute] string id,
        [FromBody] UpdateCanvasAssetGroupRequest request,
        CancellationToken ct) =>
        await AiGenerationHandlers.RenameAssetGroupAsync(
            id,
            request,
            HttpContext,
            assets,
            ct);

    /// <summary>删除素材分组，其素材按原规则回到未分组状态。</summary>
    [HttpDelete("groups/{id}", Name = "DeleteCanvasAssetGroup")]
    public async Task<IActionResult> DeleteGroup([FromRoute] string id, CancellationToken ct) =>
        await AiGenerationHandlers.DeleteAssetGroupAsync(id, HttpContext, assets, ct);

    /// <summary>批量分配或取消素材分组。</summary>
    [HttpPatch("group", Name = "AssignCanvasAssetsToGroup")]
    public async Task<IActionResult> AssignGroup(
        [FromBody] AssignCanvasAssetsRequest request,
        CancellationToken ct) =>
        await AiGenerationHandlers.AssignAssetsToGroupAsync(
            request,
            HttpContext,
            assets,
            ct);

    /// <summary>提取并复用视频素材的最后可解码帧。</summary>
    [HttpPost("{id}/last-frame", Name = "ExtractCanvasVideoLastFrame")]
    public async Task<IActionResult> ExtractLastFrame(
        [FromRoute] string id,
        CancellationToken ct) =>
        await AiGenerationHandlers.ExtractLastFrameAsync(id, HttpContext, frames, ct);

    /// <summary>读取指定素材内容，保留缺失资源的既有处理。</summary>
    [HttpGet("{id}", Name = "GetCanvasAsset")]
    public async Task<IActionResult> Get([FromRoute] string id, CancellationToken ct) =>
        await AiGenerationHandlers.GetAssetAsync(id, HttpContext, assets, assetStore, ct);

    /// <summary>读取有权限的图片或视频缩略图，回收站及私有资产不会通过公开 URL 绕过鉴权。</summary>
    [HttpGet("{id:guid}/thumbnail", Name = "GetCanvasAssetThumbnail")]
    public async Task<IActionResult> Thumbnail(Guid id, [FromServices] IAssetCenterRepository center,
        [FromServices] IAssetPreviewService previews, CancellationToken ct)
    {
        if (HttpContext.GetWorkflowDeviceId() is not { } device) return Unauthorized();
        return await AssetContentHandlers.ThumbnailAsync(id, device.Value, false, HttpContext, center, previews, ct);
    }

    /// <summary>列出可访问的素材，保留现有筛选、排序与访问范围。</summary>
    [HttpGet("", Name = "ListCanvasAssets")]
    public async Task<IActionResult> List(
        [FromQuery] string? kind,
        [FromQuery] int? limit,
        CancellationToken ct) =>
        await AiGenerationHandlers.ListAssetsAsync(HttpContext, assets, ct, kind, limit);

    /// <summary>删除指定素材，保留不存在或不可访问时的返回语义。</summary>
    [HttpDelete("{id}", Name = "DeleteCanvasAsset")]
    public async Task<IActionResult> Delete([FromRoute] string id, CancellationToken ct) =>
        await AiGenerationHandlers.DeleteAssetAsync(id, HttpContext, assets, assetStore, ct);

    /// <summary>验证并保存用户上传的素材。</summary>
    [HttpPost("", Name = "UploadCanvasAsset")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Upload(IFormFile file, CancellationToken ct) =>
        await AiGenerationHandlers.UploadAssetAsync(
            file,
            HttpContext,
            assetStore,
            assets,
            ct);
}
