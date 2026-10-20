using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Ai;
using static CreativeFlow.Application.Features.Workflow.WorkflowV2Values;

namespace CreativeFlow.Application.Features.Workflow;

/// <summary>条件配置和确定性判断；只读取已绑定字段，不执行脚本或改写知识证据。</summary>
public static class WorkflowBranchValues
{
    /// <summary>执行条件的专用输入端口，不可能与有效业务字段重名。</summary>
    public const string ControlPort = "$flow";
    /// <summary>所有显式条件均不满足时选择的固定默认出口。</summary>
    public const string DefaultBranch = "else";

    /// <summary>条件和单选意图分类都生成互斥执行路由。</summary>
    public static bool IsBranch(string type) => type is "condition" or "intentClassify";

    /// <summary>列出从上到下判断的命名出口及默认出口，不把条件表达式交给客户端执行。</summary>
    public static IReadOnlyList<string> Branches(JsonObject data) =>
        ((JsonArray)(data["intents"] ?? data["cases"])!).OfType<JsonObject>().Select(item => Text(item["key"])).Append(DefaultBranch).ToArray();

    /// <summary>条件输出只保存一个稳定的出口标识，用于恢复、显示和下游执行条件。</summary>
    public static JsonArray Outputs() => [new JsonObject { ["key"] = "route", ["type"] = "string", ["required"] = true }];

    /// <summary>校验有界 IF/ELSE IF、每组 AND/OR 和操作数类型；知识对象只开放受限谓词。</summary>
    public static string? Validate(JsonObject data)
    {
        if (data.Any(pair => pair.Key is not ("title" or "fields" or "cases"))) return "condition has unknown configuration";
        if (ValidateFields(data["fields"], allowKnowledge: true) is { } fieldsError) return fieldsError;
        if (data["cases"] is not JsonArray cases || cases.Count is < 1 or > 8 || !IsBounded(cases)) return "condition requires 1–8 bounded cases";
        var fields = ((JsonArray)data["fields"]!).OfType<JsonObject>().ToDictionary(field => Text(field["key"]));
        var keys = new HashSet<string>(StringComparer.Ordinal) { DefaultBranch, "route" };
        foreach (var item in cases)
        {
            if (item is not JsonObject branch || branch.Any(pair => pair.Key is not ("key" or "label" or "match" or "conditions"))
                || !ValidKey(Text(branch["key"])) || !keys.Add(Text(branch["key"]))) return "condition case keys must be unique; else and route are reserved";
            if (branch["label"] is not null && (branch["label"]!.GetValueKind() != System.Text.Json.JsonValueKind.String || Text(branch["label"]).Length > 100)) return "invalid case label";
            if (Text(branch["match"]) is not ("all" or "any") || branch["conditions"] is not JsonArray rules || rules.Count is < 1 or > 16)
                return "each case requires all/any and 1–16 conditions";
            foreach (var rule in rules)
            {
                if (rule is not JsonObject condition || condition.Any(pair => pair.Key is not ("input" or "operator" or "value"))
                    || !fields.TryGetValue(Text(condition["input"]), out var field)) return "condition must reference a declared input";
                if (ValidateRule(Text(field["type"]), condition) is { } ruleError) return ruleError;
            }
        }
        return null;
    }

    /// <summary>根据输入类型限制比较操作，不以 JSON 字符串比较证据、布尔或数字。</summary>
    private static string? ValidateRule(string type, JsonObject condition)
    {
        var op = Text(condition["operator"]);
        var unary = op is "isPresent" or "isMissing" or "isTrue" or "isFalse" or "isEmpty" or "notEmpty" or "hasHits" or "noHits" or "isGrounded" or "notGrounded";
        var valid = op switch
        {
            "isPresent" or "isMissing" => true,
            "eq" or "ne" => type is "string" or "number" or "boolean",
            "gt" or "gte" or "lt" or "lte" => type == "number",
            "contains" or "startsWith" or "endsWith" => type == "string",
            "isTrue" or "isFalse" => type == "boolean",
            "isEmpty" or "notEmpty" => type is "string" or "array" or "object",
            "hasHits" or "noHits" => type == "contextChunks",
            "isGrounded" or "notGrounded" => type == "answer",
            _ => false,
        };
        if (!valid) return $"operator '{op}' is not allowed for '{type}'";
        if (unary) return condition.ContainsKey("value") ? "unary conditions must not contain a value" : null;
        var constant = new JsonObject { ["type"] = type };
        return condition.ContainsKey("value") && Matches(constant, condition["value"]) ? null : "comparison value must match its input type";
    }

    /// <summary>选择首个匹配的出口；可选字段缺失仅匹配 isMissing，不会隐式当成 false、空串或零。</summary>
    public static JsonObject Evaluate(JsonObject data, JsonObject input)
    {
        foreach (var branch in ((JsonArray)data["cases"]!).OfType<JsonObject>())
        {
            var conditions = ((JsonArray)branch["conditions"]!).OfType<JsonObject>();
            var matched = Text(branch["match"]) == "all" ? conditions.All(rule => Test(rule, input)) : conditions.Any(rule => Test(rule, input));
            if (matched) return new JsonObject { ["route"] = Text(branch["key"]) };
        }
        return new JsonObject { ["route"] = DefaultBranch };
    }

    /// <summary>执行已验证谓词，字符串比较固定为 Ordinal；名义知识类型已在节点输入绑定时核验。</summary>
    private static bool Test(JsonObject rule, JsonObject input)
    {
        var present = input.TryGetPropertyValue(Text(rule["input"]), out var actual);
        var op = Text(rule["operator"]);
        if (op == "isMissing") return !present;
        if (op == "isPresent") return present;
        if (!present) return false;
        var expected = rule["value"];
        return op switch
        {
            "eq" => JsonNode.DeepEquals(actual, expected),
            "ne" => !JsonNode.DeepEquals(actual, expected),
            "gt" => Number(actual) > Number(expected), "gte" => Number(actual) >= Number(expected),
            "lt" => Number(actual) < Number(expected), "lte" => Number(actual) <= Number(expected),
            "contains" => Text(actual).Contains(Text(expected), StringComparison.Ordinal),
            "startsWith" => Text(actual).StartsWith(Text(expected), StringComparison.Ordinal),
            "endsWith" => Text(actual).EndsWith(Text(expected), StringComparison.Ordinal),
            "isTrue" => actual!.GetValue<bool>(), "isFalse" => !actual!.GetValue<bool>(),
            "isEmpty" => Empty(actual), "notEmpty" => !Empty(actual),
            "hasHits" => actual!["hits"]!.AsArray().Count > 0, "noHits" => actual!["hits"]!.AsArray().Count == 0,
            "isGrounded" => actual!["grounded"]!.GetValue<bool>(), "notGrounded" => !actual!["grounded"]!.GetValue<bool>(),
            _ => throw new WorkflowV2Exception("条件操作不受支持。"),
        };
    }

    /// <summary>只判断定义过的文本与集合长度，不对知识上下文进行任意字段读取。</summary>
    private static bool Empty(JsonNode? value) => value switch
    { JsonArray array => array.Count == 0, JsonObject obj => obj.Count == 0, _ => Text(value).Length == 0 };

    /// <summary>恢复时拒绝持久记录中不存在的路由，防止默默把整个图标为跳过。</summary>
    public static bool ValidOutput(WorkflowPlanNode node, JsonObject fields) =>
        !IsBranch(node.Type) || Branches(node.Data).Contains(Text(fields["route"]), StringComparer.Ordinal);
}
