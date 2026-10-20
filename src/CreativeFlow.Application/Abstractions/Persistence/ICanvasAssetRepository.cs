using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;

namespace CreativeFlow.Application.Abstractions.Persistence;

/// <summary>已存储图片或视频的元数据，字节内容本身位于磁盘。</summary>
/// <param name="AssetId">素材唯一标识。</param>
/// <param name="DeviceId">资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</param>
/// <param name="Kind">当前资源或数据项的种类。</param>
/// <param name="MediaType">内容的 MIME 类型，响应时沿用已验证的存储值。</param>
/// <param name="StoragePath">存储后端的不透明键，不能直接当作用户可控文件路径。</param>
/// <param name="ByteSize">素材或文件内容的字节数。</param>
/// <param name="Prompt">提交给生成模型的用户提示词。</param>
/// <param name="CreatedAt">记录创建时间，沿用当前 UTC 与序列化约定。</param>
/// <param name="GroupId">素材分组标识；允许空值的场景表示未分组。</param>
/// <param name="SourceUrl">上游返回的原始素材地址，保留供后续来源选择使用。</param>
/// <param name="SourceUrlExpiresAt">上游原始素材地址的已知失效时间。</param>
public sealed record CanvasAsset(
    Guid AssetId,
    DeviceId DeviceId,
    string Kind,
    string MediaType,
    string StoragePath,
    long ByteSize,
    string? Prompt,
    DateTimeOffset CreatedAt,
    Guid? GroupId = null,
    string? SourceUrl = null,
    DateTimeOffset? SourceUrlExpiresAt = null)
{
    /// <summary>回收站内容只供历史读取，不能用于新的模型请求。</summary>
    public bool IsRecycled { get; init; }
    /// <summary>上传时保留的安全展示名称，不参与存储路径构造。</summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>服务端能证实的来源类型；旧记录不猜测来源。</summary>
    public string Origin { get; init; } = "unknown";
}

public interface ICanvasAssetRepository
{
    /// <summary>按原有存储规则写入素材。</summary>
    Task InsertAsync(CanvasAsset asset, CancellationToken ct);

    /// <summary>
    /// 加载素材元数据。按设备限定作用域，防止调用方通过猜测标识读取其他设备的媒体。
    /// </summary>
    Task<CanvasAsset?> FindAsync(DeviceId deviceId, Guid assetId, CancellationToken ct);

    /// <summary>查找自有源素材的已缓存派生素材。</summary>
    Task<CanvasAsset?> FindDerivativeAsync(
        DeviceId deviceId,
        Guid sourceAssetId,
        string derivation,
        CancellationToken ct);

    /// <summary>
    /// 仅在不存在相同来源和派生方式的素材时插入。返回假表示另一请求已赢得竞争，
    /// 应复用其缓存素材。
    /// </summary>
    Task<bool> TryInsertDerivativeAsync(
        CanvasAsset asset,
        Guid sourceAssetId,
        string derivation,
        CancellationToken ct);

    /// <summary>
    /// 列出设备的素材，最新的排在前面。
    /// </summary>
    /// <remarks>
    /// 采用数量上限而非分页：素材库是选择器而非归档浏览器，生成过数千张图片的设备
    /// 不应能通过一次请求返回全部内容。
    /// </remarks>
    Task<IReadOnlyList<CanvasAsset>> ListAsync(
        DeviceId deviceId,
        string? kind,
        int limit,
        CancellationToken ct);

    /// <summary>列出设备的素材分组及各组已分配素材数量。</summary>
    Task<IReadOnlyList<CanvasAssetGroup>> ListGroupsAsync(DeviceId deviceId, CancellationToken ct);

    /// <summary>当分组属于该设备时加载它。</summary>
    Task<CanvasAssetGroup?> FindGroupAsync(DeviceId deviceId, Guid groupId, CancellationToken ct);

    /// <summary>创建分组；名称已存在时返回空值。</summary>
    Task<CanvasAssetGroup?> InsertGroupAsync(CanvasAssetGroup group, CancellationToken ct);

    /// <summary>重命名分组；分组已不存在或名称被占用时返回空值。</summary>
    Task<CanvasAssetGroup?> RenameGroupAsync(
        DeviceId deviceId,
        Guid groupId,
        string name,
        DateTimeOffset updatedAt,
        CancellationToken ct);

    /// <summary>删除分组。外键会让其中素材变为未分组状态。</summary>
    Task<bool> DeleteGroupAsync(DeviceId deviceId, Guid groupId, CancellationToken ct);

    /// <summary>批量分配或取消分配设备拥有的素材。</summary>
    Task<int> AssignGroupAsync(
        DeviceId deviceId,
        IReadOnlyList<Guid> assetIds,
        Guid? groupId,
        CancellationToken ct);
}
