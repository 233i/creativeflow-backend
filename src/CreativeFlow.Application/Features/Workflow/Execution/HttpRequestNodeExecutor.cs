using System.Text.Json.Nodes;

namespace CreativeFlow.Application.Features.Workflow;

/// <summary>把已绑定 HTTP 节点交给现有凭据和网络策略服务，不在执行器扩展授权。</summary>
public sealed class HttpRequestNodeExecutor(HttpRequestService requests) : GeneralWorkflowNodeExecutor
{
    /// <summary>使用本次运行固定的资源快照，保留请求及响应限制。</summary>
    protected override Task<JsonObject> ExecuteBoundAsync(WorkflowNodeExecutionContext context, JsonObject bound, CancellationToken ct) =>
        requests.ExecuteAsync(context.Node, context.Snapshot, bound, ct);
}
