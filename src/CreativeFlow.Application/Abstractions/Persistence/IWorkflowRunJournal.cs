using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Application.Abstractions.Persistence;

/// <summary>按提交顺序读取公开运行事件；状态和正文与业务写入处于同一事务。</summary>
public interface IWorkflowRunJournal
{
    /// <summary>读取游标后的有界事件批次，不触发执行或重试。</summary>
    Task<IReadOnlyList<WorkflowRunJournalEntry>> ReadAsync(WorkflowRunId runId, long after, CancellationToken ct);
}

/// <summary>协议无关的持久化公开事件，不含提示词、凭据或内部节点结果。</summary>
/// <param name="Sequence">同运行内按事务提交顺序递增的序号。</param>
/// <param name="Kind">运行、节点、公开文本或最终完成标记的事件分类。</param>
/// <param name="Data">仅包含公开状态或公开输出增量的 JSON 对象。</param>
/// <param name="CreatedAt">数据库记录事件的 UTC 时间。</param>
public sealed record WorkflowRunJournalEntry(long Sequence, string Kind, string Data, DateTimeOffset CreatedAt);
