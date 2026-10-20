using CreativeFlow.Application.Contracts.Ai;

namespace CreativeFlow.Application.Abstractions.Ai;

/// <summary>从已经过权限校验的资产生成有界缩略图，不调用模型、不将媒体交给外部服务。</summary>
public interface IAssetPreviewService
{
    Task<Stream?> OpenThumbnailAsync(AssetContent asset, CancellationToken ct);
}
