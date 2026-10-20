using CreativeFlow.Application.Features.Workflow;
using static CreativeFlow.Infrastructure.Ai.Workflows.WorkflowMediaInputs;

namespace CreativeFlow.Infrastructure.Ai.Workflows;

/// <summary>读取媒体文本输入，保持原来的空值与去空白规则。</summary>
internal sealed class TextInputNodeExecutor : IWorkflowNodeExecutor
{
    /// <summary>文本节点不访问模型或持久化，仅构造文本输出。</summary>
    public Task<WorkflowNodeValue> ExecuteAsync(WorkflowNodeExecutionContext context, CancellationToken ct) =>
        Task.FromResult(WorkflowNodeValue.FromText(ReadString(context.Node.Data, "text")));
}

/// <summary>汇总媒体结果节点的真实上游连线，不重排、不去重已有输出。</summary>
internal sealed class MediaOutputNodeExecutor : IWorkflowNodeExecutor
{
    /// <summary>图片、视频与混合结果节点共享原来的端口绑定规则。</summary>
    public Task<WorkflowNodeValue> ExecuteAsync(WorkflowNodeExecutionContext context, CancellationToken ct) =>
        Task.FromResult(GatherInputs(context.Node, context.Values));
}
