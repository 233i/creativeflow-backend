using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CreativeFlow.Application.Features.Workflow;

/// <summary>新版图的有界类型化字段；不改变媒体图的 texts/images/videos 契约。</summary>
public static partial class WorkflowV2Values
{
    /// <summary>普通字段数据最大 1 MiB；按 UTF-8 内容计量，JSON 转义不重复占用正文额度。</summary>
    public const int MaxBytes = 1024 * 1024;
    /// <summary>节点信封容纳正文和一次提取副本，元数据另留 8 KiB，不提高 HTTP 传输的 1 MiB 上限。</summary>
    public const int MaxFieldsBytes = 2 * MaxBytes + 8192;
    /// <summary>每个节点最多声明十六个命名字段。</summary>
    public const int MaxFields = 16;
    /// <summary>不允许原型相关名称充当变量或对象字段。</summary>
    private static readonly HashSet<string> Reserved = ["__proto__", "prototype", "constructor"];
    /// <summary>变量使用稳定的 ASCII 标识，显示名称由 label 单独保存。</summary>
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,47}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();

    /// <summary>检查稳定字段名，拒绝危险的 JavaScript 原型属性。</summary>
    public static bool ValidKey(string key) => KeyPattern().IsMatch(key) && !Reserved.Contains(key);

    /// <summary>只读取真正的字符串，不把其他 JSON 值隐式转换为文本。</summary>
    public static string Text(JsonNode? value) => value is JsonValue item && item.TryGetValue<string>(out var text) ? text : "";

    /// <summary>读取数值配置，调用者先通过图校验确保范围和类型有效。</summary>
    public static double? Number(JsonNode? value) => value is JsonValue item && item.GetValueKind() == JsonValueKind.Number
        && double.TryParse(item.ToJsonString(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : null;

    /// <summary>限制 JSON 的总大小、深度、集合数量和原型字段，保留 null、布尔和数值的真实类型。</summary>
    public static bool IsBounded(JsonNode? value) => IsBounded(value, MaxBytes);

    /// <summary>模型输出保留原 32 KiB JSON/16000 字符限制，不随 HTTP 响应预算一起放宽。</summary>
    public static bool IsModelOutputBounded(JsonNode? value)
    {
        try { return Walk(value, 0, 16000) && Encoding.UTF8.GetByteCount(value?.ToJsonString() ?? "null") <= 32768; }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException) { return false; }
    }

    /// <summary>保留深度、集合及文本结构保护，允许受信任的领域类型使用其推导出的序列化上限。</summary>
    internal static bool IsBounded(JsonNode? value, int maxBytes)
    {
        try { return Walk(value, 0) && ContentBytes(value) <= maxBytes; }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException) { return false; }
    }

    /// <summary>计算内容字节，不把 JSON 的反斜线转义或信封分隔符重复计入正文。</summary>
    private static long ContentBytes(JsonNode? value) => value switch
    {
        JsonValue item when item.TryGetValue<string>(out var text) => Encoding.UTF8.GetByteCount(text),
        JsonObject obj => obj.Sum(pair => (long)Encoding.UTF8.GetByteCount(pair.Key) + ContentBytes(pair.Value)),
        JsonArray array => array.Sum(ContentBytes),
        _ => Encoding.UTF8.GetByteCount(value?.ToJsonString() ?? "null"),
    };

    /// <summary>递归检查有界 JSON；文本最多 1 MiB，数组最多一百项，对象最多三十二键。</summary>
    private static bool Walk(JsonNode? value, int depth, int maxText = MaxBytes)
    {
        if (depth > 8) return false;
        return value switch
        {
            null => true,
            JsonObject obj => obj.Count <= 32 && obj.All(pair => pair.Key.Length <= 100 && !Reserved.Contains(pair.Key) && Walk(pair.Value, depth + 1, maxText)),
            JsonArray array => array.Count <= 100 && array.All(item => Walk(item, depth + 1, maxText)),
            JsonValue item when item.TryGetValue<string>(out var text) => text.Length <= maxText && Encoding.UTF8.GetByteCount(text) <= MaxBytes,
            JsonValue item when item.TryGetValue<bool>(out _) => true,
            JsonValue item when Number(item) is not null => true,
            _ => false,
        };
    }

    /// <summary>检查字段定义、默认值和显式嵌套模式；旧对象端口未声明子字段时保持原来的 JSON 语义。</summary>
    public static string? ValidateFields(JsonNode? node, bool allowKnowledge = false, int depth = 0)
    {
        if (depth == 0 && (node is null || Encoding.UTF8.GetByteCount(node.ToJsonString()) > MaxBytes || !WorkflowFieldSchema.WithinBudget(node))) return "field schema exceeds size or complexity limits";
        if (node is not JsonArray fields || fields.Count is < 1 or > MaxFields) return "fields must contain 1–16 entries";
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in fields)
        {
            if (item is not JsonObject field || field.Any(pair => pair.Key is not ("key" or "label" or "type" or "required" or "default" or "maxLength" or "minimum" or "maximum" or "description" or "nullable" or "integer" or "properties" or "items" or "enum" or "minItems" or "maxItems"))) return "invalid field definition";
            var key = Text(field["key"]);
            if (!ValidKey(key) || !keys.Add(key)) return "field keys must be valid and unique";
            if (Text(field["label"]).Length > 100 || (field["label"] is not null && field["label"]!.GetValueKind() != JsonValueKind.String)) return "invalid field label";
            var knowledge = Text(field["type"]) is "contextChunks" or "answer";
            if (Text(field["type"]) is not ("string" or "number" or "boolean" or "object" or "array" or "image") && !(allowKnowledge && knowledge)) return "unsupported field type";
            if (knowledge && (field.ContainsKey("default") || field.ContainsKey("maxLength") || field.ContainsKey("minimum") || field.ContainsKey("maximum"))) return "knowledge fields require a real typed connection, not defaults";
            if (Text(field["type"]) == "image" && (field.ContainsKey("nullable") || field.ContainsKey("properties") || field.ContainsKey("items") || field.ContainsKey("enum") || field.ContainsKey("maxLength") || field.ContainsKey("minimum") || field.ContainsKey("maximum"))) return "image fields only support an assetId value";
            if (field["required"] is not JsonValue required || !required.TryGetValue<bool>(out _)) return "field required must be boolean";
            if (field.ContainsKey("maxLength") && (Number(field["maxLength"]) is not { } max || max < 1 || max > MaxBytes || max != Math.Truncate(max))) return "maxLength must be an integer between 1 and 1048576";
            if (field.ContainsKey("minimum") && Number(field["minimum"]) is null || field.ContainsKey("maximum") && Number(field["maximum"]) is null) return "invalid numeric constraint";
            if (Number(field["minimum"]) is { } min && Number(field["maximum"]) is { } upper && min > upper) return "minimum exceeds maximum";
            if (WorkflowFieldSchema.Validate(field, depth) is { } schemaError) return $"field '{key}': {schemaError}";
            if (field.ContainsKey("default") && !Matches(field, field["default"])) return $"invalid default for '{key}'";
        }
        return null;
    }

    /// <summary>检查实际字段类型与限制，不做字符串转数字、截断、填零或字段丢弃。</summary>
    public static bool Matches(JsonObject field, JsonNode? value)
    {
        if (value is null) return field["nullable"]?.GetValue<bool>() == true;
        if (Text(field["type"]) is "contextChunks" or "answer") return KnowledgeWorkflowValues.Matches(Text(field["type"]), value) && WorkflowFieldSchema.MatchesDetails(field, value);
        if (!IsBounded(value)) return false;
        if (!WorkflowFieldSchema.MatchesDetails(field, value)) return false;
        return Text(field["type"]) switch
        {
            "string" => value is JsonValue text && text.TryGetValue<string>(out var content) && content.Length <= (Number(field["maxLength"]) ?? MaxBytes),
            "number" => Number(value) is { } number && number >= (Number(field["minimum"]) ?? double.MinValue) && number <= (Number(field["maximum"]) ?? double.MaxValue),
            "boolean" => value is JsonValue boolean && boolean.TryGetValue<bool>(out _),
            "object" => value is JsonObject,
            "array" => value is JsonArray,
            "image" => value is JsonObject image && Guid.TryParse(Text(image["assetId"]), out var assetId) && assetId != Guid.Empty && image.Count == 1,
            _ => false,
        };
    }

    /// <summary>规范化开始节点输入；只补显式默认值，拒绝未知字段、缺少必填项和错误类型。</summary>
    public static string? Bind(JsonArray fields, JsonObject input, out JsonObject bound, bool useDefaults = true)
    {
        bound = [];
        if (!FieldsBounded(fields, input)) return "inputs exceed the JSON limits";
        var definitions = fields.OfType<JsonObject>().ToDictionary(field => Text(field["key"]), StringComparer.Ordinal);
        if (input.Any(pair => !definitions.ContainsKey(pair.Key))) return "inputs contain an unknown field";
        foreach (var (key, field) in definitions)
        {
            var present = input.TryGetPropertyValue(key, out var value);
            if (!present && useDefaults) present = field.TryGetPropertyValue("default", out value);
            if (!present)
            {
                if (field["required"]!.GetValue<bool>()) return $"input '{key}' is required";
                continue;
            }
            if (!Matches(field, value)) return $"input '{key}' has an invalid type or exceeds its limits";
            bound[key] = value?.DeepClone();
        }
        return FieldsBounded(fields, bound) ? null : "combined inputs exceed the JSON limits";
    }

    /// <summary>知识端口使用独立预算，普通字段受节点信封总额及每值 1 MiB 双重约束。</summary>
    private static bool FieldsBounded(JsonArray fields, JsonObject values)
    {
        if (!IsBounded(values, FieldsMaxBytes(fields))) return false;
        var knowledgeKeys = fields.OfType<JsonObject>().Where(field => Text(field["type"]) is "contextChunks" or "answer")
            .Select(field => Text(field["key"])).ToHashSet(StringComparer.Ordinal);
        if (knowledgeKeys.Count == 0) return true;
        var ordinary = new JsonObject();
        foreach (var (key, value) in values)
            if (!knowledgeKeys.Contains(key)) ordinary[key] = value?.DeepClone();
        return IsBounded(ordinary, MaxFieldsBytes);
    }

    /// <summary>根据已验证的端口声明计算输入/恢复信封容量，不修改普通字段的安全预算。</summary>
    private static int FieldsMaxBytes(JsonArray fields) => MaxFieldsBytes + Math.Min(MaxFields,
        fields.OfType<JsonObject>().Count(field => Text(field["type"]) is "contextChunks" or "answer")) * KnowledgeWorkflowValues.MaxValueBytes;

    /// <summary>构造稳定输出信封，媒体节点仍使用原有的 texts/images/videos 结构。</summary>
    public static JsonObject Output(JsonObject fields) => new() { ["schemaVersion"] = WorkflowGraphValidator.SchemaVersion, ["fields"] = fields.DeepClone() };

    /// <summary>验证恢复输出与节点声明相符，拒绝错误版本、缺字段、超限和额外数据。</summary>
    public static bool TryRestore(WorkflowPlanNode node, string? json, out JsonObject fields)
    {
        fields = [];
        if (json is null) return false;
        try
        {
            var definitions = node.Type == "end" ? (JsonArray)node.Data["fields"]! : WorkflowV2GraphValidator.OutputFields(node);
            if (Encoding.UTF8.GetByteCount(json) > 6L * FieldsMaxBytes(definitions) + 8192) return false;
            if (JsonNode.Parse(json) is not JsonObject envelope || envelope.Any(pair => pair.Key is not ("schemaVersion" or "fields" or "modelCall")) || Number(envelope["schemaVersion"]) != WorkflowGraphValidator.SchemaVersion || envelope["fields"] is not JsonObject candidate) return false;
            if (envelope.ContainsKey("modelCall") && (envelope["modelCall"] is not JsonObject trace || !IsBounded(trace) || Encoding.UTF8.GetByteCount(trace.ToJsonString()) > 8000)) return false;
            return Bind(definitions, candidate, out fields, useDefaults: false) is null && WorkflowBranchValues.ValidOutput(node, fields);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException) { return false; }
    }

    /// <summary>普通文本 LLM 的固定输出，已有图不设置输出模式时继续使用此契约。</summary>
    public static JsonArray TextOutputFields() => [new JsonObject { ["key"] = "text", ["type"] = "string", ["required"] = true, ["maxLength"] = 16000 }];
}
