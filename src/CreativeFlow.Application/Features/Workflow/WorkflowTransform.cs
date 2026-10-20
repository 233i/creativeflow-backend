using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Ai;
using static CreativeFlow.Application.Features.Workflow.WorkflowV2Values;

namespace CreativeFlow.Application.Features.Workflow;

/// <summary>有界声明式数据转换，不执行脚本、网络请求、文件操作或模型调用。</summary>
public static class WorkflowTransform
{
    /// <summary>验证赋值目标和表达式树；每个输出恰好有一个表达式。</summary>
    public static string? Validate(JsonObject data)
    {
        if (data.Any(pair => pair.Key is not ("title" or "fields" or "outputFields" or "assignments"))) return "unknown transform configuration";
        if (ValidateFields(data["outputFields"]) is { } error) return error;
        if (data["assignments"] is not JsonArray assignments || assignments.Count is < 1 or > MaxFields) return "assignments require 1–16 entries";
        var fields = (JsonArray)data["outputFields"]!;
        var keys = new HashSet<string>(fields.OfType<JsonObject>().Select(field => Text(field["key"])));
        var inputs = (data["fields"] as JsonArray)?.OfType<JsonObject>().Select(field => Text(field["key"])).ToHashSet() ?? [];
        var budget = 256;
        foreach (var item in assignments)
        {
            if (item is not JsonObject row || row.Count != 2 || !keys.Remove(Text(row["key"]))) return "each output requires one unique assignment";
            if (ValidateExpression(row["expression"], inputs, 0, ref budget) is { } expressionError) return expressionError;
        }
        return keys.Count == 0 ? null : "every output requires an assignment";
    }

    /// <summary>限制表达式深度、参数数量及安全属性路径，阻止原型字段和不受控迭代。</summary>
    private static string? ValidateExpression(JsonNode? value, HashSet<string> inputs, int depth, ref int budget)
    {
        if (--budget < 0 || depth > 8 || value is not JsonObject expression) return "expression exceeds complexity limits";
        var op = Text(expression["op"]);
        var allowed = op switch { "input" => new[] { "op", "input", "path" }, "constant" => ["op", "value"], "object" => ["op", "properties"],
            "get" => ["op", "args", "path"], "concat" or "split" or "join" => ["op", "args", "separator"], _ => ["op", "args"] };
        if (expression.Any(pair => !allowed.Contains(pair.Key))) return "unknown expression property";
        foreach (var name in new[] { "input", "path", "separator" }) if (expression.ContainsKey(name) && expression[name]?.GetValueKind() != JsonValueKind.String) return $"{name} must be a string";
        if (op == "input") return inputs.Contains(Text(expression["input"])) && ValidPath(Text(expression["path"])) ? null : "invalid input or property path";
        if (op == "constant") return expression.ContainsKey("value") && IsBounded(expression["value"]) ? null : "invalid constant";
        if (op == "object")
        {
            if (expression["properties"] is not JsonObject properties || properties.Count > 16 || properties.Any(pair => !ValidKey(pair.Key))) return "invalid object properties";
            foreach (var pair in properties) if (ValidateExpression(pair.Value, inputs, depth + 1, ref budget) is { } error) return error;
            return null;
        }
        if (expression["args"] is not JsonArray args || args.Count > 32) return "expression requires bounded args";
        var validCount = op switch
        {
            "array" => true, "concat" or "coalesce" or "add" => args.Count >= 1,
            "get" or "parseJson" or "stringify" or "toString" or "toNumber" or "toBoolean" or "trim" or "upper" or "lower" or "split" or "join" or "length" => args.Count == 1,
            _ => false,
        };
        if (!validCount || !ValidPath(Text(expression["path"])) || Text(expression["separator"]).Length > 1000) return "unsupported operation or invalid arguments";
        foreach (var arg in args) if (ValidateExpression(arg, inputs, depth + 1, ref budget) is { } error) return error;
        return null;
    }

    /// <summary>JSON Pointer 路径显式处理对象键和数组下标，拒绝危险属性及过深路径。</summary>
    private static bool ValidPath(string path) => path.Length <= 512 && (path.Length == 0 || path.StartsWith('/'))
        && path.Split('/').Length <= 9 && path.Split('/').Skip(1).All(part => part is not ("__proto__" or "prototype" or "constructor") && !part.Contains('~') || ValidEscapedPathPart(part));

    /// <summary>只接受 JSON Pointer 的标准转义，不允许转义绕过危险键检查。</summary>
    private static bool ValidEscapedPathPart(string part)
    {
        for (var index = 0; index < part.Length; index++) if (part[index] == '~' && (++index >= part.Length || part[index] is not ('0' or '1'))) return false;
        return part.Replace("~1", "/").Replace("~0", "~") is not ("__proto__" or "prototype" or "constructor");
    }

    /// <summary>执行所有输出赋值并再次校验实际类型；错误只包含字段名，不泄露输入内容。</summary>
    public static JsonObject Execute(JsonObject data, JsonObject inputs)
    {
        var output = new JsonObject(); var budget = 512;
        foreach (var row in ((JsonArray)data["assignments"]!).OfType<JsonObject>())
        {
            var key = Text(row["key"]);
            try { output[key] = Evaluate((JsonObject)row["expression"]!, inputs, ref budget); }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or JsonException or FormatException or OverflowException)
            { throw new WorkflowV2Exception($"变量处理字段 '{key}' 失败：{(error is WorkflowTransformException ? error.Message : "输入类型或格式不符合操作要求")}。"); }
        }
        if (Bind((JsonArray)data["outputFields"]!, output, out var bound, useDefaults: false) is { } failure) throw new WorkflowV2Exception($"变量处理输出校验失败：{failure}");
        return bound;
    }

    /// <summary>递归求值，每一步消耗预算并限制中间值大小，避免通过拼接制造巨型对象。</summary>
    private static JsonNode? Evaluate(JsonObject expression, JsonObject inputs, ref int budget)
    {
        // 每个中间值独立受限；coalesce 短路可避免对缺失字段执行转换，其他运算不隐式容错。
        if (--budget < 0) throw new WorkflowTransformException("运算预算耗尽");
        var op = Text(expression["op"]); JsonNode? result;
        if (op == "constant") result = expression["value"]?.DeepClone();
        else if (op == "input") result = ReadPath(inputs[Text(expression["input"])], Text(expression["path"]))?.DeepClone();
        else if (op == "object")
        {
            var obj = new JsonObject(); foreach (var pair in (JsonObject)expression["properties"]!) obj[pair.Key] = Evaluate((JsonObject)pair.Value!, inputs, ref budget); result = obj;
        }
        else
        {
            var args = new List<JsonNode?>();
            foreach (var arg in ((JsonArray)expression["args"]!).OfType<JsonObject>())
            {
                args.Add(Evaluate(arg, inputs, ref budget));
                if (op == "coalesce" && args[^1] is not null) break;
            }
            var first = args.FirstOrDefault(); var separator = Text(expression["separator"]);
            result = op switch
            {
                "array" => new JsonArray(args.ToArray()), "coalesce" => args.FirstOrDefault(value => value is not null),
                "get" => ReadPath(first, Text(expression["path"]))?.DeepClone(),
                "concat" => JsonValue.Create(string.Join(separator, args.Select(String))),
                "parseJson" => JsonNode.Parse(String(first), documentOptions: new JsonDocumentOptions { MaxDepth = 16 }),
                "stringify" => JsonValue.Create(first?.ToJsonString() ?? "null"),
                "toString" => JsonValue.Create(first is JsonValue value && value.TryGetValue<string>(out var text) ? text : first?.ToJsonString() ?? "null"),
                "toNumber" => JsonValue.Create(Number(first) ?? (double.TryParse(String(first), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : throw new WorkflowTransformException("无法转换为有限数字"))),
                "toBoolean" => JsonValue.Create(first is JsonValue boolean && boolean.TryGetValue<bool>(out var flag) ? flag : String(first) switch { "true" => true, "false" => false, _ => throw new WorkflowTransformException("布尔转换只接受 true 或 false") }),
                "trim" => JsonValue.Create(String(first).Trim()), "upper" => JsonValue.Create(String(first).ToUpperInvariant()), "lower" => JsonValue.Create(String(first).ToLowerInvariant()),
                "split" => separator.Length == 0 ? throw new WorkflowTransformException("分隔符不能为空") : new JsonArray(String(first).Split(separator).Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
                "join" => first is JsonArray array ? JsonValue.Create(string.Join(separator, array.Select(String))) : throw new WorkflowTransformException("连接操作需要字符串数组"),
                "length" => JsonValue.Create(first switch { JsonArray array => array.Count, JsonObject obj => obj.Count, _ => String(first).Length }),
                "add" => JsonValue.Create(args.Sum(value => Number(value) ?? throw new WorkflowTransformException("加法只接受数字"))),
                _ => throw new WorkflowTransformException("不支持的操作"),
            };
        }
        if (!IsBounded(result)) throw new WorkflowTransformException("中间结果超过大小或深度限制");
        return result;
    }

    /// <summary>字符串操作不隐式接受数字或对象；调用者可先使用显式转换操作。</summary>
    private static string String(JsonNode? value) => value is JsonValue item && item.TryGetValue<string>(out var text) ? text : throw new WorkflowTransformException("操作需要字符串");

    /// <summary>路径缺失返回 null，便于显式 coalesce；非法数组下标不会抛出内容相关错误。</summary>
    private static JsonNode? ReadPath(JsonNode? value, string path)
    {
        foreach (var encoded in path.Split('/').Skip(1))
        {
            var part = encoded.Replace("~1", "/").Replace("~0", "~");
            value = value switch { JsonObject obj => obj[part], JsonArray array when int.TryParse(part, out var index) && index >= 0 && index < array.Count => array[index], _ => null };
        }
        return value;
    }

    /// <summary>只携带可展示的转换错误，不包含原始业务数据。</summary>
    private sealed class WorkflowTransformException(string message) : InvalidOperationException(message);
}
