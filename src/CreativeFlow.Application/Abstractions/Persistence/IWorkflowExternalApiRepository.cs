using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;

namespace CreativeFlow.Application.Abstractions.Persistence;

/// <param name="Deployment">发布后固定的工作流版本快照。</param>
/// <param name="WorkflowName">工作流的显示名称。</param>
/// <param name="OwnerDeviceId">资源实际归属的设备标识，用于恢复业务访问范围。</param>
public sealed record PublishedWorkflowCatalogItem(
    WorkflowDeployment Deployment,
    string WorkflowName,
    DeviceId OwnerDeviceId);

public interface IWorkflowExternalApiRepository
{
    /// <summary>在原子写入边界内保存新的工作流发布版本及输入输出契约。</summary>
    Task<WorkflowDeployment?> PublishAsync(
        DeviceId deviceId,
        WorkflowDeployment deployment,
        CancellationToken ct);

    /// <summary>读取工作流已有的发布版本。</summary>
    Task<IReadOnlyList<WorkflowDeployment>> ListDeploymentsAsync(
        DeviceId deviceId,
        WorkflowId workflowId,
        CancellationToken ct);

    /// <summary>读取工作流当前公开的发布快照。</summary>
    Task<WorkflowDeployment?> FindPublishedAsync(
        DeviceId deviceId,
        WorkflowId workflowId,
        CancellationToken ct);

    /// <summary>读取工作流当前公开的发布快照。</summary>
    Task<PublishedWorkflowCatalogItem?> FindPublishedAsync(
        WorkflowId workflowId,
        CancellationToken ct);

    /// <summary>根据发布版本标识读取固定快照。</summary>
    Task<PublishedWorkflowCatalogItem?> FindDeploymentAsync(
        WorkflowDeploymentId deploymentId,
        CancellationToken ct);

    /// <summary>读取当前公开可用的发布条目。</summary>
    Task<IReadOnlyList<PublishedWorkflowCatalogItem>> ListPublishedAsync(
        int limit,
        CancellationToken ct);

    /// <summary>停用工作流的对外发布入口。</summary>
    Task<bool> DisableAsync(
        DeviceId deviceId,
        WorkflowId workflowId,
        DateTimeOffset now,
        CancellationToken ct);

    /// <summary>保存工作流 API Key 的摘要与安全元数据。</summary>
    Task InsertApiKeyAsync(WorkflowApiKey apiKey, CancellationToken ct);

    /// <summary>列出工作流 API Key 的安全元数据。</summary>
    Task<IReadOnlyList<WorkflowApiKey>> ListApiKeysAsync(
        DeviceId deviceId,
        WorkflowId workflowId,
        CancellationToken ct);

    /// <summary>撤销指定工作流 API Key。</summary>
    Task<bool> RevokeApiKeyAsync(
        DeviceId deviceId,
        WorkflowId workflowId,
        WorkflowApiKeyId apiKeyId,
        DateTimeOffset now,
        CancellationToken ct);

    /// <summary>使用公开前缀定位 API Key 验证记录。</summary>
    Task<WorkflowApiKey?> FindApiKeyByPrefixAsync(string prefix, CancellationToken ct);

    /// <summary>后台执行按服务端保存的 Key 标识复验期限与撤销状态，不需要或返回明文凭据。</summary>
    Task<WorkflowApiKey?> FindApiKeyAsync(WorkflowApiKeyId id, CancellationToken ct);

    /// <summary>记录有效 API Key 的最近使用时间。</summary>
    Task TouchApiKeyAsync(WorkflowApiKeyId apiKeyId, DateTimeOffset now, CancellationToken ct);

    /// <summary>保存外部工作流上传素材的归属与期限。</summary>
    Task InsertExternalAssetAsync(WorkflowExternalAsset asset, CancellationToken ct);

    /// <summary>读取属于指定工作流且符合既有有效性条件的上传素材。</summary>
    Task<WorkflowExternalAsset?> FindExternalAssetAsync(
        WorkflowId workflowId,
        Guid assetId,
        CancellationToken ct);

    /// <summary>标记本次外部运行已使用的上传素材。</summary>
    Task MarkExternalAssetsConsumedAsync(
        WorkflowId workflowId,
        IReadOnlyList<Guid> assetIds,
        DateTimeOffset now,
        CancellationToken ct);
}
