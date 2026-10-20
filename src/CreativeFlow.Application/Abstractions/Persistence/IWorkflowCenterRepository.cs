using CreativeFlow.Application.Contracts.Ai;

namespace CreativeFlow.Application.Abstractions.Persistence;

/// <summary>管理员工作流中心的聚合、历史查询及受保护的软删除，不改变原资源归属。</summary>
public interface IWorkflowCenterRepository
{
    /// <summary>在服务端聚合 API 工作流和历史模板，并对相同权限范围分页、排序及计数。</summary>
    Task<WorkflowCenterPage> ListAsync(WorkflowCenterQuery query, bool includeApis, CancellationToken ct);

    /// <summary>按原工作流或模板调试来源分页列出运行，不混入其他来源的同 ID 记录。</summary>
    Task<WorkflowCenterRunPage> ListRunsAsync(string source, Guid id, string? status, int offset, int limit, CancellationToken ct);

    /// <summary>先核对来源与运行的真实关联，再读取原运行的权限快照。</summary>
    Task<WorkflowCenterRunScope?> FindRunAsync(string source, Guid id, Guid runId, CancellationToken ct);

    /// <summary>读取当前删除影响；仅支持后台工作流和历史模板，不包含个人工作流。</summary>
    Task<WorkflowDeletionPreview?> PreviewDeletionAsync(string source, Guid id, CancellationToken ct);

    /// <summary>持有资源锁重新核对发布和任务状态，软删除并记入管理员审计。</summary>
    Task<WorkflowDeletionResult> DeleteAsync(string source, Guid id, Guid adminId, DeleteWorkflowRequest request, CancellationToken ct);
}

/// <param name="WorkflowId">原运行所属工作流，不是模板标识。</param>
/// <param name="DeviceId">运行的原执行设备。</param>
/// <param name="TriggerType">原持久化触发类型。</param>
public sealed record WorkflowCenterRunScope(Guid WorkflowId, Guid DeviceId, string TriggerType);
