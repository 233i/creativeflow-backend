using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Features.Knowledge;
using CreativeFlow.Application.Features.Workflow;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;

namespace CreativeFlow.UnitTests.Support;

/// <summary>直接构造生产通用节点执行器；只传入当前测试需要的模型和资源替身，不搭建服务容器。</summary>
internal static class WorkflowNodeTestHost
{
    /// <summary>模拟正常节点的连线绑定，不运行调度器或隐式补跑上游。</summary>
    internal static Task<JsonObject> ExecuteAsync(WorkflowPlanNode node, JsonObject snapshot,
        IReadOnlyDictionary<string, JsonObject> values, IWorkflowV2Models models, CancellationToken ct,
        KnowledgeWorkflowService? knowledge = null, IReadOnlySet<string>? skipped = null,
        IWorkflowV2AssetResolver? assetResolver = null, DeviceId? deviceId = null, HttpRequestService? http = null) =>
        ExecuteCoreAsync(node, snapshot, values, null, models, ct, knowledge, skipped, assetResolver, deviceId, http);

    /// <summary>模拟单节点调试输入，仍调用同一个生产节点执行器。</summary>
    internal static Task<JsonObject> ExecuteBoundAsync(WorkflowPlanNode node, JsonObject snapshot,
        JsonObject input, IWorkflowV2Models models, CancellationToken ct,
        KnowledgeWorkflowService? knowledge = null, IWorkflowV2AssetResolver? assetResolver = null,
        DeviceId? deviceId = null, HttpRequestService? http = null) =>
        ExecuteCoreAsync(node, snapshot, new Dictionary<string, JsonObject>(), input, models, ct,
            knowledge, null, assetResolver, deviceId, http);

    /// <summary>通过显式构造依赖执行真实节点；缺少替身时明确失败，不访问开发或生产服务。</summary>
    private static async Task<JsonObject> ExecuteCoreAsync(WorkflowPlanNode node, JsonObject snapshot,
        IReadOnlyDictionary<string, JsonObject> values, JsonObject? debugInputs,
        IWorkflowV2Models models, CancellationToken ct, KnowledgeWorkflowService? knowledge,
        IReadOnlySet<string>? skipped, IWorkflowV2AssetResolver? assetResolver, DeviceId? deviceId, HttpRequestService? http)
    {
        IWorkflowNodeExecutor executor = node.Type switch
        {
            "start" or "end" or "merge" => new BoundaryNodeExecutor(),
            "condition" => new ConditionNodeExecutor(),
            "transform" => new TransformNodeExecutor(),
            "llm" or "parameterExtract" or "intentClassify" => new ModelNodeExecutor(models, assetResolver),
            "knowledgeRetrieve" => new KnowledgeRetrievalNodeExecutor(knowledge ?? throw new ArgumentNullException(nameof(knowledge)), new TestProgress()),
            "knowledgeAnswer" => new KnowledgeAnswerNodeExecutor(knowledge ?? throw new ArgumentNullException(nameof(knowledge)), models),
            "httpRequest" => new HttpRequestNodeExecutor(http ?? throw new ArgumentNullException(nameof(http))),
            _ => throw new InvalidOperationException($"No test executor for node '{node.Type}'."),
        };
        var run = Run(deviceId);
        var context = new WorkflowNodeExecutionContext(WorkflowNodeFamily.General, run, node,
            values.ToDictionary(pair => pair.Key, pair => new WorkflowNodeValue([], [], []) { Fields = pair.Value }),
            snapshot, skipped ?? new HashSet<string>(), debugInputs, NodeRun(run, node));
        return (await executor.ExecuteAsync(context, ct)).Fields!;
    }

    /// <summary>构造与当前运行、节点身份一致的执行开始快照，不写入数据库。</summary>
    internal static WorkflowNodeRun NodeRun(WorkflowRun run, WorkflowPlanNode node) =>
        new(run.Id, node.Id, node.Type, WorkflowNodeRunStatus.Running, false, null, null, null, DateTimeOffset.UnixEpoch, null);

    private sealed class TestProgress : IWorkflowNodeProgress
    {
        /// <summary>测试节点进度回调，不生成额外执行或外部模型请求。</summary>
        public Task ReportAsync(WorkflowRun run, WorkflowNodeRun nodeRun, double? progress, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>创建不落库的合成身份，供独立执行器测试构造上下文。</summary>
    internal static WorkflowRun Run(DeviceId? deviceId = null) => new(
        WorkflowRunId.New(), WorkflowId.New(), deviceId ?? new DeviceId(Guid.NewGuid()), "{}", null,
        WorkflowRunStatus.Running, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null, null);
}
