using System.Text.Json;
using System.Text.Json.Nodes;

namespace CreativeFlow.Api.Http.Handlers;

/// <summary>从运行时固定图快照解析公开节点名称，不读取草稿或内部模型配置。</summary>
internal static class WorkflowNodeDisplayNames
{
    /// <summary>与画布历史图保持一致的内置节点名称，用户标题仍优先于该回退。</summary>
    private static readonly IReadOnlyDictionary<string, string> Defaults = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["start"] = "开始", ["end"] = "结束", ["text"] = "文本输入", ["image"] = "图片输入",
        ["llm"] = "LLM", ["knowledgeRetrieve"] = "知识检索", ["knowledgeAnswer"] = "知识问答",
        ["parameterExtract"] = "参数提取", ["intentClassify"] = "意图分类", ["transform"] = "变量处理", ["script"] = "脚本",
        ["condition"] = "条件分支", ["merge"] = "结果汇合", ["httpRequest"] = "HTTP 请求",
        ["imageGenerate"] = "图片生成", ["videoGenerate"] = "视频生成", ["videoExtend"] = "视频延长",
        ["videoFrame"] = "提取尾帧", ["imageOutput"] = "图片结果", ["videoOutput"] = "视频结果", ["output"] = "输出",
    };

    /// <summary>构造节点 ID 到显示名称的快照映射；缺失或损坏的图不会阻断状态查询。</summary>
    internal static IReadOnlyDictionary<string, string> FromGraph(string graphJson)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (JsonNode.Parse(graphJson)?["nodes"] is not JsonArray nodes) return result;
            foreach (var node in nodes.OfType<JsonObject>())
            {
                var id = Text(node["id"]);
                if (id.Length == 0) continue;
                var type = Text(node["type"]);
                result[id] = First(node["data"]?["title"], node["data"]?["label"], node["name"], Defaults.GetValueOrDefault(type, type), id);
            }
        }
        catch (JsonException)
        {
            // 节点日志仍可使用 node_type 和 node_id 的安全回退。
        }
        return result;
    }

    /// <summary>从固定图快照获取节点名称，优先使用用户配置标题，再回退节点类型和 ID。</summary>
    internal static string Resolve(string graphJson, string nodeId, string nodeType) =>
        FromGraph(graphJson).GetValueOrDefault(nodeId) ?? First(null, null, null, Defaults.GetValueOrDefault(nodeType, nodeType), nodeId);

    /// <summary>公开名称只取短文本，避免把任意图字段或模型提示词带入事件。</summary>
    private static string First(JsonNode? title, JsonNode? label, JsonNode? name, JsonNode? type, string fallback)
    {
        foreach (var value in new[] { title, label, name, type })
        {
            var text = Text(value);
            if (text.Length is > 0 and <= 200) return text;
        }
        return fallback.Length <= 200 ? fallback : fallback[..200];
    }

    /// <summary>仅接受 JSON 字符串，类型异常按缺失处理。</summary>
    private static string Text(JsonNode? value) =>
        value is JsonValue json && json.TryGetValue<string>(out var text) ? text.Trim() : string.Empty;
}
