using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Features.Knowledge;

namespace CreativeFlow.Application.Features.Workflow;

/// <summary>执行固定知识版本的检索，沿用来源、正文、维度及权限校验。</summary>
public sealed class KnowledgeRetrievalNodeExecutor(KnowledgeWorkflowService knowledge, IWorkflowNodeProgress progress) : GeneralWorkflowNodeExecutor
{
    /// <summary>仅交付由知识服务构造的证据字段，不把普通输入当作检索结果。</summary>
    protected override Task<JsonObject> ExecuteBoundAsync(WorkflowNodeExecutionContext context, JsonObject bound, CancellationToken ct) =>
        knowledge.RetrieveAsync(context.Node, context.Snapshot, WorkflowV2Values.Text(bound["question"]), ct,
            bound["originalQuestion"] is null ? null : WorkflowV2Values.Text(bound["originalQuestion"]),
            (value, token) => progress.ReportAsync(context.Run, context.NodeRun, value, token));
}

/// <summary>执行带引用校验的知识问答，不改变原始问题与检索词的独立语义。</summary>
public sealed class KnowledgeAnswerNodeExecutor(KnowledgeWorkflowService knowledge, IWorkflowV2Models models)
    : GeneralWorkflowNodeExecutor
{
    /// <summary>调用原问答服务进行证据校验和一次模型回答，不引入自动重试。</summary>
    protected override Task<JsonObject> ExecuteBoundAsync(WorkflowNodeExecutionContext context, JsonObject bound, CancellationToken ct) =>
        knowledge.AnswerAsync(context.Node, context.Snapshot, bound, models, ct);
}
