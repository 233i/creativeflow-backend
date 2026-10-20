using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using System.Runtime.CompilerServices;

namespace CreativeFlow.MafProbe;

/// <summary>仅供离线验证 MAF 控制点；生产项目不得引用此探针。</summary>
public static class RuntimeProbe
{
    /// <summary>关闭框架默认装饰器，验证一次模型请求不会隐式执行工具或重试。</summary>
    public static ChatClientAgent CreateSingleTurnAgent(IChatClient client, ChatOptions options) =>
        new(client, new ChatClientAgentOptions
        {
            Id = "creativeflow-maf-probe",
            Name = "creativeflow-maf-probe",
            UseProvidedChatClientAsIs = true,
            ChatOptions = options,
        });

    /// <summary>显式限制框架工具循环，供对照其计数、顺序和异常行为。</summary>
    public static ChatClientAgent CreateToolLoopAgent(IChatClient client, ChatOptions options, int maximumIterations)
    {
        var invoking = new FunctionInvokingChatClient(client)
        {
            AllowConcurrentInvocation = false,
            MaximumIterationsPerRequest = maximumIterations,
            MaximumConsecutiveErrorsPerRequest = 1,
        };
        return CreateSingleTurnAgent(invoking, options);
    }

    /// <summary>验证独立的出站预算能拦截框架额外请求；业务终态映射仍须另行验收。</summary>
    public static ChatClientAgent CreateBoundedToolLoopAgent(IChatClient client, ChatOptions options, int maximumCalls) =>
        CreateToolLoopAgent(new BudgetedClient(client, maximumCalls), options, maximumCalls);

    /// <summary>探测 19 次工具执行加最后一次模型请求的计数映射；并不代替旧业务终态和事件映射。</summary>
    public static ChatClientAgent CreateLegacyCountAgent(IChatClient client, ChatOptions options) =>
        CreateToolLoopAgent(new BudgetedClient(client, 20), options, 19);

    /// <summary>以稳定 ID 的串行 Executor 验证顺序编排；没有模型、数据库或真实业务副作用。</summary>
    public static async Task<IReadOnlyList<string>> RunSequenceAsync(
        IReadOnlyList<string> nodeIds, IList<string> trace, CancellationToken ct)
    {
        if (nodeIds.Count == 0) throw new ArgumentException("At least one node is required.", nameof(nodeIds));
        var nodes = nodeIds.Select(id => new RecordingExecutor(id, trace)).ToArray();
        var builder = new WorkflowBuilder(nodes[0]);
        for (var index = 1; index < nodes.Length; index++)
        {
            builder.AddEdge(nodes[index - 1], nodes[index]);
        }
        var workflow = builder.WithOutputFrom(nodes[^1]).Build();
        await using var run = await InProcessExecution.RunAsync(workflow, "input", cancellationToken: ct);
        return run.NewEvents.OfType<WorkflowOutputEvent>().Select(item => (string)item.Data!).ToArray();
    }

    /// <summary>记录一个确定性节点的执行顺序并向下游原样传递字符串。</summary>
    private sealed class RecordingExecutor(string nodeId, IList<string> trace) : Executor<string, string>(nodeId)
    {
        /// <summary>观察取消并记录当前 Executor 标识，向输出追加该标识以验证 MAF 的串行调度顺序。</summary>
        public override ValueTask<string> HandleAsync(string message, IWorkflowContext context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            trace.Add(Id);
            return ValueTask.FromResult(message + "/" + Id);
        }
    }

    /// <summary>仅供探针验证的单运行调用预算，不会创建第二个客户端或切换模型。</summary>
    private sealed class BudgetedClient(IChatClient innerClient, int maximumCalls) : DelegatingChatClient(innerClient)
    {
        /// <summary>本次运行已获准的模型请求次数。</summary>
        private int calls;

        /// <summary>在转发 SDK 流式请求前原子检查模型调用预算，超限时抛出探针异常且不触及下游。</summary>
        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref calls) > maximumCalls) throw new ModelCallBudgetExceededException();
            await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
                yield return update;
        }

        /// <summary>在转发 SDK 非流式请求前使用同一计数器检查预算，保证两种请求路径共享上限。</summary>
        public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref calls) > maximumCalls) throw new ModelCallBudgetExceededException();
            return base.GetResponseAsync(messages, options, cancellationToken);
        }
    }
}

/// <summary>框架尝试超出旧运行器模型调用硬上限；禁止把此异常当作重试条件。</summary>
public sealed class ModelCallBudgetExceededException : Exception
{
    /// <summary>构造不含模型输入、密钥或内部请求内容的探针预算异常。</summary>
    public ModelCallBudgetExceededException() : base("The model call budget is exhausted.") { }
}
