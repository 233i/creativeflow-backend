namespace CreativeFlow.Application.Features.Workflow;

/// <summary>由装配层提供的延迟执行器注册，恢复成功输出时不构造无关模型服务。</summary>
/// <param name="Family">注册所属的节点语义。</param>
/// <param name="NodeType">图协议中的稳定节点类型名称。</param>
/// <param name="Resolve">在当前运行作用域内解析执行器，不创建新的作用域。</param>
public sealed record WorkflowNodeExecutorRegistration(
    WorkflowNodeFamily Family, string NodeType, Func<IWorkflowNodeExecutor> Resolve);

/// <summary>按图语义和节点类型分派业务执行，独立于 MAF 调度与持久化。</summary>
public sealed class WorkflowNodeExecutorRegistry
{
    /// <summary>每个键仅允许一个执行器工厂，防止注册顺序改变实际行为。</summary>
    private readonly Dictionary<(WorkflowNodeFamily Family, string NodeType), Func<IWorkflowNodeExecutor>> executors = [];

    /// <summary>验证全部注册的唯一性；此时不实例化执行器或访问外部服务。</summary>
    public WorkflowNodeExecutorRegistry(IEnumerable<WorkflowNodeExecutorRegistration> registrations)
    {
        foreach (var registration in registrations)
        {
            if (!executors.TryAdd((registration.Family, registration.NodeType), registration.Resolve))
                throw new InvalidOperationException($"Duplicate workflow node executor: {registration.Family}/{registration.NodeType}.");
        }
    }

    /// <summary>只解析实际执行的节点；未知类型明确失败，不回退到另一套运行引擎。</summary>
    public Task<WorkflowNodeValue> ExecuteAsync(WorkflowNodeExecutionContext context, CancellationToken ct)
    {
        if (!executors.TryGetValue((context.Family, context.Node.Type), out var resolve))
            throw new WorkflowExecutionException($"Unsupported workflow node '{context.Node.Type}'.");
        return resolve().ExecuteAsync(context, ct);
    }
}
