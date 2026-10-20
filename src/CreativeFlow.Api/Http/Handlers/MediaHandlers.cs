using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Domain.Identity;

namespace CreativeFlow.Api.Http.Handlers;

/// <summary>供上游模型供应商使用的无 Cookie、短期媒体读取接口。</summary>
internal static class MediaHandlers
{
    /// <summary>验证签名后交付素材，保留 GET/HEAD 与 Range 行为。</summary>
    internal static async Task<IActionResult> GetSignedAssetAsync(
        string id,
        string? owner,
        long? exp,
        string? sig,
        HttpContext http,
        ISignedMediaUrlService signedUrls,
        ICanvasAssetRepository assets,
        IAssetStore assetStore,
        CancellationToken ct)
    {
        // 此入口不依赖 Cookie：签名中的设备归属必须与查到的素材完全一致，否则统一返回不存在。
        if (!Guid.TryParse(id, out var assetId)
            || !Guid.TryParse(owner, out var ownerId)
            || exp is not { } expires
            || sig is not { Length: > 0 } signature)
        {
            return ApiResults.NotFound();
        }

        var deviceId = new DeviceId(ownerId);
        if (!signedUrls.Validate(assetId, deviceId, expires, signature))
        {
            return ApiResults.NotFound();
        }

        var asset = await assets.FindAsync(deviceId, assetId, ct);
        if (asset is null || asset.DeviceId != deviceId)
        {
            return ApiResults.NotFound();
        }

        var stream = await assetStore.OpenReadAsync(asset.StoragePath, ct);
        if (stream is null)
        {
            return ApiResults.NotFound();
        }

        http.Response.Headers.CacheControl = "private, max-age=300";
        http.Response.Headers.XContentTypeOptions = "nosniff";
        return ApiResults.Stream(stream, asset.MediaType, enableRangeProcessing: true);
    }
}
