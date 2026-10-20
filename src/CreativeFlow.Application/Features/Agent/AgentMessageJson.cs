using System.Text.Json;
using System.Text.Json.Nodes;

namespace CreativeFlow.Application.Features.Agent;

/// <summary>集中保留工具载荷和历史字段的既有 JSON 读取规则。</summary>
internal static class AgentMessageJson
{
    /// <summary>读取工具输入 JSON，保持无效内容的既有回退。</summary>
    internal static JsonNode ToNode(string json)
    {
        try
        {
            return JsonNode.Parse(json) ?? JsonValue.Create(json)!;
        }
        catch (JsonException)
        {
            return JsonValue.Create(json)!;
        }
    }

    /// <summary>从当前输入读取所需字段，保留缺失值处理。</summary>
    internal static string? Read(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

}
