using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Agents.AI.Workflows;

namespace CreativeFlow.Infrastructure.Ai.Maf;

/// <summary>由 MAF 条件边推进稳定的串行 Executor 链；没有第二套队列、重试或持久化检查点。</summary>
internal static class MafSequence
{
    /// <summary>每次创建独立 Executor 状态，回调 false 后不再传递令牌；原异常类型交还业务层。</summary>
    public static async Task RunAsync(IReadOnlyList<string> identities,
        Func<int, CancellationToken, Task<bool>> execute, CancellationToken ct)
    {
        if (identities.Count == 0) return;
        var state = new ExecutionState(execute);
        var steps = identities.Select((id, index) => new StepExecutor(
            $"step_{index}_{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..16]}", index, state)).ToArray();
        var builder = new WorkflowBuilder(steps[0]);
        for (var index = 1; index < steps.Length; index++)
            builder.AddEdge<bool>(steps[index - 1], steps[index], next => next);
        var workflow = builder.WithOutputFrom(steps[^1]).Build();
        try
        {
            // MAF 负责超步调度；业务模型调用/节点副作用只可能在对应 Executor 内发生。
            await using var run = await InProcessExecution.RunAsync(workflow, true, cancellationToken: ct);
            state.Failure?.Throw();
            foreach (var error in run.NewEvents.OfType<WorkflowErrorEvent>())
                throw new InvalidOperationException("The execution scheduler failed.", error.Exception);
        }
        catch when (state.Failure is not null)
        {
            state.Failure.Throw();
            throw;
        }
    }

    /// <summary>单次引擎运行内的顺序与异常记录，不跨任务共享。</summary>
    private sealed class ExecutionState(Func<int, CancellationToken, Task<bool>> execute)
    {
        /// <summary>下一次被允许执行的原计划索引。</summary>
        private int next;
        /// <summary>原业务异常，避免被 SDK 错误事件包装改变对外映射。</summary>
        public ExceptionDispatchInfo? Failure { get; private set; }

        /// <summary>防御性限制每个节点/轮次执行一次，保留业务原异常及取消语义。</summary>
        public async Task<bool> ExecuteAsync(int index, CancellationToken ct)
        {
            try
            {
                if (index != next++) throw new InvalidOperationException("Execution order changed.");
                return await execute(index, ct);
            }
            catch (Exception exception)
            {
                Failure = ExceptionDispatchInfo.Capture(exception);
                throw;
            }
        }
    }

    /// <summary>一个确定性步骤，只传递继续令牌，不把业务图、密钥或素材放入框架事件。</summary>
    private sealed class StepExecutor(string id, int index, ExecutionState state) : Executor<bool, bool>(id)
    {
        /// <summary>收到继续令牌时执行对应的唯一业务步骤，并将回调结果作为后续条件边的继续标志。</summary>
        public override async ValueTask<bool> HandleAsync(bool message, IWorkflowContext context, CancellationToken cancellationToken = default) =>
            message && await state.ExecuteAsync(index, cancellationToken);
    }
}
