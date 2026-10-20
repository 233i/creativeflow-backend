namespace CreativeFlow.Application.Abstractions.Ai;

/// <summary>只负责 Agent 的轮次推进；业务回调继续拥有历史、审批、工具、事件及终态。</summary>
public interface IAgentRuntime
{
    /// <summary>按串行轮次执行；回调返回 false 时停止，不重试、不改模型、不重放工具。</summary>
    Task RunAsync(int maximumIterations, IAgentModelDispatcher models,
        Func<int, IAgentModelDispatcher, CancellationToken, Task<bool>> executeIteration, CancellationToken ct);
}

/// <summary>只负责原拓扑计划的串行调度；每个索引最多执行一次，异常交还原业务收尾。</summary>
public interface IWorkflowRuntime
{
    /// <summary>按给定节点顺序调用原业务组件，不并行、不新增节点、不自行保存业务检查点。</summary>
    Task RunAsync(IReadOnlyList<string> nodeIds, Func<int, CancellationToken, Task> executeNode, CancellationToken ct);
}
