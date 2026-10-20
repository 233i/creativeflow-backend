using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Ai;
using static CreativeFlow.Application.Features.Workflow.WorkflowV2Values;

namespace CreativeFlow.Application.Features.Workflow;

/// <summary>MAF 节点回调共用的分支激活与汇合规则；不持有队列、重试或另一套调度循环。</summary>
public static class WorkflowBranchFlow
{
    /// <summary>控制边不匹配或普通依赖被跳过时不执行；汇合只要求有活跃候选，不等待被跳过的结果。</summary>
    public static bool ShouldSkip(WorkflowPlanNode node, IReadOnlyDictionary<string, JsonObject> values, IReadOnlySet<string> skipped)
    {
        foreach (var edge in node.Inputs.Where(edge => edge.IsControl))
        {
            if (skipped.Contains(edge.Source)) return true;
            if (!values.TryGetValue(edge.Source, out var decision)) throw new WorkflowV2Exception("分支决定尚未就绪。");
            if (Text(decision["route"]) != edge.SourceHandle) return true;
        }
        var data = node.Inputs.Where(edge => !edge.IsControl).ToArray();
        return node.Type == "merge" ? data.Length > 0 && data.All(edge => skipped.Contains(edge.Source)) : data.Any(edge => skipped.Contains(edge.Source));
    }

    /// <summary>汇合按目标字段选择唯一活跃来源；缺少、多个活跃值或越过未执行节点都不以先到者获胜。</summary>
    public static JsonObject BindInputs(WorkflowPlanNode node, IReadOnlyDictionary<string, JsonObject> values, IReadOnlySet<string> skipped)
    {
        var input = new JsonObject();
        foreach (var edge in node.Inputs.Where(edge => !edge.IsControl))
        {
            if (node.Type == "merge" && skipped.Contains(edge.Source)) continue;
            if (!values.TryGetValue(edge.Source, out var source)) throw new WorkflowV2Exception("依赖节点输出缺失；分支结果必须经过汇合节点。");
            if (!source.TryGetPropertyValue(edge.SourceHandle, out var value)) continue;
            if (input.ContainsKey(edge.TargetHandle)) throw new WorkflowV2Exception($"汇合字段 '{edge.TargetHandle}' 同时有多个活跃来源。");
            input[edge.TargetHandle] = value?.DeepClone();
        }
        return input;
    }

    /// <summary>传播节点必需的路由约束，拒绝矛盾依赖、非互斥的多来源及有空洞的必填汇合。</summary>
    public static IReadOnlyList<string> Validate(IReadOnlyList<WorkflowPlanNode> ordered)
    {
        var errors = new List<string>();
        var guards = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var branches = ordered.Where(node => WorkflowBranchValues.IsBranch(node.Type)).ToDictionary(node => node.Id, node => WorkflowBranchValues.Branches(node.Data));
        foreach (var node in ordered)
        {
            var data = node.Inputs.Where(edge => !edge.IsControl).ToArray();
            var guard = new Dictionary<string, string>(StringComparer.Ordinal);
            // 普通节点同时需要所有数据依赖；汇合消去互斥条件，只保留各来源共同的外层路由。
            foreach (var edge in data)
                foreach (var pair in guards[edge.Source])
                    if (node.Type != "merge" || data.All(other => guards[other.Source].GetValueOrDefault(pair.Key) == pair.Value)) Add(guard, pair, node.Id, errors);
            foreach (var edge in node.Inputs.Where(edge => edge.IsControl))
            {
                foreach (var pair in guards[edge.Source]) Add(guard, pair, node.Id, errors);
                Add(guard, new(edge.Source, edge.SourceHandle), node.Id, errors);
            }
            guards[node.Id] = guard;
            if (node.Type != "merge") continue;
            foreach (var field in ((JsonArray)node.Data["fields"]!).OfType<JsonObject>())
            {
                var candidates = data.Where(edge => edge.TargetHandle == Text(field["key"])).Select(edge => guards[edge.Source]).ToArray();
                for (var i = 0; i < candidates.Length; i++) for (var j = i + 1; j < candidates.Length; j++)
                    if (!Exclusive(candidates[i], candidates[j])) errors.Add($"merge '{node.Id}' field '{Text(field["key"])}' has non-exclusive sources");
                if (!field["required"]!.GetValue<bool>() || field.ContainsKey("default")) continue;
                var budget = 8192;
                if (!Covers(guard, candidates, branches, ref budget)) errors.Add($"merge '{node.Id}' required field '{Text(field["key"])}' is not covered by every route; connect all cases or make it optional");
            }
        }
        return errors;
    }

    /// <summary>加入必须满足的路由；两个不相容的要求说明图把互斥结果直接连入普通节点。</summary>
    private static void Add(Dictionary<string, string> guard, KeyValuePair<string, string> pair, string node, List<string> errors)
    {
        if (guard.TryGetValue(pair.Key, out var value) && value != pair.Value) errors.Add($"node '{node}' combines incompatible routes; use a merge node");
        else guard[pair.Key] = pair.Value;
    }

    /// <summary>不同出口属于同一条件决定时互斥，不能仅因来自不同节点就认定不同时执行。</summary>
    private static bool Exclusive(IReadOnlyDictionary<string, string> left, IReadOnlyDictionary<string, string> right) =>
        left.Any(pair => right.TryGetValue(pair.Key, out var value) && value != pair.Value);

    /// <summary>对有界路由空间验证必填字段覆盖；超过分析预算安全拒绝，而不是运行时任选结果。</summary>
    private static bool Covers(Dictionary<string, string> scope, IReadOnlyList<Dictionary<string, string>> candidates,
        IReadOnlyDictionary<string, IReadOnlyList<string>> branches, ref int budget)
    {
        if (--budget < 0) return false;
        var possible = candidates.Where(candidate => !Exclusive(scope, candidate)).ToArray();
        if (possible.Any(candidate => candidate.All(pair => scope.GetValueOrDefault(pair.Key) == pair.Value))) return true;
        if (possible.Length == 0) return false;
        var next = possible.SelectMany(candidate => candidate.Keys).First(key => !scope.ContainsKey(key));
        foreach (var branch in branches[next])
        {
            var narrowed = new Dictionary<string, string>(scope, StringComparer.Ordinal) { [next] = branch };
            if (!Covers(narrowed, possible, branches, ref budget)) return false;
        }
        return true;
    }
}
