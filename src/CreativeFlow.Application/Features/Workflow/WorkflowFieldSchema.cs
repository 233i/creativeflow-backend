using System.Text.Json.Nodes;
using static CreativeFlow.Application.Features.Workflow.WorkflowV2Values;

namespace CreativeFlow.Application.Features.Workflow;

/// <summary>有界字段模式的递归校验与 JSON Schema 转换；不解析远程引用或执行用户代码。</summary>
public static class WorkflowFieldSchema
{
    /// <summary>字段模式最多包含 128 个声明，避免宽且深的递归模式消耗无界校验时间。</summary>
    public static bool WithinBudget(JsonNode node)
    {
        var remaining = 128;
        /// <summary>只遍历模式声明而非枚举值，有限预算防止宽深模式递归膨胀。</summary>
        bool Walk(JsonNode? value, int depth)
        {
            if (--remaining < 0 || depth > 12) return false;
            return value switch
            {
                JsonArray fields => fields.All(field => Walk(field, depth + 1)),
                JsonObject field => (!field.ContainsKey("properties") || Walk(field["properties"], depth + 1)) && (!field.ContainsKey("items") || Walk(field["items"], depth + 1)),
                _ => true,
            };
        }
        return Walk(node, 0);
    }

    /// <summary>验证嵌套字段、数组元素及枚举，保留未声明子字段的旧 JSON 端口。</summary>
    public static string? Validate(JsonObject field, int depth = 0)
    {
        if (depth > 5) return "field schema exceeds five nested levels";
        var type = Text(field["type"]);
        if (field["description"] is not null && (field["description"]!.GetValueKind() != System.Text.Json.JsonValueKind.String || Text(field["description"]).Length > 1000)) return "invalid field description";
        foreach (var key in new[] { "nullable", "integer" })
            if (field.ContainsKey(key) && (field[key] is not JsonValue flag || !flag.TryGetValue<bool>(out _))) return $"{key} must be boolean";
        if (field.ContainsKey("integer") && type != "number") return "integer applies only to numbers";
        if (field.ContainsKey("nullable") && type is "contextChunks" or "answer") return "knowledge fields cannot be nullable";
        if (field.ContainsKey("properties"))
        {
            if (type != "object" || field["properties"] is not JsonArray properties || properties.Count > MaxFields) return "properties require an object and at most 16 fields";
            if (properties.Count > 0 && ValidateFields(properties, depth: depth + 1) is { } error) return error;
        }
        if (field.ContainsKey("items"))
        {
            if (type != "array" || field["items"] is not JsonObject item) return "items require an array element schema";
            var wrapped = (JsonObject)item.DeepClone(); wrapped["key"] = "item"; wrapped["required"] = true;
            if (ValidateFields(new JsonArray(wrapped), depth: depth + 1) is { } error) return error;
        }
        foreach (var key in new[] { "minItems", "maxItems" })
            if (field.ContainsKey(key) && (type != "array" || Number(field[key]) is not { } n || n < 0 || n > 100 || n != Math.Truncate(n))) return $"{key} must be 0–100 for arrays";
        if (Number(field["minItems"]) is { } min && Number(field["maxItems"]) is { } max && min > max) return "minItems exceeds maxItems";
        if (field.ContainsKey("enum"))
        {
            if (type is not ("string" or "number" or "boolean") || field["enum"] is not JsonArray choices || choices.Count is < 1 or > 32) return "enum requires 1–32 primitive values";
            var without = (JsonObject)field.DeepClone(); without.Remove("enum");
            if (choices.Any(choice => !Matches(without, choice)) || choices.Select(choice => choice?.ToJsonString()).Distinct().Count() != choices.Count) return "enum values must be valid and unique";
        }
        return null;
    }

    /// <summary>在基本类型检查之外验证嵌套对象、数组、枚举和整数约束。</summary>
    public static bool MatchesDetails(JsonObject field, JsonNode? value)
    {
        if (value is null) return field["nullable"]?.GetValue<bool>() == true;
        if (field["enum"] is JsonArray choices && !choices.Any(choice => JsonNode.DeepEquals(choice, value))) return false;
        if (field["integer"]?.GetValue<bool>() == true && Number(value) is { } n && n != Math.Truncate(n)) return false;
        if (value is JsonObject obj && field["properties"] is JsonArray properties && Bind(properties, obj, out _, useDefaults: false) is not null) return false;
        if (value is JsonArray array)
        {
            if (array.Count < (Number(field["minItems"]) ?? 0) || array.Count > (Number(field["maxItems"]) ?? 100)) return false;
            if (field["items"] is JsonObject items && array.Any(item => !Matches(items, item))) return false;
        }
        return true;
    }

    /// <summary>构造闭合对象模式；原生严格模式把可选字段表示为必填且可空，解析时再去除占位 null。</summary>
    public static JsonObject ObjectSchema(JsonArray fields, bool strict = false) => new()
    {
        ["type"] = "object", ["additionalProperties"] = false,
        ["properties"] = new JsonObject(fields.OfType<JsonObject>().Select(field => new KeyValuePair<string, JsonNode?>(Text(field["key"]), ValueSchema(field, strict)))),
        ["required"] = new JsonArray(fields.OfType<JsonObject>().Where(field => strict || field["required"]?.GetValue<bool>() == true)
            .Select(field => (JsonNode?)JsonValue.Create(Text(field["key"]))).ToArray()),
    };

    /// <summary>转换本地支持的约束；未声明结构的旧对象不允许假装满足原生严格模式。</summary>
    private static JsonObject ValueSchema(JsonObject field, bool strict)
    {
        var type = Text(field["type"]);
        JsonObject schema;
        if (type == "object")
        {
            if (field["properties"] is JsonArray properties) schema = ObjectSchema(properties, strict);
            else if (strict) throw new ArgumentException("原生 JSON Schema 的对象字段必须声明子字段。");
            else schema = new() { ["type"] = "object" };
        }
        else if (type == "array")
        {
            if (strict && field["items"] is not JsonObject) throw new ArgumentException("原生 JSON Schema 的数组字段必须声明元素类型。");
            schema = new() { ["type"] = "array", ["items"] = field["items"] is JsonObject item ? ValueSchema(item, strict) : new JsonObject() };
        }
        else schema = new() { ["type"] = field["integer"]?.GetValue<bool>() == true ? "integer" : type };
        var nullable = field["nullable"]?.GetValue<bool>() == true || strict && field["required"]?.GetValue<bool>() == false;
        if (nullable) schema["type"] = new JsonArray(Text(schema["type"]), "null");
        foreach (var key in new[] { "description", "enum", "maxLength", "minimum", "maximum", "minItems", "maxItems" })
            if (field.TryGetPropertyValue(key, out var value)) schema[key] = value?.DeepClone();
        if (nullable && schema["enum"] is JsonArray values && !values.Any(value => value is null)) values.Add((JsonNode?)null);
        return schema;
    }

    /// <summary>仅规范化原生严格模式为可选字段返回的 null；不转换字符串、数字或删掉未知属性。</summary>
    public static void NormalizeOptionalNulls(JsonArray fields, JsonObject values)
    {
        foreach (var field in fields.OfType<JsonObject>())
        {
            var key = Text(field["key"]);
            if (!values.TryGetPropertyValue(key, out var value)) continue;
            if (value is null && field["required"]?.GetValue<bool>() == false && field["nullable"]?.GetValue<bool>() != true) { values.Remove(key); continue; }
            NormalizeValue(field, value);
        }
    }

    /// <summary>向内遍历对象和数组，保持原生可选字段的同一规范化规则。</summary>
    private static void NormalizeValue(JsonObject field, JsonNode? value)
    {
        if (field["properties"] is JsonArray properties && value is JsonObject obj) NormalizeOptionalNulls(properties, obj);
        if (field["items"] is JsonObject item && value is JsonArray array) foreach (var element in array) NormalizeValue(item, element);
    }
}
