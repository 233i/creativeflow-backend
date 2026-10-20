using CreativeFlow.Application.Abstractions.Ai;

namespace CreativeFlow.Infrastructure.Ai.Maf;

/// <summary>原拓扑计划映射为 MAF 串行 Executor，不自行规划、并行、恢复或再次提交模型。</summary>
internal sealed class MafWorkflowRuntime : IWorkflowRuntime
{
    /// <summary>将原拓扑计划映射为 MAF 串行步骤，每步等待原节点业务执行，不另建检查点或重试。</summary>
    public Task RunAsync(IReadOnlyList<string> nodeIds, Func<int, CancellationToken, Task> executeNode, CancellationToken ct) =>
        MafSequence.RunAsync(nodeIds, async (index, token) =>
        {
            await executeNode(index, token);
            return true;
        }, ct);
}
