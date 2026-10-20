using System.Text;
using System.Text.Json.Nodes;

namespace CreativeFlow.Application.Features.Workflow;

/// <summary>脚本节点的稳定配置与协议校验；脚本不共享模型、凭据或知识资源快照。</summary>
public static class WorkflowScriptValues
{
    /// <summary>脚本源码最大 32 KiB，避免把编排图变成任意文件上传通道。</summary>
    public const int MaxCodeBytes = 32 * 1024;

    /// <summary>脚本节点的公开超时上限；基础设施可以进一步收紧该值。</summary>
    public const int MaxTimeoutSeconds = 30;

    /// <summary>递归识别循环/批处理子图中的脚本，发布和排队开关不能被嵌套图绕过。</summary>
    public static bool ContainsScript(WorkflowPlan plan) => plan.Nodes.Any(node =>
        node.Type == "script" || node.Data["body"] is JsonObject body && ContainsScript(body));

    private static bool ContainsScript(JsonObject graph) => graph["nodes"] is JsonArray nodes && nodes.OfType<JsonObject>().Any(node =>
        WorkflowV2Values.Text(node["type"]) == "script"
        || node["data"] is JsonObject data && data["body"] is JsonObject body && ContainsScript(body));

    /// <summary>节点只允许保存脚本协议本身的配置，拒绝隐藏的网络、凭据或模型引用。</summary>
    public static string? Validate(JsonObject data)
    {
        if (data.Any(pair => pair.Key is not ("title" or "fields" or "outputFields" or "language" or "code" or "timeoutSeconds" or "retry")))
            return "script node contains unknown configuration fields";
        if (WorkflowV2Values.ValidateFields(data["fields"]) is { } fieldsError)
            return fieldsError;
        if (ValidateScriptFields(data["fields"]) is { } inputError)
            return inputError;
        if (data["outputFields"] is not JsonArray outputFields)
            return "script outputFields are required";
        if (WorkflowV2Values.ValidateFields(outputFields) is { } outputError)
            return $"outputFields: {outputError}";
        if (ValidateScriptFields(outputFields) is { } outputFieldError)
            return $"outputFields: {outputFieldError}";

        var language = WorkflowV2Values.Text(data["language"]);
        if (language is not ("python" or "javascript")) return "language must be python or javascript";
        var code = WorkflowV2Values.Text(data["code"]);
        if (code.Length == 0 || Encoding.UTF8.GetByteCount(code) > MaxCodeBytes || code.Contains('\0'))
            return "code must be non-empty UTF-8 text no larger than 32 KiB";
        if (WorkflowV2Values.Number(data["timeoutSeconds"]) is not { } timeout
            || timeout < 1 || timeout > MaxTimeoutSeconds || timeout != Math.Truncate(timeout))
            return "timeoutSeconds must be an integer between 1 and 30";
        return null;
    }

    /// <summary>脚本只处理普通 JSON；图片、知识证据和答案对象必须交给专用节点。</summary>
    private static string? ValidateScriptFields(JsonNode? value)
    {
        if (value is not JsonArray fields) return "script fields are invalid";
        foreach (var field in fields.OfType<JsonObject>())
        {
            if (WorkflowV2Values.Text(field["type"]) is "image" or "contextChunks" or "answer")
                return "script fields cannot use image or knowledge values";
            if (field["properties"] is JsonArray properties && ValidateScriptFields(properties) is { } nestedObjectError)
                return nestedObjectError;
            if (field["items"] is JsonObject item && ValidateScriptFields(new JsonArray(item)) is { } nestedArrayError)
                return nestedArrayError;
        }
        return null;
    }
}
