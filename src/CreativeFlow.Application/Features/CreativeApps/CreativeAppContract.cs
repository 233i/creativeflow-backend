using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Features.Workflow;

namespace CreativeFlow.Application.Features.CreativeApps;

/// <summary>公共创作只公开表单与结果，不把内部图、绑定目标或模型凭据发送给客户。</summary>
public static class CreativeAppContract
{
    /// <summary>投影客户可见的应用资料，移除节点绑定和模型配置。</summary>
    public static JsonObject Describe(CreativeAppRelease release)
    {
        var metadata = release.Metadata;
        var fields = JsonNode.Parse(release.InputSchemaJson)!.AsArray();
        foreach (var field in fields.OfType<JsonObject>()) field.Remove("target");
        return new JsonObject
        {
            ["id"] = release.WorkflowId.ToString(),
            ["releaseId"] = release.Id.ToString(),
            ["version"] = release.Version,
            ["name"] = metadata["name"]?.DeepClone(),
            ["description"] = metadata["description"]?.DeepClone(),
            ["category"] = metadata["category"]?.DeepClone(),
            ["coverUrl"] = metadata["coverAssetId"] is null ? null : $"/api/v1/creative-apps/{release.WorkflowId}/cover",
            ["inputs"] = fields,
            ["outputs"] = new JsonArray(JsonNode.Parse(release.OutputSchemaJson)!.AsArray().OfType<JsonObject>()
                .Select(item => (JsonNode)new JsonObject { ["key"] = item["key"]?.DeepClone(), ["label"] = item["label"]?.DeepClone(), ["type"] = item["type"]?.DeepClone() }).ToArray()),
            ["dailyLimit"] = release.DailyLimit,
            ["userConcurrency"] = release.UserConcurrency,
            ["publishedAt"] = release.CreatedAt.ToString("O"),
        };
    }

    /// <summary>摘要包含指定版本和规范化字段顺序；同一键的不同参数不会生成第二次。</summary>
    public static string Fingerprint(Guid releaseId, JsonObject inputs) => HttpRequestDefinition.Digest(
        new JsonObject { ["releaseId"] = releaseId.ToString(), ["inputs"] = inputs.DeepClone() });

    /// <summary>安全读取 JSON 字符串，类型不符时返回空字符串。</summary>
    public static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
    /// <summary>读取整数配置，未提供或类型不符时使用默认值。</summary>
    public static int Number(JsonNode? node, int fallback) => node is JsonValue value && value.TryGetValue<int>(out var number) ? number : fallback;
    /// <summary>只将 JSON 布尔真值视为开启。</summary>
    public static bool Flag(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;
    /// <summary>安全读取 JSON 字符串，类型不符时返回空字符串。</summary>
    public static Guid? AssetId(JsonNode? node) => Guid.TryParse(Text(node).Replace("/api/v1/canvas/assets/", "", StringComparison.Ordinal), out var id) && id != Guid.Empty ? id : null;
}

/// <summary>可直接交付调用者的验证错误，不包含图正文或服务商响应。</summary>
public sealed class CreativeAppException(string code, string message, int status = 400) : Exception(message)
{
    /// <summary>机器可读的业务校验错误码。</summary>
    public string Code { get; } = code;
    /// <summary>该错误对应的 HTTP 状态码。</summary>
    public int Status { get; } = status;
}
