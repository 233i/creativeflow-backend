using System.Text.Json.Nodes;

namespace CreativeFlow.Application.Features.Workflow;

/// <summary>循环与批处理节点的声明式配置校验；子图仍使用同一类型化工作流协议。</summary>
public static class WorkflowLoopValues
{
    public const int MaxItems = 100;
    public const int MaxParallelism = 8;
    public const int MaxBatchSize = 50;
    public const int MaxBodyDepth = 2;

    /// <summary>验证输入字段、子流程图、并发和失败策略，避免运行时才发现无效配置。</summary>
    public static string? Validate(JsonObject data, string type, bool forExecution, int depth)
    {
        if (data.Any(pair => pair.Key is not ("title" or "fields" or "outputFields" or "body" or "maxItems" or "parallelism" or "batchSize" or "failurePolicy" or "retry")))
            return "unknown loop configuration";
        if (WorkflowV2Values.ValidateFields(data["outputFields"]) is { } outputError) return outputError;
        var outputFields = (JsonArray)data["outputFields"]!;
        if (outputFields.OfType<JsonObject>().FirstOrDefault(field => WorkflowV2Values.Text(field["key"]) == "results") is not { } results
            || WorkflowV2Values.Text(results["type"]) != "array"
            || outputFields.OfType<JsonObject>().FirstOrDefault(field => WorkflowV2Values.Text(field["key"]) == "errors") is not { } errors
            || WorkflowV2Values.Text(errors["type"]) != "array"
            || outputFields.OfType<JsonObject>().FirstOrDefault(field => WorkflowV2Values.Text(field["key"]) == "count") is not { } count
            || WorkflowV2Values.Text(count["type"]) != "number")
            return "loop outputFields must declare results, errors and count";
        if (data["body"] is not JsonObject body) return "loop body is required";
        if (depth >= MaxBodyDepth) return $"nested loop depth cannot exceed {MaxBodyDepth}";
        var bodyValidation = WorkflowV2GraphValidator.Validate(body, forExecution, depth: depth + 1);
        if (!bodyValidation.IsValid) return "loop body is invalid: " + string.Join("; ", bodyValidation.Errors);
        var bodyPlan = bodyValidation.Plan!;
        if (bodyPlan.Nodes.Any(node => node.Type is "knowledgeRetrieve" or "knowledgeAnswer" or "httpRequest"))
            return "loop body cannot contain knowledge or HTTP nodes; place those calls outside the loop or use an explicit batch API";
        if (bodyPlan.Nodes.Any(node => (node.Data["fields"] as JsonArray)?.OfType<JsonObject>().Any(field => WorkflowV2Values.Text(field["type"]) == "image") == true))
            return "loop body cannot contain image asset inputs; validate and fan out image work outside the loop";
        var start = bodyPlan.Nodes.Single(node => node.Type == "start");
        var requiredInput = type == "batch" ? "items" : "item";
        var field = ((JsonArray)start.Data["fields"]!).OfType<JsonObject>().FirstOrDefault(item => WorkflowV2Values.Text(item["key"]) == requiredInput);
        if (field is null || WorkflowV2Values.Text(field["type"]) != (type == "batch" ? "array" : "object"))
            return $"loop body start must declare an {requiredInput} input";
        if (Number(data["maxItems"]) is not { } maxItems || maxItems is < 1 or > MaxItems || maxItems != Math.Truncate(maxItems))
            return $"maxItems must be an integer from 1 to {MaxItems}";
        if (Number(data["parallelism"]) is not { } parallelism || parallelism is < 1 or > MaxParallelism || parallelism != Math.Truncate(parallelism))
            return $"parallelism must be an integer from 1 to {MaxParallelism}";
        if (type == "batch" && (Number(data["batchSize"]) is not { } batchSize || batchSize is < 1 or > MaxBatchSize || batchSize != Math.Truncate(batchSize)))
            return $"batchSize must be an integer from 1 to {MaxBatchSize}";
        var failurePolicy = Text(data["failurePolicy"]);
        if (failurePolicy is not ("failFast" or "continue")) return "failurePolicy must be failFast or continue";
        return null;
    }

    /// <summary>固定输出端口，结果按子流程结束节点字段逐项保存。</summary>
    public static JsonArray Outputs(JsonObject data) => (JsonArray)data["outputFields"]!;

    private static double? Number(JsonNode? node) => node is JsonValue value && value.GetValueKind() == System.Text.Json.JsonValueKind.Number
        && double.TryParse(value.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number)
        && double.IsFinite(number) ? number : null;

    private static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : string.Empty;
}
