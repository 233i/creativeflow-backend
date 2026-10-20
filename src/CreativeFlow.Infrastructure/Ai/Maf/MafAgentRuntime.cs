using CreativeFlow.Application.Abstractions.Ai;

namespace CreativeFlow.Infrastructure.Ai.Maf;

/// <summary>MAF 接管 Agent 轮次条件图及 ChatClientAgent 流式运行，业务审批/工具仍走原端口。</summary>
internal sealed class MafAgentRuntime : IAgentRuntime
{
    /// <summary>为原 Agent 轮次建立 MAF 条件执行链，并通过受硬调用预算限制的模型适配器逐轮执行。</summary>
    public Task RunAsync(int maximumIterations, IAgentModelDispatcher models,
        Func<int, IAgentModelDispatcher, CancellationToken, Task<bool>> executeIteration, CancellationToken ct)
    {
        var adapter = new MafModelDispatcher(models, maximumIterations);
        return MafSequence.RunAsync(Enumerable.Range(0, maximumIterations).Select(index => $"agent-turn-{index}").ToArray(),
            (index, token) => executeIteration(index, adapter, token), ct);
    }
}
