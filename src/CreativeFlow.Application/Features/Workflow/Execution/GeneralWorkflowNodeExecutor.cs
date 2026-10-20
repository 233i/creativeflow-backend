using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Ai;

namespace CreativeFlow.Application.Features.Workflow;

/// <summary>通用节点共享真实连线与调试输入绑定，派生执行器只处理各自业务。</summary>
public abstract class GeneralWorkflowNodeExecutor : IWorkflowNodeExecutor
{
    /// <summary>按原字段契约绑定一次输入，再包装为原通用节点输出格式。</summary>
    public async Task<WorkflowNodeValue> ExecuteAsync(WorkflowNodeExecutionContext context, CancellationToken ct)
    {
        var node = context.Node;
        var input = context.DebugInputs ?? (node.Type == "start"
            ? context.Snapshot["inputs"] as JsonObject ?? throw new WorkflowV2Exception("运行输入快照缺失。")
            : WorkflowBranchFlow.BindInputs(node,
                context.Values.ToDictionary(pair => pair.Key, pair => pair.Value.Fields!, StringComparer.Ordinal),
                context.Skipped));
        var error = WorkflowV2Values.Bind((JsonArray)node.Data["fields"]!, input, out var bound);
        if (error is not null) throw new WorkflowV2Exception(error);
        return new WorkflowNodeValue([], [], []) { Fields = await ExecuteBoundAsync(context, bound, ct) };
    }

    /// <summary>消费已校验的绑定字段，返回节点声明的字段结果，不调度其他节点。</summary>
    protected abstract Task<JsonObject> ExecuteBoundAsync(
        WorkflowNodeExecutionContext context, JsonObject bound, CancellationToken ct);
}
