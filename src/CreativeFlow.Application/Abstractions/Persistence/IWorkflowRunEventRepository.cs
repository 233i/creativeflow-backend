using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Application.Abstractions.Persistence;

/// <summary>外部运行的公开文本增量；进度和最终结果始终读取原运行记录。</summary>
public interface IWorkflowRunEventRepository
{
    /// <summary>仅对活动外部运行追加事件；与终态更新互斥，序号按提交顺序递增。</summary>
    Task AppendAsync(WorkflowRunId runId, string data, CancellationToken ct);
    /// <summary>有界读取游标之后的已提交事件，读取不会启动或重试任务。</summary>
    Task<IReadOnlyList<WorkflowRunTextEvent>> ReadAsync(WorkflowRunId runId, long after, CancellationToken ct);
    /// <summary>校验重连游标，防止错误游标静默跳过后续文本。</summary>
    Task<long> LastSequenceAsync(WorkflowRunId runId, CancellationToken ct);
}

/// <summary>公开增量的持久化序号与 JSON 负载。</summary>
public sealed record WorkflowRunTextEvent(long Sequence, string Data);
