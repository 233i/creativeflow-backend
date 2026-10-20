using System.Text.Json.Nodes;
using CreativeFlow.Application.Features.Knowledge;
using CreativeFlow.Application.Features.Workflow;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Infrastructure.Ai.Workflows;

/// <summary>为一次运行选择原有资源复验策略，不参与节点业务或改变模型网络许可。</summary>
internal sealed class WorkflowExecutionPolicy(
    WorkflowV2Network? network = null,
    KnowledgeWorkflowService? knowledge = null,
    HttpRequestService? http = null,
    WorkflowV2PublicationService? publication = null)
{
    /// <summary>媒体运行不需要通用配置；通用运行仍要求已注册并启用网络策略。</summary>
    internal bool GeneralEnabled => (network ?? throw new InvalidOperationException("Workflow network policy is not registered.")).Enabled;

    /// <summary>根据图与调试快照选择必需的资源服务，保持缺失服务时明确失败。</summary>
    internal WorkflowRunAuthorization Bind(
        WorkflowRun run, WorkflowPlan plan, JsonObject snapshot, bool isGeneral, string? debugNodeId)
    {
        var needsKnowledge = isGeneral && (plan.Nodes.Any(node => KnowledgeWorkflowValues.IsKnowledge(node.Type))
            || debugNodeId is not null && snapshot["knowledge"] is not null);
        var needsHttp = isGeneral && plan.Nodes.Any(node => node.Type == "httpRequest");
        var needsPublication = isGeneral && run.TriggerType == WorkflowRunTriggerType.Api;
        return new WorkflowRunAuthorization(run, snapshot,
            needsKnowledge ? knowledge ?? throw new InvalidOperationException("Knowledge workflow service is not registered.") : null,
            needsHttp ? http ?? throw new InvalidOperationException("Workflow HTTP service is not registered.") : null,
            needsPublication ? publication ?? throw new InvalidOperationException("Workflow publication service is not registered.") : null);
    }
}

/// <summary>保存一次运行所需的资源复验规则；不会缓存授权结论。</summary>
internal sealed class WorkflowRunAuthorization(
    WorkflowRun run, JsonObject snapshot, KnowledgeWorkflowService? knowledge,
    HttpRequestService? http, WorkflowV2PublicationService? publication)
{
    /// <summary>每个节点前后重新检查发布、知识和 HTTP 授权，保留原检查顺序。</summary>
    internal async Task ValidateNodeAsync(CancellationToken ct)
    {
        if (publication is not null) await publication.ValidateRunAsync(run, ct);
        if (knowledge is not null) await knowledge.ValidateAsync(snapshot, run.DeviceId.Value, ct);
        if (http is not null) await http.ValidateAsync(snapshot, run.DeviceId.Value, ct);
    }

    /// <summary>写整图成功前保留原发布与知识复验；HTTP 已在节点结束时复验。</summary>
    internal async Task ValidateCompletionAsync(CancellationToken ct)
    {
        if (publication is not null) await publication.ValidateRunAsync(run, ct);
        if (knowledge is not null) await knowledge.ValidateAsync(snapshot, run.DeviceId.Value, ct);
    }
}
