using System.Text.Json.Nodes;

namespace CreativeFlow.Application.Features.Workflow;

/// <summary>媒体与通用节点共用的执行结果；保留已有持久化输出格式。</summary>
/// <param name="Texts">节点输出携带的文本集合。</param>
/// <param name="Images">图片内容或引用地址集合，元素类型由所属契约定义。</param>
/// <param name="Videos">节点输出携带的视频引用集合。</param>
public sealed record WorkflowNodeValue(
    IReadOnlyList<string> Texts,
    IReadOnlyList<string> Images,
    IReadOnlyList<string> Videos)
{
    /// <summary>仅新版节点使用的类型化字段；旧节点仍输出原有三个媒体数组。</summary>
    public JsonObject? Fields { get; init; }

    /// <summary>按新版声明复验恢复值，不把 v1 媒体输出隐式转换为新版字段。</summary>
    public static bool TryRestoreV2(WorkflowPlanNode node, string? json, out WorkflowNodeValue value)
    {
        var ok = WorkflowV2Values.TryRestore(node, json, out var fields);
        value = new WorkflowNodeValue([], [], []) { Fields = fields };
        return ok;
    }
    /// <summary>构造仅携带文本输出的节点值。</summary>
    public static WorkflowNodeValue FromText(string value) => new([value], [], []);
    /// <summary>构造仅携带图片引用的节点值。</summary>
    public static WorkflowNodeValue FromImage(string value) => new([], [value], []);
    /// <summary>构造仅携带视频引用的节点值。</summary>
    public static WorkflowNodeValue FromVideo(string value) => new([], [], [value]);

    /// <summary>恢复现有 texts/images/videos 输出数组，拒绝缺失字段或损坏值。</summary>
    public static bool TryParse(string? json, out WorkflowNodeValue value)
    {
        value = new WorkflowNodeValue([], [], []);
        if (json is null) return false;

        try
        {
            if (JsonNode.Parse(json) is not JsonObject output
                || !TryReadArray(output["texts"], out var texts)
                || !TryReadArray(output["images"], out var images)
                || !TryReadArray(output["videos"], out var videos))
            {
                return false;
            }

            value = new WorkflowNodeValue(texts, images, videos);
            return true;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    /// <summary>将节点值编码为既有 texts/images/videos 输出格式。</summary>
    public JsonObject ToJson() => Fields is not null ? WorkflowV2Values.Output(Fields) : new()
    {
        ["texts"] = ToArray(Texts),
        ["images"] = ToArray(Images),
        ["videos"] = ToArray(Videos),
    };

    /// <summary>把节点输出集合编码为 JSON 字符串数组。</summary>
    private static JsonArray ToArray(IReadOnlyList<string> values)
    {
        var result = new JsonArray();
        foreach (var value in values) result.Add((JsonNode?)JsonValue.Create(value));
        return result;
    }

    /// <summary>读取完整的节点输出字符串数组，类型不符时拒绝恢复。</summary>
    private static bool TryReadArray(JsonNode? node, out IReadOnlyList<string> values)
    {
        values = [];
        if (node is not JsonArray array) return false;

        var result = new List<string>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonValue jsonValue
                || !jsonValue.TryGetValue<string>(out var text))
            {
                return false;
            }
            result.Add(text);
        }

        values = result;
        return true;
    }
}
