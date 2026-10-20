using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Features.Workflow;
using CreativeFlow.UnitTests.Support;

namespace CreativeFlow.UnitTests.Ai;

/// <summary>条件及汇合的类型、控制依赖和确定性语义；业务调用使用计数替身。</summary>
public sealed class WorkflowBranchTests
{
    /// <summary>分别验证匹配、默认和缺少匹配子串，只有活跃 LLM 产生一次调用。</summary>
    [Theory]
    [InlineData("hit", 1, "fallback")]
    [InlineData("miss", 0, "hit")]
    [InlineData("HIT", 0, "hit")]
    public async Task OnlySelectedRouteExecutesAndMergeUsesItsTypedResult(string question, int calls, string skippedNode)
    {
        var validation = WorkflowGraphCompiler.Validate(WorkflowBranchFixtures.Graph());
        Assert.True(validation.IsValid, string.Join(";", validation.Errors));
        var plan = validation.Plan!;
        var models = new WorkflowV2Fixtures.Models();
        var snapshot = new JsonObject { ["inputs"] = new JsonObject { ["question"] = question }, ["models"] = await models.CaptureAsync(plan, TestContext.Current.CancellationToken) };
        var values = new Dictionary<string, JsonObject>(); var skipped = new HashSet<string>();
        foreach (var node in plan.Nodes)
        {
            if (WorkflowBranchFlow.ShouldSkip(node, values, skipped)) { skipped.Add(node.Id); continue; }
            values[node.Id] = await WorkflowNodeTestHost.ExecuteAsync(node, snapshot, values, models, TestContext.Current.CancellationToken, skipped: skipped);
        }
        Assert.Equal(calls, models.Calls); Assert.Single(skipped); Assert.Contains(skippedNode, skipped);
        Assert.Equal(calls == 1 ? "Synthetic: Answer: hit" : "No matching route; no model called.", values["end"]["answer"]!.GetValue<string>());
        Assert.True(WorkflowV2Values.TryRestore(plan.Nodes.Single(node => node.Id == "condition"), WorkflowV2Values.Output(values["condition"]).ToJsonString(), out _));
    }

    /// <summary>未知谓词、伪造知识类型、漏接默认、非法控制、非互斥汇合及必填覆盖空洞均在执行前拒绝。</summary>
    [Theory]
    [InlineData("operator")][InlineData("valueType")][InlineData("knowledge")][InlineData("missingDefault")]
    [InlineData("controlSource")][InlineData("controlTarget")][InlineData("duplicateControl")]
    [InlineData("ambiguous")][InlineData("uncovered")][InlineData("noMerge")][InlineData("duplicateCase")]
    public void InvalidRoutesCannotReachExecution(string mutation)
    {
        var graph = WorkflowBranchFixtures.Graph(); var nodes = graph["nodes"]!.AsArray(); var edges = graph["edges"]!.AsArray();
        var condition = nodes.Single(node => node!["id"]!.GetValue<string>() == "condition")!["data"]!;
        if (mutation == "operator") condition["cases"]![0]!["conditions"]![0]!["operator"] = "eval";
        if (mutation == "valueType") condition["cases"]![0]!["conditions"]![0]!["value"] = 4;
        if (mutation == "knowledge") condition["cases"]![0]!["conditions"]![0]!["operator"] = "hasHits";
        if (mutation == "missingDefault") edges.RemoveAt(3);
        if (mutation == "controlSource") edges[2]!["source"] = "start";
        if (mutation == "controlTarget") edges[2]!["target"] = "end";
        if (mutation == "duplicateControl") edges.Add((JsonNode)WorkflowBranchFixtures.Edge("extra", "condition", "else", "hit", WorkflowBranchValues.ControlPort));
        if (mutation == "ambiguous") edges[5]!["source"] = "hit";
        if (mutation == "uncovered") { edges[5]!["targetHandle"] = "optional"; nodes.Single(node => node!["id"]!.GetValue<string>() == "merge")!["data"]!["fields"]!.AsArray().Add((JsonNode)new JsonObject { ["key"] = "optional", ["type"] = "string", ["required"] = false }); }
        if (mutation == "noMerge") nodes.Single(node => node!["id"]!.GetValue<string>() == "merge")!["type"] = "llm";
        if (mutation == "duplicateCase") condition["cases"]!.AsArray().Add(condition["cases"]![0]!.DeepClone());
        Assert.False(WorkflowGraphCompiler.Validate(graph).IsValid);
    }

    /// <summary>多条件 AND/OR 采用首个匹配出口；可选字段缺失不会使不等判断意外匹配。</summary>
    [Fact]
    public void TypedRulesSupportAllAnyAndMissingWithoutCoercion()
    {
        var data = JsonNode.Parse("""
          {"fields":[{"key":"score","type":"number","required":true},{"key":"flag","type":"boolean","required":true},{"key":"optional","type":"string","required":false}],"cases":[
            {"key":"high","match":"all","conditions":[{"input":"score","operator":"gte","value":5},{"input":"flag","operator":"isTrue"}]},
            {"key":"other","match":"any","conditions":[{"input":"optional","operator":"ne","value":"x"},{"input":"flag","operator":"isFalse"}]}]}
          """)!.AsObject();
        Assert.Null(WorkflowBranchValues.Validate(data));
        Assert.Equal("high", WorkflowBranchValues.Evaluate(data, new JsonObject { ["score"] = 5, ["flag"] = true })["route"]!.GetValue<string>());
        Assert.Equal("other", WorkflowBranchValues.Evaluate(data, new JsonObject { ["score"] = 5, ["flag"] = false })["route"]!.GetValue<string>());
        Assert.Equal("else", WorkflowBranchValues.Evaluate(data, new JsonObject { ["score"] = 2, ["flag"] = true })["route"]!.GetValue<string>());
        data["cases"]![1]!["conditions"]![0] = new JsonObject { ["input"] = "optional", ["operator"] = "isMissing" };
        Assert.Equal("other", WorkflowBranchValues.Evaluate(data, new JsonObject { ["score"] = 2, ["flag"] = true })["route"]!.GetValue<string>());
    }

    /// <summary>名义知识类型支持命中判断但不接受任意 JSON 比较或默认伪造证据。</summary>
    [Theory]
    [InlineData("contextChunks", "hasHits")][InlineData("contextChunks", "noHits")]
    [InlineData("answer", "isGrounded")][InlineData("answer", "notGrounded")]
    public void KnowledgePredicatesRemainTypeRestricted(string type, string op)
    {
        var data = JsonNode.Parse("""{"fields":[{"key":"evidence","type":"string","required":true}],"cases":[{"key":"yes","match":"all","conditions":[{"input":"evidence","operator":"isPresent"}]}]}""")!.AsObject();
        data["fields"]![0]!["type"] = type; data["cases"]![0]!["conditions"]![0]!["operator"] = op;
        Assert.Null(WorkflowBranchValues.Validate(data));
        data["fields"]![0]!["type"] = "object"; Assert.NotNull(WorkflowBranchValues.Validate(data));
    }

    /// <summary>恢复必须使用真实出口；即使持久数据被破坏，汇合也拒绝多活跃来源。</summary>
    [Fact]
    public void CorruptDecisionAndAmbiguousRuntimeValuesAreRejected()
    {
        var plan = WorkflowGraphCompiler.Validate(WorkflowBranchFixtures.Graph()).Plan!;
        Assert.False(WorkflowV2Values.TryRestore(plan.Nodes.Single(node => node.Id == "condition"), """{"schemaVersion":2,"fields":{"route":"invented"}}""", out _));
        var values = new Dictionary<string, JsonObject> { ["hit"] = new() { ["text"] = "one" }, ["fallback"] = new() { ["text"] = "two" } };
        Assert.Throws<WorkflowV2Exception>(() => WorkflowBranchFlow.BindInputs(plan.Nodes.Single(node => node.Id == "merge"), values, new HashSet<string>()));
        Assert.Equal(new HashSet<string> { "condition", "hit", "fallback", "merge", "end" }, WorkflowPartialRerunPlanner.FindRerunNodeIds(plan, "condition"));
    }

    /// <summary>嵌套决定保留外层执行条件，覆盖分析能验证内外层全部出口而不会合并互斥依赖。</summary>
    [Theory]
    [InlineData("hit deep", 1, "Synthetic: Answer: hit deep")]
    [InlineData("hit shallow", 0, "inner default")]
    [InlineData("miss", 0, "No matching route; no model called.")]
    public async Task NestedConditionsExecuteOnlyTheSelectedLeaf(string question, int calls, string expected)
    {
        var graph = WorkflowBranchFixtures.Graph(); var nodes = graph["nodes"]!.AsArray(); var edges = graph["edges"]!.AsArray();
        var inner = nodes.Single(node => node!["id"]!.GetValue<string>() == "condition")!.DeepClone(); inner["id"] = "inner";
        inner["data"]!["cases"]![0]!["conditions"]![0]!["value"] = "deep";
        nodes.Add(inner);
        nodes.Add((JsonNode)WorkflowBranchFixtures.Node("innerFallback", "merge", JsonNode.Parse("""{"fields":[{"key":"text","type":"string","required":true,"default":"inner default"}]}""")!.AsObject()));
        edges[2]!["target"] = "inner";
        edges.Add((JsonNode)WorkflowBranchFixtures.Edge("inner-input", "start", "question", "inner", "question"));
        edges.Add((JsonNode)WorkflowBranchFixtures.Edge("inner-hit", "inner", "match", "hit", WorkflowBranchValues.ControlPort));
        edges.Add((JsonNode)WorkflowBranchFixtures.Edge("inner-else", "inner", "else", "innerFallback", WorkflowBranchValues.ControlPort));
        edges.Add((JsonNode)WorkflowBranchFixtures.Edge("inner-result", "innerFallback", "text", "merge", "text"));
        var validation = WorkflowGraphCompiler.Validate(graph); Assert.True(validation.IsValid, string.Join(";", validation.Errors));
        var models = new WorkflowV2Fixtures.Models(); var snapshot = new JsonObject { ["inputs"] = new JsonObject { ["question"] = question }, ["models"] = await models.CaptureAsync(validation.Plan!, TestContext.Current.CancellationToken) };
        var values = new Dictionary<string, JsonObject>(); var skipped = new HashSet<string>();
        foreach (var node in validation.Plan!.Nodes)
        {
            if (WorkflowBranchFlow.ShouldSkip(node, values, skipped)) { skipped.Add(node.Id); continue; }
            values[node.Id] = await WorkflowNodeTestHost.ExecuteAsync(node, snapshot, values, models, TestContext.Current.CancellationToken, skipped: skipped);
        }
        Assert.Equal(calls, models.Calls); Assert.Equal(expected, values["end"]["answer"]!.GetValue<string>());
    }
}
