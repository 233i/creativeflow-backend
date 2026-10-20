namespace CreativeFlow.Application.Features.Workflow;

/// <summary>
/// 查找不可变工作流快照中受依赖关系影响的部分。
/// </summary>
public static class WorkflowPartialRerunPlanner
{
    /// <summary>
    /// 返回选中节点以及所有直接或间接使用其输出的节点。
    /// 刻意排除无关分支上的节点，使部分重新运行可以复用它们已完成的输出。
    /// </summary>
    public static IReadOnlySet<string>? FindRerunNodeIds(
        WorkflowPlan plan,
        string fromNodeId)
    {
        // 从指定节点沿出边收集依赖节点，集合去重保证汇合路径不会重复处理同一节点。
        if (string.IsNullOrWhiteSpace(fromNodeId)
            || !plan.Nodes.Any(node => string.Equals(
                node.Id,
                fromNodeId,
                StringComparison.Ordinal)))
        {
            return null;
        }

        var outgoing = plan.Nodes.ToDictionary(
            node => node.Id,
            _ => new List<string>(),
            StringComparer.Ordinal);

        foreach (var node in plan.Nodes)
        {
            foreach (var input in node.Inputs)
            {
                outgoing[input.Source].Add(node.Id);
            }
        }

        var affected = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>();
        pending.Enqueue(fromNodeId);

        while (pending.TryDequeue(out var nodeId))
        {
            if (!affected.Add(nodeId)) continue;
            foreach (var target in outgoing[nodeId]) pending.Enqueue(target);
        }

        return affected;
    }
}
