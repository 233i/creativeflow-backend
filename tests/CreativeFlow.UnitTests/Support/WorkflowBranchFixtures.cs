using System.Text.Json.Nodes;
using CreativeFlow.Application.Features.Workflow;

namespace CreativeFlow.UnitTests.Support;

/// <summary>分支验收图只引用合成模型；未命中路径直接返回固定文本，不发送模型请求。</summary>
internal static class WorkflowBranchFixtures
{
    /// <summary>构造文本条件、一次 LLM、无调用预设结果和必填文本汇合的完整图。</summary>
    public static JsonObject Graph()
    {
        var graph = WorkflowV2Fixtures.Graph();
        var nodes = graph["nodes"]!.AsArray();
        nodes[1]!["id"] = "hit";
        nodes.Add((JsonNode)Node("condition", "condition", JsonNode.Parse("""
            {"fields":[{"key":"question","type":"string","required":true}],"cases":[
              {"key":"match","label":"包含 hit","match":"all","conditions":[{"input":"question","operator":"contains","value":"hit"}]}]}
            """)!.AsObject()));
        nodes.Add((JsonNode)Node("fallback", "merge", JsonNode.Parse("""
            {"fields":[{"key":"text","type":"string","required":true,"default":"No matching route; no model called."}]}
            """)!.AsObject()));
        nodes.Add((JsonNode)Node("merge", "merge", JsonNode.Parse("""
            {"fields":[{"key":"text","type":"string","required":true}]}
            """)!.AsObject()));
        graph["edges"] = new JsonArray(
            Edge("a", "start", "question", "condition", "question"),
            Edge("b", "start", "question", "hit", "question"),
            Edge("c", "condition", "match", "hit", WorkflowBranchValues.ControlPort),
            Edge("d", "condition", "else", "fallback", WorkflowBranchValues.ControlPort),
            Edge("e", "hit", "text", "merge", "text"),
            Edge("f", "fallback", "text", "merge", "text"),
            Edge("g", "merge", "text", "end", "answer"));
        return graph;
    }

    /// <summary>在真实知识检索后按命中分流，答案保留名义类型，默认路径返回独立的可选文本字段。</summary>
    public static JsonObject KnowledgeGraph(Guid library, Guid version)
    {
        var graph = WorkflowV2Fixtures.KnowledgeGraph(library, version);
        var fields = JsonNode.Parse("""[{"key":"answer","type":"answer","required":false},{"key":"fallback","type":"string","required":false}]""")!;
        graph["nodes"]![0]!["data"]!["fields"] = fields.DeepClone();
        graph["nodes"]!.AsArray().Add((JsonNode)Node("condition", "condition", JsonNode.Parse("""
            {"fields":[{"key":"context","type":"contextChunks","required":true}],"cases":[
              {"key":"hit","match":"all","conditions":[{"input":"context","operator":"hasHits"}]}]}
            """)!.AsObject()));
        graph["nodes"]!.AsArray().Add((JsonNode)Node("fallback", "merge", JsonNode.Parse("""
            {"fields":[{"key":"fallback","type":"string","required":true,"default":"No knowledge evidence; use another approved source."}]}
            """)!.AsObject()));
        graph["nodes"]!.AsArray().Add((JsonNode)Node("merge", "merge", new JsonObject { ["fields"] = fields.DeepClone() }));
        var edges = graph["edges"]!.AsArray(); edges.RemoveAt(edges.Count - 1);
        edges.Add((JsonNode)Edge("branch-input", "retrieve", "context", "condition", "context"));
        edges.Add((JsonNode)Edge("hit-gate", "condition", "hit", "answer", WorkflowBranchValues.ControlPort));
        edges.Add((JsonNode)Edge("default-gate", "condition", "else", "fallback", WorkflowBranchValues.ControlPort));
        edges.Add((JsonNode)Edge("answer-merge", "answer", "answer", "merge", "answer"));
        edges.Add((JsonNode)Edge("fallback-merge", "fallback", "fallback", "merge", "fallback"));
        edges.Add((JsonNode)Edge("answer-end", "merge", "answer", "end", "answer"));
        edges.Add((JsonNode)Edge("fallback-end", "merge", "fallback", "end", "fallback"));
        return graph;
    }

    /// <summary>为测试拼装只有服务端允许字段的节点，不携带 React Flow 临时状态。</summary>
    public static JsonObject Node(string id, string type, JsonObject data) => new()
    { ["id"] = id, ["type"] = type, ["data"] = data, ["position"] = new JsonObject { ["x"] = 200, ["y"] = 100 } };

    /// <summary>构造真正的命名数据边或专用控制边。</summary>
    public static JsonObject Edge(string id, string source, string output, string target, string input) => new()
    { ["id"] = id, ["source"] = source, ["sourceHandle"] = output, ["target"] = target, ["targetHandle"] = input };
}
