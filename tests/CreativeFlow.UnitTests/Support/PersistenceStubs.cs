using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;

namespace CreativeFlow.UnitTests.Support;

/// <summary>只开放测试明确覆盖的数据访问；意外写入或查询直接失败。</summary>
internal abstract class StubProviderRepository : IProviderRepository
{
    /// <summary>默认拒绝列出渠道，具体测试必须显式覆盖其需要的目录查询。</summary>
    public virtual Task<IReadOnlyList<AiProvider>> ListAsync(CancellationToken ct) =>
        throw new NotSupportedException("This database operation is outside the test scenario.");

    /// <summary>默认拒绝按标识查询渠道，避免测试意外触及未配置的仓储路径。</summary>
    public virtual Task<AiProvider?> FindAsync(ProviderId providerId, CancellationToken ct) =>
        throw new NotSupportedException("This database operation is outside the test scenario.");

    /// <summary>默认拒绝按名称查询渠道，供需要该操作的测试单独覆盖。</summary>
    public virtual Task<AiProvider?> FindByNameAsync(string name, CancellationToken ct) =>
        throw new NotSupportedException("This database operation is outside the test scenario.");

    /// <summary>默认拒绝按内置标识查询渠道，供需要该操作的测试单独覆盖。</summary>
    public virtual Task<AiProvider?> FindByBuiltinIdAsync(string builtinId, CancellationToken ct) =>
        throw new NotSupportedException("This database operation is outside the test scenario.");

    /// <summary>默认拒绝插入渠道，避免测试意外修改配置。</summary>
    public virtual Task InsertAsync(AiProvider provider, CancellationToken ct) =>
        throw new NotSupportedException("This database operation is outside the test scenario.");

    /// <summary>默认拒绝更新渠道或密钥，供需要写入的测试显式覆盖。</summary>
    public virtual Task UpdateAsync(AiProvider provider, ApiKeyUpdate apiKey, CancellationToken ct) =>
        throw new NotSupportedException("This database operation is outside the test scenario.");

    /// <summary>默认拒绝删除渠道，防止测试未声明的配置清理。</summary>
    public virtual Task<bool> DeleteAsync(ProviderId providerId, CancellationToken ct) =>
        throw new NotSupportedException("This database operation is outside the test scenario.");

    /// <summary>默认拒绝查询渠道排序位置，供排序测试按需覆盖。</summary>
    public virtual Task<int> NextSortOrderAsync(CancellationToken ct) =>
        throw new NotSupportedException("This database operation is outside the test scenario.");

    /// <summary>默认拒绝插入单个模型，防止测试意外写模型目录。</summary>
    public virtual Task InsertModelAsync(AiModel model, CancellationToken ct) =>
        throw new NotSupportedException("This database operation is outside the test scenario.");

    /// <summary>默认拒绝更新模型，供模型配置测试按需覆盖。</summary>
    public virtual Task UpdateModelAsync(AiModel model, CancellationToken ct) =>
        throw new NotSupportedException("This database operation is outside the test scenario.");

    /// <summary>默认拒绝删除模型，防止测试未声明的模型清理。</summary>
    public virtual Task<bool> DeleteModelAsync(ModelId modelId, CancellationToken ct) =>
        throw new NotSupportedException("This database operation is outside the test scenario.");

    /// <summary>默认拒绝按标识查询模型，供所需场景显式覆盖。</summary>
    public virtual Task<AiModel?> FindModelAsync(ModelId modelId, CancellationToken ct) =>
        throw new NotSupportedException("This database operation is outside the test scenario.");

    /// <summary>默认拒绝批量导入模型，避免隐式创建测试未声明的配置。</summary>
    public virtual Task<int> InsertModelsIfAbsentAsync(IReadOnlyList<AiModel> models, CancellationToken ct) =>
        throw new NotSupportedException("This database operation is outside the test scenario.");

    /// <summary>默认拒绝批量切换模型启用状态。</summary>
    public virtual Task SetModelsEnabledAsync(ProviderId providerId, bool enabled, CancellationToken ct) =>
        throw new NotSupportedException("This database operation is outside the test scenario.");

}

/// <summary>只开放测试明确覆盖的数据访问；意外写入或查询直接失败。</summary>
internal abstract class StubCanvasAssetRepository : ICanvasAssetRepository
{
    /// <summary>默认拒绝保存素材元数据，供具体生成测试显式记录或模拟失败。</summary>
    public virtual Task InsertAsync(CanvasAsset asset, CancellationToken ct) =>
        throw new NotSupportedException("This database operation is outside the test scenario.");

    /// <summary>默认拒绝读取素材，供具体测试定义可访问数据。</summary>
    public virtual Task<CanvasAsset?> FindAsync(DeviceId deviceId, Guid assetId, CancellationToken ct) =>
        throw new NotSupportedException("This database operation is outside the test scenario.");

    /// <summary>默认拒绝查找派生素材，供缓存复用测试按需覆盖。</summary>
    public virtual Task<CanvasAsset?> FindDerivativeAsync(
        DeviceId deviceId,
        Guid sourceAssetId,
        string derivation,
        CancellationToken ct) =>
        throw new NotSupportedException("This database operation is outside the test scenario.");

    /// <summary>默认拒绝插入派生素材，供竞争和缓存测试按需覆盖。</summary>
    public virtual Task<bool> TryInsertDerivativeAsync(
        CanvasAsset asset,
        Guid sourceAssetId,
        string derivation,
        CancellationToken ct) =>
        throw new NotSupportedException("This database operation is outside the test scenario.");

    /// <summary>默认拒绝列出素材，避免测试依赖未配置的素材库数据。</summary>
    public virtual Task<IReadOnlyList<CanvasAsset>> ListAsync(
        DeviceId deviceId,
        string? kind,
        int limit,
        CancellationToken ct) =>
        throw new NotSupportedException("This database operation is outside the test scenario.");

    /// <summary>默认拒绝删除素材元数据，防止场景外清理。</summary>
    public virtual Task<string?> DeleteAsync(DeviceId deviceId, Guid assetId, CancellationToken ct) =>
        throw new NotSupportedException("This database operation is outside the test scenario.");

    /// <summary>默认拒绝列出素材分组，供分组测试显式覆盖。</summary>
    public virtual Task<IReadOnlyList<CanvasAssetGroup>> ListGroupsAsync(DeviceId deviceId, CancellationToken ct) =>
        throw new NotSupportedException("This database operation is outside the test scenario.");

    /// <summary>默认拒绝查找指定素材分组。</summary>
    public virtual Task<CanvasAssetGroup?> FindGroupAsync(DeviceId deviceId, Guid groupId, CancellationToken ct) =>
        throw new NotSupportedException("This database operation is outside the test scenario.");

    /// <summary>默认拒绝创建素材分组。</summary>
    public virtual Task<CanvasAssetGroup?> InsertGroupAsync(CanvasAssetGroup group, CancellationToken ct) =>
        throw new NotSupportedException("This database operation is outside the test scenario.");

    /// <summary>默认拒绝重命名素材分组。</summary>
    public virtual Task<CanvasAssetGroup?> RenameGroupAsync(
        DeviceId deviceId,
        Guid groupId,
        string name,
        DateTimeOffset updatedAt,
        CancellationToken ct) =>
        throw new NotSupportedException("This database operation is outside the test scenario.");

    /// <summary>默认拒绝删除素材分组。</summary>
    public virtual Task<bool> DeleteGroupAsync(DeviceId deviceId, Guid groupId, CancellationToken ct) =>
        throw new NotSupportedException("This database operation is outside the test scenario.");

    /// <summary>默认拒绝批量调整素材分组归属。</summary>
    public virtual Task<int> AssignGroupAsync(
        DeviceId deviceId,
        IReadOnlyList<Guid> assetIds,
        Guid? groupId,
        CancellationToken ct) =>
        throw new NotSupportedException("This database operation is outside the test scenario.");

}
