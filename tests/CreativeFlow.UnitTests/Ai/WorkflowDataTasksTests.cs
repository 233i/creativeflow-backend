using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Features.Workflow;
using CreativeFlow.UnitTests.Support;

namespace CreativeFlow.UnitTests.Ai;

/// <summary>数据节点的真实类型、嵌套模式、严格解析与有界表达式回归，不访问收费模型。</summary>
public sealed class WorkflowDataTasksTests
{
    /// <summary>具有嵌套对象、整数、枚举、数组和可选字段的公共测试模式。</summary>
    private static JsonArray Schema() => JsonNode.Parse("""
        [{"key":"customer","type":"object","required":true,"properties":[
          {"key":"name","type":"string","required":true,"maxLength":20},
          {"key":"count","type":"number","integer":true,"minimum":1,"maximum":8,"required":true},
          {"key":"note","type":"string","required":false}]},
         {"key":"tags","type":"array","required":true,"minItems":1,"maxItems":2,
          "items":{"type":"string","enum":["a","b"]}}]
        """)!.AsArray();

    /// <summary>嵌套约束和必填字段严格生效，旧未声明子字段的对象继续兼容。</summary>
    [Fact]
    public void NestedSchemasBindWithoutCoercionAndKeepLegacyObjects()
    {
        var schema = Schema(); Assert.Null(WorkflowV2Values.ValidateFields(schema));
        var input = JsonNode.Parse("""{"customer":{"name":"Ada","count":2},"tags":["a"]}""")!.AsObject();
        Assert.Null(WorkflowV2Values.Bind(schema, input, out var bound)); Assert.True(JsonNode.DeepEquals(input, bound));
        foreach (var bad in new[] { """{"name":"Ada","count":"2"}""", """{"name":"Ada","count":2.5}""", """{"name":"Ada","count":2,"secret":1}""", """{"name":"Ada"}""" })
        {
            input["customer"] = JsonNode.Parse(bad); Assert.NotNull(WorkflowV2Values.Bind(schema, input, out _));
        }
        Assert.True(WorkflowV2Values.Matches(new JsonObject { ["type"] = "object" }, new JsonObject { ["legacy"] = 1 }));
    }

    /// <summary>模式本身的错误在提交前拒绝，不能借嵌套字段伪造知识证据或原型。</summary>
    [Theory]
    [InlineData("prototype")][InlineData("knowledge")][InlineData("enum")][InlineData("depth")]
    public void InvalidSchemaCannotBeSaved(string mutation)
    {
        var schema = Schema();
        if (mutation == "prototype") schema[0]!["properties"]![0]!["key"] = "constructor";
        if (mutation == "knowledge") schema[0]!["properties"]![0]!["type"] = "answer";
        if (mutation == "enum") schema[1]!["items"]!["enum"] = new JsonArray(1);
        if (mutation == "depth")
        {
            var current = schema[0]!.AsObject();
            for (var i = 0; i < 8; i++) { var child = new JsonObject { ["key"] = "deep", ["type"] = "object", ["required"] = true }; current["properties"] = new JsonArray(child); current = child; }
        }
        Assert.NotNull(WorkflowV2Values.ValidateFields(schema));
    }

    /// <summary>原生严格模式显式表示可选 null，返回后只去掉可选占位，不丢掉未知字段或错误值。</summary>
    [Fact]
    public void NativeSchemaNormalizesOnlyOptionalPlaceholders()
    {
        var node = Structured(); node.Data["outputTransport"] = "json_schema";
        var format = WorkflowModelTasks.ResponseFormat(node)!;
        Assert.True(format["json_schema"]!["strict"]!.GetValue<bool>());
        Assert.Equal(3, format["json_schema"]!["schema"]!["properties"]!["customer"]!["required"]!.AsArray().Count);
        var output = WorkflowModelTasks.Parse(node, """{"customer":{"name":"Ada","count":2,"note":null},"tags":["a"]}""");
        Assert.False(output["customer"]!.AsObject().ContainsKey("note"));
        Assert.Throws<WorkflowV2Exception>(() => WorkflowModelTasks.Parse(node, """{"customer":{"name":null,"count":2},"tags":["a"]}"""));
    }

    /// <summary>不自动修复围栏、截断、错误枚举、错误数组、未知字段和缺失的必填值。</summary>
    [Theory]
    [InlineData("```json\n{}\n```")][InlineData("{")][InlineData("{}")][InlineData("[]")]
    [InlineData("{\"customer\":{\"name\":\"Ada\",\"count\":2},\"tags\":[\"c\"]}")]
    [InlineData("{\"customer\":{\"name\":\"Ada\",\"count\":2},\"tags\":[]}")]
    public void InvalidModelJsonFailsWithoutFabricatedDefaults(string response) => Assert.Throws<WorkflowV2Exception>(() => WorkflowModelTasks.Parse(Structured(), response));

    /// <summary>提取节点调用一次并交付类型化字段；相同节点也能独立调试，未隐式执行上游。</summary>
    [Fact]
    public async Task ExtractionUsesOneCallAndTypedPorts()
    {
        var node = Structured() with { Type = "parameterExtract" }; var models = new WorkflowV2Fixtures.Models { StructuredResponse = """{"customer":{"name":"Ada","count":2},"tags":["a"]}""" };
        var snapshot = new JsonObject { ["models"] = await models.CaptureAsync(new([node]), TestContext.Current.CancellationToken) };
        var output = await WorkflowNodeTestHost.ExecuteBoundAsync(node, snapshot, new JsonObject { ["question"] = "Ada wants two of a" }, models, TestContext.Current.CancellationToken);
        Assert.Equal(1, models.Calls); Assert.Equal(2, output["customer"]!["count"]!.GetValue<int>());
        Assert.Contains("Do not invent", models.Prompt); Assert.Equal("customer", WorkflowV2GraphValidator.OutputFields(node)[0]!["key"]!.GetValue<string>());
    }

    /// <summary>分类复用条件路由和汇合证明，不能返回未声明分类或漏接默认分支。</summary>
    [Fact]
    public void IntentBranchesCompileAndRejectUnknownDecisions()
    {
        var graph = WorkflowBranchFixtures.Graph(); var raw = graph["nodes"]!.AsArray().Single(node => node!["id"]!.GetValue<string>() == "condition")!;
        raw["type"] = "intentClassify";
        var data = (JsonObject)WorkflowV2Fixtures.Graph()["nodes"]![1]!["data"]!.DeepClone();
        data["intents"] = new JsonArray(new JsonObject { ["key"] = "match", ["label"] = "命中", ["description"] = "输入明确要求命中分支" });
        raw["data"] = data;
        var validation = WorkflowV2GraphValidator.Validate(graph);
        Assert.True(validation.IsValid, string.Join(";", validation.Errors));
        var node = validation.Plan!.Nodes.Single(node => node.Id == "condition");
        Assert.Equal("else", WorkflowModelTasks.Parse(node, """{"route":"else"}""")["route"]!.GetValue<string>());
        Assert.Throws<WorkflowV2Exception>(() => WorkflowModelTasks.Parse(node, """{"route":"unknown"}"""));
        Assert.True(WorkflowBranchFlow.ShouldSkip(validation.Plan.Nodes.Single(node => node.Id == "hit"), new Dictionary<string, JsonObject> { ["condition"] = new() { ["route"] = "else" } }, new HashSet<string>()));
    }

    /// <summary>所有内置变量操作的真实结果，不通过字符串猜测类型。</summary>
    [Theory]
    [InlineData("{\"op\":\"toNumber\",\"args\":[{\"op\":\"constant\",\"value\":\"2.5\"}]}", "2.5", "number")]
    [InlineData("{\"op\":\"toBoolean\",\"args\":[{\"op\":\"constant\",\"value\":\"false\"}]}", "false", "boolean")]
    [InlineData("{\"op\":\"concat\",\"separator\":\"-\",\"args\":[{\"op\":\"constant\",\"value\":\"a\"},{\"op\":\"constant\",\"value\":\"b\"}]}", "\"a-b\"", "string")]
    [InlineData("{\"op\":\"parseJson\",\"args\":[{\"op\":\"constant\",\"value\":\"{\\\"a\\\":1}\"}]}", "{\"a\":1}", "object")]
    [InlineData("{\"op\":\"coalesce\",\"args\":[{\"op\":\"input\",\"input\":\"input\",\"path\":\"/missing\"},{\"op\":\"constant\",\"value\":3}]}", "3", "number")]
    [InlineData("{\"op\":\"input\",\"input\":\"input\",\"path\":\"/nested/0/name\"}", "\"Ada\"", "string")]
    public void TransformOperationsAreDeterministic(string expression, string expected, string type)
    {
        var data = TransformData(expression, type); Assert.Null(WorkflowTransform.Validate(data));
        var inputs = JsonNode.Parse("""{"input":{"nested":[{"name":"Ada"}]}}""")!.AsObject();
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), WorkflowTransform.Execute(data, inputs)["result"]));
    }

    /// <summary>不支持的脚本、危险属性和错误转换明确拒绝，不执行用户代码。</summary>
    [Theory]
    [InlineData("{\"op\":\"eval\",\"args\":[]}")]
    [InlineData("{\"op\":\"input\",\"input\":\"input\",\"path\":\"/constructor\"}")]
    public void UnsafeTransformsFailValidation(string expression) => Assert.NotNull(WorkflowTransform.Validate(TransformData(expression, "string")));

    /// <summary>构建合法的提取测试节点，保持原有 LLM 参数和输入契约。</summary>
    private static WorkflowPlanNode Structured()
    {
        var node = WorkflowV2GraphValidator.Validate(WorkflowV2Fixtures.Graph()).Plan!.Nodes.Single(node => node.Type == "llm");
        node.Data["outputMode"] = "json"; node.Data["outputFields"] = Schema(); return node;
    }

    /// <summary>生成只包含一个输出的表达式测试配置。</summary>
    private static JsonObject TransformData(string expression, string type) => new()
    {
        ["fields"] = new JsonArray(new JsonObject { ["key"] = "input", ["type"] = "object", ["required"] = true }),
        ["outputFields"] = new JsonArray(new JsonObject { ["key"] = "result", ["type"] = type, ["required"] = true }),
        ["assignments"] = new JsonArray(new JsonObject { ["key"] = "result", ["expression"] = JsonNode.Parse(expression) }),
    };
}
