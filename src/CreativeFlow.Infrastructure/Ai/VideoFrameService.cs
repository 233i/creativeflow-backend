using Microsoft.Extensions.Logging;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Domain.Identity;

namespace CreativeFlow.Infrastructure.Ai;

/// <summary>将视频帧转换为普通的已缓存画布图片素材。</summary>
internal sealed class VideoFrameService(
    ICanvasAssetRepository assets,
    IAssetStore assetStore,
    IVideoFrameExtractor extractor,
    ILogger<VideoFrameService> logger) : IVideoFrameService
{
    /// <summary>视频尾帧缓存使用的稳定派生名称。</summary>
    private const string LastFrameDerivation = "last-frame-v1";

    /// <summary>在当前设备的可访问素材中查找视频，优先复用已有尾帧，否则提取并保存关联的图片素材。</summary>
    public async Task<VideoFrameAssetResult> ExtractLastFrameAsync(
        DeviceId deviceId,
        Guid sourceAssetId,
        CancellationToken ct)
    {
        var source = await assets.FindAsync(deviceId, sourceAssetId, ct);
        if (source is null || source.IsRecycled) return VideoFrameAssetResult.NotFound();
        if (source.Kind != "video"
            || !source.MediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
        {
            return VideoFrameAssetResult.Invalid("Only a stored video can provide a last frame.");
        }

        if (await assets.FindDerivativeAsync(
                deviceId, sourceAssetId, LastFrameDerivation, ct) is { } cached)
        {
            return VideoFrameAssetResult.Success(cached.AssetId, cached.MediaType);
        }

        StoredAsset? stored = null;
        try
        {
            await using var stream = await assetStore.OpenReadAsync(source.StoragePath, ct);
            if (stream is null) return VideoFrameAssetResult.NotFound();

            var bytes = await extractor.ExtractLastFrameAsync(stream, ct);
            stored = await assetStore.SaveAsync(bytes, "image/png", ".png", ct);
            var derived = new CanvasAsset(
                Guid.CreateVersion7(),
                source.DeviceId,
                "image",
                stored.MediaType,
                stored.StoragePath,
                stored.ByteSize,
                source.Prompt,
                DateTimeOffset.UtcNow);

            if (await assets.TryInsertDerivativeAsync(
                    derived, sourceAssetId, LastFrameDerivation, ct))
            {
                return VideoFrameAssetResult.Success(derived.AssetId, derived.MediaType);
            }

            // FFmpeg 运行期间，并发请求创建了相同派生素材。保留胜出项，
            // 删除本次重复负载后再返回缓存元数据。
            await assetStore.DeleteAsync(stored.StoragePath, CancellationToken.None);
            stored = null;
            var winner = await assets.FindDerivativeAsync(
                deviceId, sourceAssetId, LastFrameDerivation, ct);
            return winner is null
                ? VideoFrameAssetResult.Failure("The extracted frame could not be stored.")
                : VideoFrameAssetResult.Success(winner.AssetId, winner.MediaType);
        }
        catch (OperationCanceledException)
        {
            if (stored is not null)
            {
                await DeleteQuietlyAsync(stored.StoragePath);
            }
            throw;
        }
        catch (InvalidDataException exception)
        {
            if (stored is not null)
            {
                await DeleteQuietlyAsync(stored.StoragePath);
            }
            return VideoFrameAssetResult.Invalid(exception.Message);
        }
        catch (Exception exception)
        {
            if (stored is not null)
            {
                await DeleteQuietlyAsync(stored.StoragePath);
            }
            logger.LogError(exception, "Could not extract the final frame from asset {AssetId}.", sourceAssetId);
            return VideoFrameAssetResult.Failure("The video frame service is unavailable.");
        }
    }

    /// <summary>清理失去用途的派生素材，不让清理异常覆盖原处理结果。</summary>
    private async Task DeleteQuietlyAsync(string storagePath)
    {
        try
        {
            await assetStore.DeleteAsync(storagePath, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not remove duplicate derived asset payload.");
        }
    }
}
