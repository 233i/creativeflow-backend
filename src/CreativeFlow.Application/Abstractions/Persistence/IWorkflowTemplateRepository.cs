using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Application.Abstractions.Persistence;

public interface IWorkflowTemplateRepository
{
    /// <summary>列出工作流模板，保留现有筛选、排序与访问范围。</summary>
    Task<IReadOnlyList<WorkflowTemplate>> ListAsync(
        string? search,
        string? category,
        WorkflowTemplateStatus? status,
        CancellationToken ct);

    /// <summary>按当前查找键读取已有配置或目录项。</summary>
    Task<WorkflowTemplate?> FindAsync(WorkflowTemplateId id, CancellationToken ct);

    /// <summary>按原有存储规则写入工作流模板。</summary>
    Task InsertAsync(WorkflowTemplate template, CancellationToken ct);

    /// <summary>更新工作流模板，保留原有访问范围和并发结果。</summary>
    Task<bool> UpdateAsync(WorkflowTemplate template, CancellationToken ct);

    /// <summary>在模板实例化后累加使用次数。</summary>
    Task IncrementUseCountAsync(WorkflowTemplateId id, CancellationToken ct);
}
