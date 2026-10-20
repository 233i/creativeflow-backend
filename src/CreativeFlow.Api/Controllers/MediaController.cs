using Microsoft.AspNetCore.Mvc;
using CreativeFlow.Api.Http.Handlers;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;

namespace CreativeFlow.Api.Controllers;

/// <summary>供上游模型供应商使用的无 Cookie、短期媒体读取接口。</summary>
[ApiController]
[Route("api/v1/media/assets")]
[Tags("media")]
public sealed class MediaController(
    ISignedMediaUrlService signedUrls,
    ICanvasAssetRepository assets,
    IAssetStore assetStore) : ControllerBase
{
    /// <summary>验证签名后交付素材，保留 GET/HEAD 与 Range 行为。</summary>
    [AcceptVerbs(
        "GET",
        "HEAD",
        Route = "{id}",
        Name = "GetSignedMediaAsset")]
    public async Task<IActionResult> GetSignedAsset(
        [FromRoute] string id,
        [FromQuery] string? owner,
        [FromQuery] long? exp,
        [FromQuery] string? sig,
        CancellationToken ct) =>
        await MediaHandlers.GetSignedAssetAsync(
            id,
            owner,
            exp,
            sig,
            HttpContext,
            signedUrls,
            assets,
            assetStore,
            ct);
}
