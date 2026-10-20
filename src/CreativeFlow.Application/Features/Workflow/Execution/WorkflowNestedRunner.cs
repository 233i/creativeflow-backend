using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Application.Features.Workflow;

/// <summary>在一个循环项内执行已校验的子图；不创建运行记录、不创建队列，也不绕过节点执行器。</summary>
public sealed class WorkflowNestedRunner(WorkflowNodeExecutorRegistry executors)
{
    public async Task<JsonObject> ExecuteAsync(
        WorkflowNodeExecutionContext parent,
        WorkflowPlan plan,
        JsonObject input,
        CancellationToken ct)
    {
        if (parent.NestedDepth >= WorkflowLoopValues.MaxBodyDepth)
            throw new WorkflowV2Exception($"子流程嵌套深度不能超过 {WorkflowLoopValues.MaxBodyDepth}。 ");

        var values = new Dictionary<string, WorkflowNodeValue>(StringComparer.Ordinal);
        var skipped = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in plan.Nodes)
        {
            ct.ThrowIfCancellationRequested();
            if (node.Type != "start" && WorkflowBranchFlow.ShouldSkip(
                    node,
                    values.ToDictionary(pair => pair.Key, pair => pair.Value.Fields!, StringComparer.Ordinal),
                    skipped))
            {
                skipped.Add(node.Id);
                continue;
            }

            var nodeRun = new WorkflowNodeRun(
                parent.Run.Id,
                $"{parent.Node.Id}:{node.Id}",
                node.Type,
                WorkflowNodeRunStatus.Running,
                false,
                null,
                null,
                null,
                DateTimeOffset.UtcNow,
                null);
            var context = new WorkflowNodeExecutionContext(
                WorkflowNodeFamily.General,
                parent.Run,
                node,
                values,
                parent.Snapshot,
                skipped,
                node.Type == "start" ? input : null,
                nodeRun)
            { NestedDepth = parent.NestedDepth + 1 };
            var value = await executors.ExecuteAsync(context, ct);
            values[node.Id] = value;
        }

        var end = plan.Nodes.SingleOrDefault(node => node.Type == "end");
        if (end is null || !values.TryGetValue(end.Id, out var result) || result.Fields is null)
            throw new WorkflowV2Exception("子流程未产生结束节点输出。 ");
        return result.Fields.DeepClone().AsObject();
    }
}
