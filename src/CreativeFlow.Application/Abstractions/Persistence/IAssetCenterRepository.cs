using CreativeFlow.Application.Contracts.Ai;

namespace CreativeFlow.Application.Abstractions.Persistence;

/// <summary>统一资产管理端口；scopeDevice 为空只允许由已认证管理员入口调用。</summary>
public interface IAssetCenterRepository
{
    Task<AssetPage> ListAsync(AssetQuery query, Guid? scopeDevice, CancellationToken ct);
    Task<AssetFacets> FacetsAsync(Guid? scopeDevice, CancellationToken ct);
    Task<AssetDetail?> GetAsync(Guid id, Guid? scopeDevice, CancellationToken ct);
    Task<AssetContent?> GetContentAsync(Guid id, Guid? scopeDevice, bool publicOnly, CancellationToken ct);
    Task<AssetDeletionPreview?> PreviewAsync(Guid id, Guid? scopeDevice, CancellationToken ct);
    Task<AssetActionResult> UpdateAsync(Guid id, UpdateAssetRequest request, Guid actor, Guid? scopeDevice, CancellationToken ct);
    Task<AssetActionResult> ActAsync(Guid id, string action, AssetActionRequest request, Guid actor, Guid? scopeDevice, CancellationToken ct);
    Task<AssetActionResult[]> BatchAsync(AssetBatchRequest request, Guid actor, Guid? scopeDevice, CancellationToken ct);
    Task<AssetItem?> ReuseAsync(Guid id, string revision, Guid deviceId, CancellationToken ct);
    Task<AssetStorageReport> StorageAsync(CancellationToken ct);
    Task SetPolicyAsync(AssetStoragePolicy policy, Guid adminId, CancellationToken ct);
    Task<Guid> GetAdminDeviceAsync(Guid adminId, CancellationToken ct);
}
