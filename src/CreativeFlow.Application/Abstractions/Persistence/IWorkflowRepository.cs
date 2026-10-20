using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;

namespace CreativeFlow.Application.Abstractions.Persistence;

public interface IWorkflowRepository
{
    /// <summary>读取全局 API 工作流及其最新发布状态，仅供管理员入口使用。</summary>
    Task<IReadOnlyList<ManagedWorkflowApi>> ListApisAsync(CancellationToken ct);

    /// <summary>按标识读取后台专用 API 工作流，调用方必须先验证管理权限。</summary>
    Task<Workflow?> FindApiAsync(WorkflowId workflowId, CancellationToken ct, bool includeDeleted = false);

    /// <summary>列出工作流，保留现有筛选、排序与访问范围。</summary>
    Task<IReadOnlyList<WorkflowSummary>> ListAsync(DeviceId deviceId, CancellationToken ct);

    /// <summary>按当前查找键读取已有配置或目录项。</summary>
    Task<Workflow?> FindAsync(DeviceId deviceId, WorkflowId workflowId, CancellationToken ct);

    /// <summary>按原有存储规则写入工作流。</summary>
    Task InsertAsync(Workflow workflow, CancellationToken ct);

    /// <summary>为模板准备或更新既有调试工作流。</summary>
    Task<Workflow> UpsertTemplateDebugAsync(
        WorkflowTemplateId templateId,
        DeviceId deviceId,
        string name,
        int schemaVersion,
        string graphJson,
        DateTimeOffset now,
        CancellationToken ct);

    /// <summary>更新工作流，保留原有访问范围和并发结果。</summary>
    Task<bool> UpdateAsync(Workflow workflow, CancellationToken ct);

    /// <summary>删除工作流，保留不存在或不可访问时的返回语义。</summary>
    Task<bool> DeleteAsync(DeviceId deviceId, WorkflowId workflowId, CancellationToken ct);
}

/// <param name="WorkflowId">API 工作流的稳定标识。</param>
/// <param name="Name">API 工作流名称。</param>
/// <param name="Status">草稿、已发布或已停用。</param>
/// <param name="Version">最近一次发布版本，草稿为空。</param>
/// <param name="UpdatedAt">最近一次草稿更新时间。</param>
public sealed record ManagedWorkflowApi(
    Guid WorkflowId,
    string Name,
    string Status,
    int? Version,
    DateTimeOffset UpdatedAt);

/// <param name="WorkflowId">关联工作流的唯一标识。</param>
/// <param name="Name">实体或配置项的名称。</param>
/// <param name="NodeCount">节点图包含的节点数量。</param>
/// <param name="CreatedAt">记录创建时间，沿用当前 UTC 与序列化约定。</param>
/// <param name="UpdatedAt">记录最近一次修改的 UTC 时间。</param>
public sealed record WorkflowSummary(
    Guid WorkflowId,
    string Name,
    int NodeCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
