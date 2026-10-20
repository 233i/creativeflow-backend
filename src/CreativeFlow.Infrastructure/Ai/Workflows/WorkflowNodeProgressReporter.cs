using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Features.Workflow;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Infrastructure.Ai.Workflows;

/// <summary>先持久化节点进度，再通过原工作流事件协议通知客户端；不持有可变的执行上下文。</summary>
internal sealed class WorkflowNodeProgressReporter(IWorkflowRunRepository runs, WorkflowProgressPublisher publisher)
    : IWorkflowNodeProgress
{
    /// <summary>仅修改当前节点快照中的进度，保留取消检查、原始状态字段及推送失败隔离策略。</summary>
    public async Task ReportAsync(WorkflowRun run, WorkflowNodeRun nodeRun, double? progress, CancellationToken ct)
    {
        if (!await runs.UpdateNodeAsync(nodeRun with { Progress = progress }, ct))
            throw new WorkflowCancelledException();
        await publisher.PublishAsync(run, "running", nodeRun.NodeId, "running", progress, null, null, ct);
    }
}
