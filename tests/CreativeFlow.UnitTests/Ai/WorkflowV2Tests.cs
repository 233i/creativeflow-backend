using System.Text.Json.Nodes;
using CreativeFlow.Application.Features.Workflow;
using CreativeFlow.Infrastructure.Ai;
using CreativeFlow.UnitTests.Support;

namespace CreativeFlow.UnitTests.Ai;

/// <summary>锁定新旧图隔离、有界类型值、真实变量依赖和输出恢复。</summary>
public sealed class WorkflowV2Tests
{
    /// <summary>乱序节点按真实依赖编译，局部重跑只覆盖指定节点与下游。</summary>
    [Fact]
    public void CompilesTypedDependenciesWithoutChangingLegacyValidation()
    {
        var graph = WorkflowV2Fixtures.Graph();
        var result = WorkflowGraphCompiler.Validate(graph);
        Assert.True(result.IsValid, string.Join(";", result.Errors));
        Assert.Equal(["start", "llm", "end"], result.Plan!.Nodes.Select(node => node.Id));
        Assert.Equal(["end", "llm"], WorkflowPartialRerunPlanner.FindRerunNodeIds(result.Plan, "llm")!.Order());
        Assert.False(WorkflowGraphValidator.Validate(graph).IsValid);
        var legacy = JsonNode.Parse("""{"nodes":[{"id":"t","type":"text","data":{"text":"old"}}],"edges":[]}""");
        Assert.Equal(WorkflowGraphValidator.Validate(legacy).Errors, WorkflowGraphCompiler.Validate(legacy).Errors);
    }

    /// <summary>草稿可暂未选择模型；正式提交绝不能推测或使用全局默认模型。</summary>
    [Fact]
    public void DraftDoesNotRequireModelButExecutionDoes()
    {
        var graph = WorkflowV2Fixtures.Graph(); graph["nodes"]![1]!["data"]!["modelId"] = "";
        Assert.True(WorkflowV2GraphValidator.Validate(graph, false).IsValid);
        Assert.False(WorkflowGraphCompiler.Validate(graph).IsValid);
    }

    /// <summary>草稿和执行计划允许大输出预算，知识回答与普通模型采用相同规则，不静默截小。</summary>
    [Theory]
    [InlineData(4097)][InlineData(32768)][InlineData(131072)][InlineData(int.MaxValue)]
    public void OutputBudgetIsNotCappedAt4096(int tokens)
    {
        foreach (var graph in new[] { WorkflowV2Fixtures.Graph(), WorkflowV2Fixtures.KnowledgeGraph(Guid.NewGuid(), Guid.NewGuid()) })
        {
            graph["nodes"]![1]!["data"]!["maxOutputTokens"] = tokens;
            Assert.True(WorkflowV2GraphValidator.Validate(graph, false).IsValid);
            var compiled = WorkflowGraphCompiler.Validate(graph);
            Assert.True(compiled.IsValid, string.Join(";", compiled.Errors));
            Assert.Equal(tokens, compiled.Plan!.Nodes.Single(node => WorkflowModelTasks.UsesModel(node.Type)).Data["maxOutputTokens"]!.GetValue<int>());
        }
    }

    /// <summary>放开业务上限不允许缺失、小数、零、负数或协议整数溢出。</summary>
    [Theory]
    [InlineData("null")][InlineData("0")][InlineData("-1")][InlineData("1.5")][InlineData("2147483648")][InlineData("1e100")][InlineData("\"32768\"")]
    public void InvalidOutputBudgetsAreRejected(string value)
    {
        var graph = WorkflowV2Fixtures.Graph(); graph["nodes"]![1]!["data"]!["maxOutputTokens"] = JsonNode.Parse(value);
        Assert.False(WorkflowV2GraphValidator.Validate(graph, false).IsValid);
        Assert.False(WorkflowGraphCompiler.Validate(graph).IsValid);
    }

    /// <summary>草稿也必须能安全渲染，不允许隐藏节点样式、字段路径或非字符串连线标识。</summary>
    [Theory]
    [InlineData("node")][InlineData("edge")][InlineData("source")]
    public void DraftRejectsUnsafeRendererMetadata(string mutation)
    {
        var graph = WorkflowV2Fixtures.Graph();
        if (mutation == "node") graph["nodes"]![0]!["style"] = new JsonObject { ["backgroundImage"] = "url(https://example.invalid)" };
        if (mutation == "edge") graph["edges"]![0]!["sourcePath"] = "secret.key";
        if (mutation == "source") graph["edges"]![0]!["source"] = new JsonObject();
        Assert.False(WorkflowV2GraphValidator.Validate(graph, false).IsValid);
    }

    /// <summary>拒绝错误字段类型、未知绑定、重复来源、环路和不透明配置。</summary>
    [Theory]
    [InlineData("type")][InlineData("port")][InlineData("duplicate")][InlineData("cycle")]
    [InlineData("prompt")][InlineData("secret")][InlineData("orphan")][InlineData("range")]
    public void InvalidGraphsCannotReachExecution(string mutation)
    {
        var graph = WorkflowV2Fixtures.Graph();
        switch (mutation)
        {
            case "type": graph["nodes"]![2]!["data"]!["fields"]![0]!["type"] = "object"; break;
            case "port": graph["edges"]![0]!["sourceHandle"] = "missing"; break;
            case "duplicate": var edge = graph["edges"]![0]!.DeepClone(); edge["id"] = "c"; graph["edges"]!.AsArray().Add(edge); break;
            case "cycle": graph["edges"]![1]!["source"] = "end"; break;
            case "prompt": graph["nodes"]![1]!["data"]!["prompt"]![1]!["input"] = "missing"; break;
            case "secret": graph["nodes"]![1]!["data"]!["apiKey"] = "never-allowed"; break;
            case "orphan": graph["edges"]!.AsArray().RemoveAt(0); break;
            case "range": graph["nodes"]![1]!["data"]!["timeoutSeconds"] = 301; break;
        }
        Assert.False(WorkflowGraphCompiler.Validate(graph).IsValid);
    }

    /// <summary>数字、布尔及对象保持真实类型，默认值显式应用，原型污染键和巨型输入被拒绝。</summary>
    [Fact]
    public void ValuesRemainTypedAndBounded()
    {
        var fields = JsonNode.Parse("""[{"key":"n","type":"number","required":true,"minimum":0,"maximum":10},{"key":"flag","type":"boolean","required":true},{"key":"obj","type":"object","required":false,"default":{"a":1}}]""")!.AsArray();
        Assert.Null(WorkflowV2Values.ValidateFields(fields));
        Assert.Null(WorkflowV2Values.Bind(fields, new JsonObject { ["n"] = 3, ["flag"] = false }, out var bound));
        Assert.Equal(3, bound["n"]!.GetValue<int>()); Assert.False(bound["flag"]!.GetValue<bool>());
        Assert.NotNull(bound["obj"]);
        Assert.NotNull(WorkflowV2Values.Bind(fields, new JsonObject { ["n"] = "3", ["flag"] = false }, out _));
        Assert.NotNull(WorkflowV2Values.Bind(fields, new JsonObject { ["n"] = 11, ["flag"] = false }, out _));
        Assert.False(WorkflowV2Values.IsBounded(JsonNode.Parse("""{"safe":{"__proto__":{}}}""")));
        Assert.True(WorkflowV2Values.IsBounded(JsonValue.Create(new string('a', 16001))));
        Assert.False(WorkflowV2Values.IsBounded(JsonValue.Create(new string('a', WorkflowV2Values.MaxBytes + 1))));
        Assert.False(WorkflowV2Values.IsBounded(new JsonArray(Enumerable.Repeat<JsonNode?>(null, 101).ToArray())));
        Assert.False(WorkflowV2Values.ValidKey("constructor"));
    }

    /// <summary>输出需符合节点声明；新旧信封不能互相冒充成功输出。</summary>
    [Fact]
    public void RestoreChecksTypeVersionAndRequiredFields()
    {
        var node = WorkflowGraphCompiler.Validate(WorkflowV2Fixtures.Graph()).Plan!.Nodes[1];
        var output = WorkflowV2Values.Output(new JsonObject { ["text"] = "done" }).ToJsonString();
        Assert.True(WorkflowNodeValue.TryRestoreV2(node, output, out var restored));
        Assert.Equal(output, restored.ToJson().ToJsonString());
        Assert.False(WorkflowNodeValue.TryParse(output, out _));
        Assert.False(WorkflowV2Values.TryRestore(node, """{"texts":[],"images":[],"videos":[]}""", out _));
        Assert.False(WorkflowV2Values.TryRestore(node, """{"schemaVersion":2,"fields":{"text":2}}""", out _));
        Assert.False(WorkflowV2Values.TryRestore(node, """{"schemaVersion":2,"fields":{}}""", out _));
    }

    /// <summary>结构化片段只拼接数据，变量中的代码样式字符串不会被求值。</summary>
    [Fact]
    public async Task ExecutorBindsInputsAndReturnsDeclaredEndFields()
    {
        var plan = WorkflowGraphCompiler.Validate(WorkflowV2Fixtures.Graph()).Plan!;
        var models = new WorkflowV2Fixtures.Models();
        var snapshot = new JsonObject { ["inputs"] = new JsonObject { ["question"] = "${process.exit()}" }, ["models"] = await models.CaptureAsync(plan, TestContext.Current.CancellationToken) };
        var values = new Dictionary<string, JsonObject>();
        foreach (var node in plan.Nodes) values[node.Id] = await WorkflowNodeTestHost.ExecuteAsync(node, snapshot, values, models, TestContext.Current.CancellationToken);
        Assert.Equal(1, models.Calls); Assert.Equal("Answer: ${process.exit()}", models.Prompt);
        Assert.Equal("Synthetic: Answer: ${process.exit()}", values["end"]["answer"]!.GetValue<string>());
    }
}
