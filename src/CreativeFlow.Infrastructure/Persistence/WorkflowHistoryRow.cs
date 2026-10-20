namespace CreativeFlow.Infrastructure.Persistence;

/// <summary>交互运行历史不读取图和正文，减少列表内存与敏感数据暴露。</summary>
/// <param name="RunId">运行标识。</param>
/// <param name="Status">持久状态。</param>
/// <param name="ResumedFromRunId">继续或局部重跑的来源运行。</param>
/// <param name="CreatedAt">提交时间。</param>
/// <param name="UpdatedAt">最后状态更新时间。</param>
/// <param name="StartedAt">开始执行时间。</param>
/// <param name="FinishedAt">结束时间。</param>
/// <param name="DebugNodeId">独立调试的目标节点，普通整图运行为空。</param>
internal sealed record WorkflowHistoryRow(Guid RunId, string Status, Guid? ResumedFromRunId, DateTime CreatedAt,
    DateTime UpdatedAt, DateTime? StartedAt, DateTime? FinishedAt, string? DebugNodeId);
