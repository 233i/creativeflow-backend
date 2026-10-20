using System.Text.Json.Nodes;

namespace CreativeFlow.Application.Features.Workflow;

/// <summary>按稳定节点词汇编译工作流；所有图都使用同一个公开 schema。</summary>
public static class WorkflowGraphCompiler
{
    /// <summary>稳定图中是否包含类型化数据节点。节点类别不是协议版本。</summary>
    public static bool IsTypedGraph(JsonNode? graph) => graph is JsonObject root
        && root["nodes"] is JsonArray nodes
        && nodes.OfType<JsonObject>().Any(node => node["type"] is JsonValue value
            && value.TryGetValue<string>(out var type)
            && type is "start" or "llm" or "end" or "knowledgeRetrieve" or "knowledgeAnswer"
                or "condition" or "merge" or "httpRequest" or "parameterExtract" or "intentClassify" or "transform" or "script" or "loop" or "batch");

    /// <summary>分派图编译，不升级图、不改写输入或拓扑顺序。</summary>
    public static WorkflowValidationResult Validate(JsonNode? graph, bool forExecution = true) => IsTypedGraph(graph)
        ? WorkflowV2GraphValidator.Validate(graph, forExecution)
        : WorkflowGraphValidator.Validate(graph);
}
