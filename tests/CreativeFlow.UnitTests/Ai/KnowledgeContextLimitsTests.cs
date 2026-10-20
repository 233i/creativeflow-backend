using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Features.Workflow;
using CreativeFlow.Domain.Ai;
using CreativeFlow.UnitTests.Support;

namespace CreativeFlow.UnitTests.Ai;

/// <summary>片段数量配置、类型化证据容量和恢复路径的离线回归，不连接模型或数据库。</summary>
public sealed class KnowledgeContextLimitsTests
{
    /// <summary>草稿、执行和发布共享整数边界，不把非法配置静默替换成默认值。</summary>
    [Theory]
    [InlineData("0")][InlineData("33")][InlineData("-1")][InlineData("1.5")][InlineData("null")][InlineData("\"12\"")]
    public void InvalidNodeLimitsAreRejected(string value)
    {
        var graph = WorkflowV2Fixtures.KnowledgeGraph(Guid.NewGuid(), Guid.NewGuid());
        graph["nodes"]![3]!["data"]!["maxContextChunks"] = JsonNode.Parse(value);
        Assert.False(WorkflowV2GraphValidator.Validate(graph, false).IsValid);
        Assert.False(WorkflowV2GraphValidator.Validate(graph).IsValid);
        Assert.Throws<WorkflowV2Exception>(() => WorkflowV2PublicationService.Contract(graph.ToJsonString()));
    }

    /// <summary>设置持久化在图中，未设置的节点默认八片段；发布契约保留配置且不增加外部可伪造参数。</summary>
    [Theory]
    [InlineData(1)][InlineData(8)][InlineData(12)][InlineData(32)]
    public void ConfiguredLimitsPersistInPublicationGraph(int limit)
    {
        var graph = WorkflowV2Fixtures.KnowledgeGraph(Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(8, KnowledgeWorkflowValues.ContextLimit(graph["nodes"]![3]!["data"]!.AsObject()));
        graph["nodes"]![3]!["data"]!["maxContextChunks"] = limit;
        graph["nodes"]![3]!["data"]!["topK"] = 32;
        var published = graph.ToJsonString();
        graph["nodes"]![3]!["data"]!["maxContextChunks"] = 2;
        var (plan, inputs, _) = WorkflowV2PublicationService.Contract(published);
        Assert.Equal(limit, KnowledgeWorkflowValues.ContextLimit(plan.Nodes.Single(node => node.Type == "knowledgeRetrieve").Data));
        Assert.Single(inputs);
        Assert.Equal("question", inputs[0]!["key"]!.GetValue<string>());
    }

    /// <summary>三十二个最长片段经过绑定、汇合和恢复时仍保留知识类型，不依赖旧 32 KiB 预算。</summary>
    [Theory]
    [InlineData("contextChunks")][InlineData("answer")]
    public void LargeKnowledgeValuesStayTypedAcrossMergeAndRestore(string type)
    {
        var value = Value(type);
        var field = new JsonObject { ["key"] = "evidence", ["type"] = type, ["required"] = true };
        var fields = new JsonArray(field);
        var input = new JsonObject { ["evidence"] = value };
        Assert.True(WorkflowV2Values.IsBounded(input)); // HTTP 普通数据现允许 1 MiB；知识字段仍走专有证据验证。
        Assert.Null(WorkflowV2Values.Bind(fields, input, out var bound));
        foreach (var nodeType in new[] { "merge", "end" })
        {
            var node = new WorkflowPlanNode(nodeType, nodeType, new JsonObject { ["fields"] = fields.DeepClone() }, []);
            Assert.True(WorkflowV2Values.TryRestore(node, WorkflowV2Values.Output(bound).ToJsonString(), out var restored));
            Assert.True(JsonNode.DeepEquals(input, restored));
        }
        Assert.True(WorkflowV2Values.Matches(new JsonObject { ["type"] = "object" }, value));
        Assert.False(WorkflowV2Values.Matches(field, new JsonObject { ["text"] = "伪造普通对象" }));
    }

    /// <summary>外部 API 的结束字段投影保留超过普通预算的完整答案及全部三十二个来源。</summary>
    [Fact]
    public void PublishedOutputRetainsAllKnowledgeCitations()
    {
        var graph = WorkflowV2Fixtures.KnowledgeGraph(Guid.NewGuid(), Guid.NewGuid());
        graph["nodes"]![3]!["data"]!["maxContextChunks"] = 32;
        var (_, inputs, outputs) = WorkflowV2PublicationService.Contract(graph.ToJsonString());
        var deployment = new WorkflowDeployment(new(Guid.NewGuid()), new(Guid.NewGuid()), 1, 1, graph.ToJsonString(), inputs.ToJsonString(), outputs.ToJsonString(),
            "synthetic", WorkflowDeploymentStatus.Published, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);
        var result = new JsonObject { ["answer"] = Value("answer") };
        var node = new WorkflowNodeRun(new(Guid.NewGuid()), "end", "end", WorkflowNodeRunStatus.Succeeded, false, null, WorkflowV2Values.Output(result).ToJsonString(), null, null, null);
        Assert.True(JsonNode.DeepEquals(result, WorkflowV2PublicationService.Outputs(deployment, [node])));
    }

    /// <summary>即使存在合法知识端口，普通字段仍共同受 2 MiB 信封预算约束，包括默认值。</summary>
    [Theory]
    [InlineData(false)][InlineData(true)]
    public void KnowledgeFieldsDoNotRelaxOrdinaryAggregateBudget(bool defaults)
    {
        var fields = new JsonArray(new JsonObject { ["key"] = "context", ["type"] = "contextChunks", ["required"] = true });
        var input = new JsonObject { ["context"] = Value("contextChunks") };
        foreach (var key in new[] { "first", "second", "third" })
        {
            var field = new JsonObject { ["key"] = key, ["type"] = "string", ["required"] = true };
            if (defaults) field["default"] = new string('文', 300000); else input[key] = new string('文', 300000);
            fields.Add(field);
        }
        Assert.NotNull(WorkflowV2Values.Bind(fields, input, out _, defaults));
    }

    /// <summary>加大数量不会接受超长片段、过多片段或与数量不一致的截断元数据。</summary>
    [Theory]
    [InlineData("count")][InlineData("length")][InlineData("limit")][InlineData("reason")][InlineData("extra")]
    public void MalformedEvidenceIsRejected(string mutation)
    {
        var context = Value("contextChunks");
        if (mutation == "count") context["hits"]!.AsArray().Add(context["hits"]![0]!.DeepClone());
        if (mutation == "length") context["hits"]![0]!["text"] = new string('文', 1601);
        if (mutation == "limit") context["maxContextChunks"] = 8;
        if (mutation == "reason") context["truncationReason"] = "chunk_limit";
        if (mutation == "extra") context["hits"]![0]!["untrusted"] = new string('x', 16000);
        Assert.False(KnowledgeWorkflowValues.Matches("contextChunks", context));
        Assert.NotNull(WorkflowV2Values.Bind(KnowledgeWorkflowValues.Outputs("knowledgeRetrieve"), new JsonObject { ["context"] = context }, out _));
    }

    /// <summary>构造覆盖最大正文、文档名和答案转义长度的纯合成值。</summary>
    private static JsonObject Value(string type)
    {
        var library = Guid.NewGuid().ToString(); var version = Guid.NewGuid().ToString();
        var chunks = new JsonArray();
        for (var index = 0; index < 32; index++)
        {
            var chunk = new JsonObject { ["id"] = Guid.NewGuid().ToString(), ["document"] = new string('档', 120),
                ["startLine"] = index + 1, ["endLine"] = index + 1, ["text"] = new string('文', 1600), ["role"] = "supporting" };
            if (type == "contextChunks") chunk["score"] = 0.8;
            else { chunk["libraryId"] = library; chunk["versionId"] = version; }
            chunks.Add(chunk);
        }
        return type == "contextChunks"
            ? new() { ["libraryId"] = library, ["versionId"] = version, ["retrievalNodeId"] = "retrieve", ["question"] = new string('问', 2000), ["truncated"] = false, ["maxContextChunks"] = 32, ["hits"] = chunks }
            : new() { ["text"] = new string('答', 8000), ["grounded"] = true, ["citations"] = chunks };
    }
}
