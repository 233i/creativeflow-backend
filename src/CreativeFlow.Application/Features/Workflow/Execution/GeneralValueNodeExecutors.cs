using System.Text.Json.Nodes;

namespace CreativeFlow.Application.Features.Workflow;

/// <summary>开始、结束及结果汇合节点只交付经过原绑定规则选择的字段。</summary>
public sealed class BoundaryNodeExecutor : GeneralWorkflowNodeExecutor
{
    /// <summary>不修改字段内容或顺序，不调用模型和外部接口。</summary>
    protected override Task<JsonObject> ExecuteBoundAsync(WorkflowNodeExecutionContext context, JsonObject bound, CancellationToken ct) =>
        Task.FromResult(bound);
}

/// <summary>按原条件优先级选择唯一分支，分支跳过仍由编排层控制。</summary>
public sealed class ConditionNodeExecutor : GeneralWorkflowNodeExecutor
{
    /// <summary>使用已有条件求值器生成路由结果。</summary>
    protected override Task<JsonObject> ExecuteBoundAsync(WorkflowNodeExecutionContext context, JsonObject bound, CancellationToken ct) =>
        Task.FromResult(WorkflowBranchValues.Evaluate(context.Node.Data, bound));
}

/// <summary>执行已有变量转换表达式，不访问模型或外部网络。</summary>
public sealed class TransformNodeExecutor : GeneralWorkflowNodeExecutor
{
    /// <summary>保留变量处理的类型检查、默认值和错误语义。</summary>
    protected override Task<JsonObject> ExecuteBoundAsync(WorkflowNodeExecutionContext context, JsonObject bound, CancellationToken ct) =>
        Task.FromResult(WorkflowTransform.Execute(context.Node.Data, bound));
}
