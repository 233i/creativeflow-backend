using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Application.Features.Workflow;

/// <summary>保存并通知单节点进度，不调度节点、不提交模型任务，也不改变运行终态。</summary>
public interface IWorkflowNodeProgress
{
    /// <summary>按当前运行和节点快照更新进度；持久化拒绝更新时中止执行，不发送成功进度通知。</summary>
    Task ReportAsync(WorkflowRun run, WorkflowNodeRun nodeRun, double? progress, CancellationToken ct);
}
