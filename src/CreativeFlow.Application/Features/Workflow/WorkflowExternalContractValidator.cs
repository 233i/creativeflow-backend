using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CreativeFlow.Application.Features.Workflow;

/// <param name="NodeId">节点图中的稳定节点标识。</param>
/// <param name="Field">关联的请求或契约字段名。</param>
public sealed record WorkflowExternalInputTarget(string NodeId, string Field);

/// <param name="MaxLength">字段允许的最大长度。</param>
/// <param name="Minimum">数值或长度允许的下界。</param>
/// <param name="Maximum">数值或长度允许的上界。</param>
/// <param name="Values">当前步骤已经得到的节点值集合。</param>
public sealed record WorkflowExternalInputConstraints(
    int? MaxLength,
    int? Minimum,
    int? Maximum,
    IReadOnlyList<string> Values);

/// <param name="Key">当前查找、签名或映射所使用的键。</param>
/// <param name="Label">供界面或模型展示的字段标签。</param>
/// <param name="Description">面向用户或模型展示的说明文本。</param>
/// <param name="Type">该数据项的协议、值或事件类型，沿用所属契约的取值。</param>
/// <param name="Required">该输入是否必须由调用方提供。</param>
/// <param name="DefaultValue">输入未提供时使用的默认值。</param>
/// <param name="Target">连线或操作的目标。</param>
/// <param name="Constraints">外部工作流参数的取值约束。</param>
public sealed record WorkflowExternalInput(
    string Key,
    string Label,
    string Description,
    string Type,
    bool Required,
    JsonNode? DefaultValue,
    WorkflowExternalInputTarget Target,
    WorkflowExternalInputConstraints Constraints);

/// <param name="Key">当前查找、签名或映射所使用的键。</param>
/// <param name="Label">供界面或模型展示的字段标签。</param>
/// <param name="Type">该数据项的协议、值或事件类型，沿用所属契约的取值。</param>
/// <param name="NodeId">节点图中的稳定节点标识。</param>
public sealed record WorkflowExternalOutput(
    string Key,
    string Label,
    string Type,
    string NodeId);

/// <param name="Inputs">当前运行或节点的输入集合。</param>
/// <param name="Outputs">当前运行对外发布的输出集合。</param>
/// <param name="InputSchemaJson">对外输入契约的 JSON schema。</param>
/// <param name="OutputSchemaJson">对外输出契约的 JSON schema。</param>
public sealed record WorkflowExternalContract(
    IReadOnlyList<WorkflowExternalInput> Inputs,
    IReadOnlyList<WorkflowExternalOutput> Outputs,
    string InputSchemaJson,
    string OutputSchemaJson);

/// <param name="Contract">经过验证的外部工作流输入输出契约。</param>
/// <param name="Errors">校验或执行过程中发现的错误集合。</param>
public sealed record WorkflowExternalContractResult(
    WorkflowExternalContract? Contract,
    IReadOnlyList<string> Errors)
{
    /// <summary>当前校验结果是否允许继续处理。</summary>
    public bool IsValid => Contract is not null && Errors.Count == 0;
}

/// <param name="GraphJson">按现有 schema 保存的完整图 JSON 快照。</param>
/// <param name="InputJson">执行输入的 JSON 快照。</param>
/// <param name="Errors">校验或执行过程中发现的错误集合。</param>
public sealed record WorkflowExternalBindingResult(
    string? GraphJson,
    string? InputJson,
    IReadOnlyList<string> Errors)
{
    /// <summary>当前校验结果是否允许继续处理。</summary>
    public bool IsValid => GraphJson is not null && InputJson is not null && Errors.Count == 0;
}

/// <summary>
/// Validates the deliberately small public parameter surface and binds caller values to a cloned
/// published graph. Node ids and arbitrary JSON paths never cross this boundary.
/// </summary>
public static partial class WorkflowExternalContractValidator
{
    /// <summary>外部工作流允许声明的输入数量上限。</summary>
    private const int MaxInputs = 50;
    /// <summary>外部工作流允许声明的输出数量上限。</summary>
    private const int MaxOutputs = 20;

    /// <param name="Type">该数据项的协议、值或事件类型，沿用所属契约的取值。</param>
    /// <param name="Minimum">数值或长度允许的下界。</param>
    /// <param name="Maximum">数值或长度允许的上界。</param>
    /// <param name="Values">当前步骤已经得到的节点值集合。</param>
    private sealed record FieldPolicy(
        string Type,
        int? Minimum = null,
        int? Maximum = null,
        IReadOnlySet<string>? Values = null);

    /// <summary>局部重跑规划使用的图字段处理策略。</summary>
    private static readonly IReadOnlyDictionary<(string NodeType, string Field), FieldPolicy>
        FieldPolicies = new Dictionary<(string, string), FieldPolicy>
        {
            [("text", "text")] = new("string"),
            [("image", "assetUrl")] = new("image"),
            [("imageGenerate", "aspect")] = new("enum", Values: Set(
                "1:1", "16:9", "9:16", "4:3", "3:4", "3:2", "2:3")),
            [("imageGenerate", "count")] = new("integer", 1, 4),
            [("imageGenerate", "quality")] = new("enum", Values: Set(
                "auto", "low", "medium", "high")),
            [("imageGenerate", "outputFormat")] = new("enum", Values: Set(
                "png", "jpeg", "webp")),
            [("imageGenerate", "seed")] = new("integer", int.MinValue, int.MaxValue),
            [("videoGenerate", "aspect")] = new("enum", Values: Set(
                "1:1", "16:9", "9:16", "4:3", "3:4", "3:2", "2:3")),
            [("videoGenerate", "resolution")] = new("enum", Values: Set(
                "480p", "720p", "1080p")),
            [("videoGenerate", "duration")] = new("integer", 1, 15),
            [("videoGenerate", "seed")] = new("integer", int.MinValue, int.MaxValue),
            [("videoGenerate", "watermark")] = new("boolean"),
            [("videoGenerate", "generateAudio")] = new("boolean"),
            [("videoGenerate", "inputMode")] = new("enum", Values: Set(
                "reference", "first-last")),
        };

    /// <summary>验证图中对外输入输出声明并生成发布契约。</summary>
    public static WorkflowExternalContractResult Validate(JsonNode? graph)
    {
        // 外部契约需要与图中真实节点和可写字段对应；错误先收集完整，再决定是否生成发布契约。
        var errors = new List<string>();
        if (graph is not JsonObject root
            || root["nodes"] is not JsonArray nodes
            || root["edges"] is not JsonArray edges)
        {
            return Invalid("workflow graph must contain nodes and edges arrays");
        }

        if (root["externalApi"] is not JsonObject manifest)
        {
            return Invalid("workflow external API settings are missing");
        }

        if (ReadInt(manifest["schemaVersion"]) != 1)
        {
            errors.Add("external API schemaVersion must be 1");
        }

        if (manifest["inputs"] is not JsonArray inputArray)
        {
            errors.Add("external API inputs must be an array");
            inputArray = [];
        }

        if (manifest["outputs"] is not JsonArray outputArray)
        {
            errors.Add("external API outputs must be an array");
            outputArray = [];
        }

        if (inputArray.Count > MaxInputs) errors.Add($"at most {MaxInputs} external inputs are allowed");
        if (outputArray.Count == 0) errors.Add("at least one external output is required");
        if (outputArray.Count > MaxOutputs) errors.Add($"at most {MaxOutputs} external outputs are allowed");

        var nodeMap = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var item in nodes)
        {
            if (item is not JsonObject node)
            {
                errors.Add("every workflow node must be an object");
                continue;
            }
            var nodeId = ReadString(node["id"]);
            if (nodeId.Length is 0 or > 100)
                errors.Add("every workflow node needs an id of at most 100 characters");
            else if (!nodeMap.TryAdd(nodeId, node))
                errors.Add($"duplicate node id '{nodeId}'");
        }
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var targets = new HashSet<string>(StringComparer.Ordinal);
        var inputs = new List<WorkflowExternalInput>();

        foreach (var item in inputArray)
        {
            if (item is not JsonObject input)
            {
                errors.Add("every external input must be an object");
                continue;
            }

            var key = ReadString(input["key"]);
            var label = ReadString(input["label"]);
            var description = ReadString(input["description"]);
            var type = ReadString(input["type"]);
            var required = ReadBool(input["required"]) ?? false;
            var target = input["target"] as JsonObject;
            var nodeId = ReadString(target?["nodeId"]);
            var field = ReadString(target?["field"]);

            if (!ParameterKey().IsMatch(key)) errors.Add($"external input key '{key}' is invalid");
            else if (!keys.Add(key)) errors.Add($"duplicate external parameter key '{key}'");
            if (label.Length is 0 or > 100) errors.Add($"external input '{key}' needs a label of at most 100 characters");
            if (description.Length > 500) errors.Add($"external input '{key}' description exceeds 500 characters");
            if (!nodeMap.TryGetValue(nodeId, out var node))
            {
                errors.Add($"external input '{key}' targets a missing node");
                continue;
            }

            var nodeType = ReadString(node["type"]);
            if (!FieldPolicies.TryGetValue((nodeType, field), out var policy))
            {
                errors.Add($"field '{field}' on node '{nodeId}' cannot be exposed");
                continue;
            }

            if (!string.Equals(type, policy.Type, StringComparison.Ordinal))
            {
                errors.Add($"external input '{key}' must use type '{policy.Type}'");
            }

            if (!targets.Add($"{nodeId}\n{field}"))
            {
                errors.Add($"node field '{nodeId}.{field}' is bound more than once");
            }

            var constraintsObject = input["constraints"] as JsonObject;
            var maxLength = ReadInt(constraintsObject?["maxLength"]);
            var minimum = ReadInt(constraintsObject?["minimum"]);
            var maximum = ReadInt(constraintsObject?["maximum"]);
            var values = ReadStrings(constraintsObject?["values"] as JsonArray);
            if (type == "string")
            {
                maxLength ??= field == "text" ? 20_000 : 5_000;
                if (maxLength is < 1 or > 20_000)
                    errors.Add($"external input '{key}' maxLength must be between 1 and 20000");
            }
            else if (type == "integer")
            {
                minimum ??= policy.Minimum;
                maximum ??= policy.Maximum;
                if (minimum > maximum) errors.Add($"external input '{key}' has an invalid number range");
                if (policy.Minimum is { } hardMin && minimum < hardMin)
                    errors.Add($"external input '{key}' minimum is below the supported value");
                if (policy.Maximum is { } hardMax && maximum > hardMax)
                    errors.Add($"external input '{key}' maximum is above the supported value");
            }
            else if (type == "enum")
            {
                var allowed = policy.Values ?? Set();
                if (values.Count == 0) values = [.. allowed];
                if (values.Count == 0 || values.Any(value => !allowed.Contains(value)))
                    errors.Add($"external input '{key}' contains an unsupported enum value");
            }

            var defaultValue = input.ContainsKey("default")
                ? input["default"]?.DeepClone()
                : null;
            var constraints = new WorkflowExternalInputConstraints(
                maxLength,
                minimum,
                maximum,
                values);
            if (defaultValue is not null
                && !ValidateValue(key, type, defaultValue, constraints, out var defaultError))
            {
                errors.Add(defaultError);
            }

            var data = node["data"] as JsonObject ?? [];
            if (!required && defaultValue is null && !HasUsableFixedValue(type, data[field]))
            {
                errors.Add($"external input '{key}' needs a default value or must be required");
            }

            inputs.Add(new WorkflowExternalInput(
                key,
                label,
                description,
                type,
                required,
                defaultValue,
                new WorkflowExternalInputTarget(nodeId, field),
                constraints));
        }

        var outputs = new List<WorkflowExternalOutput>();
        foreach (var item in outputArray)
        {
            if (item is not JsonObject output)
            {
                errors.Add("every external output must be an object");
                continue;
            }

            var key = ReadString(output["key"]);
            var label = ReadString(output["label"]);
            var type = ReadString(output["type"]);
            var nodeId = ReadString(output["nodeId"]);
            if (!ParameterKey().IsMatch(key)) errors.Add($"external output key '{key}' is invalid");
            else if (!keys.Add(key)) errors.Add($"duplicate external parameter key '{key}'");
            if (label.Length is 0 or > 100) errors.Add($"external output '{key}' needs a label of at most 100 characters");
            if (!nodeMap.TryGetValue(nodeId, out var node))
            {
                errors.Add($"external output '{key}' targets a missing node");
                continue;
            }

            var expected = ReadString(node["type"]) switch
            {
                "imageOutput" => "image",
                "videoOutput" => "video",
                "output" when edges.OfType<JsonObject>().Any(edge =>
                    ReadString(edge["target"]) == nodeId
                    && ReadString(edge["targetHandle"]) == type) => type,
                _ => string.Empty,
            };
            if (type is not ("image" or "video") || !string.Equals(type, expected, StringComparison.Ordinal))
            {
                errors.Add($"external output '{key}' does not match its output node");
            }

            outputs.Add(new WorkflowExternalOutput(key, label, type, nodeId));
        }

        if (errors.Count == 0)
        {
            var validationGraph = root.DeepClone().AsObject();
            var validationNodes = validationGraph["nodes"]!.AsArray()
                .OfType<JsonObject>()
                .ToDictionary(node => ReadString(node["id"]), StringComparer.Ordinal);
            foreach (var input in inputs)
            {
                var node = validationNodes[input.Target.NodeId];
                var data = node["data"] as JsonObject ?? [];
                node["data"] = data;
                if (!HasUsableFixedValue(input.Type, data[input.Target.Field]))
                    data[input.Target.Field] = SampleValue(input);
            }

            var graphValidation = WorkflowGraphValidator.Validate(validationGraph);
            if (!graphValidation.IsValid) errors.AddRange(graphValidation.Errors);
        }

        if (errors.Count > 0) return new WorkflowExternalContractResult(null, errors);

        return new WorkflowExternalContractResult(
            new WorkflowExternalContract(
                inputs,
                outputs,
                new JsonArray([.. inputs.Select(ToInputSchema)]).ToJsonString(),
                new JsonArray([.. outputs.Select(output => new JsonObject
                {
                    ["key"] = output.Key,
                    ["label"] = output.Label,
                    ["type"] = output.Type,
                    ["nodeId"] = output.NodeId,
                })]).ToJsonString()),
            []);
    }

    /// <summary>序列化实际生效的输入约束，使发布 Schema 与运行时校验使用同一组规则。</summary>
    private static JsonObject ToInputSchema(WorkflowExternalInput input)
    {
        var constraints = new JsonObject();
        if (input.Constraints.MaxLength is { } maxLength) constraints["maxLength"] = maxLength;
        if (input.Constraints.Minimum is { } minimum) constraints["minimum"] = minimum;
        if (input.Constraints.Maximum is { } maximum) constraints["maximum"] = maximum;
        if (input.Constraints.Values.Count > 0)
            constraints["values"] = new JsonArray([.. input.Constraints.Values.Select(value => JsonValue.Create(value))]);

        var schema = new JsonObject
        {
            ["key"] = input.Key,
            ["label"] = input.Label,
            ["description"] = input.Description,
            ["type"] = input.Type,
            ["required"] = input.Required,
            ["target"] = new JsonObject { ["nodeId"] = input.Target.NodeId, ["field"] = input.Target.Field },
            ["constraints"] = constraints,
        };
        if (input.DefaultValue is not null) schema["default"] = input.DefaultValue.DeepClone();
        return schema;
    }

    /// <summary>把外部输入按已发布契约绑定到图快照，不修改编辑中的工作流。</summary>
    public static WorkflowExternalBindingResult Bind(
        string graphJson,
        JsonObject? suppliedInputs,
        IReadOnlyDictionary<string, string> resolvedImageUrls)
    {
        // 在发布图的副本上绑定输入，拒绝未知键；图片只使用调用方已验证的素材解析结果。
        JsonNode? parsed;
        try { parsed = JsonNode.Parse(graphJson); }
        catch (JsonException) { return BindingInvalid("published workflow graph is invalid JSON"); }

        var validation = Validate(parsed);
        if (!validation.IsValid || validation.Contract is null)
            return new WorkflowExternalBindingResult(null, null, validation.Errors);

        suppliedInputs ??= [];
        var errors = new List<string>();
        var contract = validation.Contract;
        var byKey = contract.Inputs.ToDictionary(input => input.Key, StringComparer.Ordinal);
        foreach (var key in suppliedInputs.Select(pair => pair.Key))
        {
            if (!byKey.ContainsKey(key)) errors.Add($"unknown external input '{key}'");
        }

        var root = parsed!.DeepClone().AsObject();
        var nodes = root["nodes"]!.AsArray()
            .OfType<JsonObject>()
            .ToDictionary(node => ReadString(node["id"]), StringComparer.Ordinal);
        var normalized = new JsonObject();

        foreach (var input in contract.Inputs)
        {
            var node = nodes[input.Target.NodeId];
            var data = node["data"] as JsonObject ?? [];
            node["data"] = data;
            JsonNode? value = suppliedInputs.ContainsKey(input.Key)
                ? suppliedInputs[input.Key]
                : input.DefaultValue;

            if (value is null)
            {
                if (input.Required && !HasUsableFixedValue(input.Type, data[input.Target.Field]))
                    errors.Add($"external input '{input.Key}' is required");
                continue;
            }

            if (!ValidateValue(input.Key, input.Type, value, input.Constraints, out var error))
            {
                errors.Add(error);
                continue;
            }

            if (input.Type == "image")
            {
                if (!resolvedImageUrls.TryGetValue(input.Key, out var assetUrl))
                {
                    errors.Add($"external image '{input.Key}' is unavailable for this workflow");
                    continue;
                }
                data[input.Target.Field] = assetUrl;
            }
            else
            {
                data[input.Target.Field] = value.DeepClone();
            }
            normalized[input.Key] = value.DeepClone();
        }

        if (errors.Count > 0) return new WorkflowExternalBindingResult(null, null, errors);
        var graphValidation = WorkflowGraphValidator.Validate(root);
        if (!graphValidation.IsValid)
            return new WorkflowExternalBindingResult(null, null, graphValidation.Errors);

        return new WorkflowExternalBindingResult(
            root.ToJsonString(),
            normalized.ToJsonString(),
            []);
    }

    /// <summary>枚举外部输入中声明为图片的素材标识。</summary>
    public static IReadOnlyDictionary<string, string> ReadImageAssetIds(
        WorkflowExternalContract contract,
        JsonObject? suppliedInputs)
    {
        suppliedInputs ??= [];
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var input in contract.Inputs.Where(input => input.Type == "image"))
        {
            var value = suppliedInputs.ContainsKey(input.Key)
                ? suppliedInputs[input.Key]
                : input.DefaultValue;
            if (value is JsonObject image && ReadString(image["assetId"]) is { Length: > 0 } id)
                result[input.Key] = id;
        }
        return result;
    }

    /// <summary>根据发布契约检查单个外部输入的类型与取值约束。</summary>
    private static bool ValidateValue(
        string key,
        string type,
        JsonNode value,
        WorkflowExternalInputConstraints constraints,
        out string error)
    {
        error = string.Empty;
        switch (type)
        {
            case "string" when TryString(value, out var text):
                if (text.Length > (constraints.MaxLength ?? 20_000))
                    error = $"external input '{key}' exceeds its maximum length";
                return error.Length == 0;
            case "integer" when ReadInt(value) is { } number:
                if (constraints.Minimum is { } min && number < min
                    || constraints.Maximum is { } max && number > max)
                    error = $"external input '{key}' is outside its allowed range";
                return error.Length == 0;
            case "boolean" when ReadBool(value) is not null:
                return true;
            case "enum" when TryString(value, out var option):
                if (!constraints.Values.Contains(option, StringComparer.Ordinal))
                    error = $"external input '{key}' is not an allowed value";
                return error.Length == 0;
            case "image" when value is JsonObject image
                              && Guid.TryParse(ReadString(image["assetId"]), out _):
                return true;
            default:
                error = $"external input '{key}' has the wrong type";
                return false;
        }
    }

    /// <summary>判断字段是否已经具有可供发布执行的固定值。</summary>
    private static bool HasUsableFixedValue(string type, JsonNode? value) => type switch
    {
        "string" or "enum" => TryString(value, out var text) && text.Length > 0,
        "integer" => ReadInt(value) is not null,
        "boolean" => ReadBool(value) is not null,
        "image" => TryString(value, out var url) && url.StartsWith(
            "/api/v1/canvas/assets/", StringComparison.Ordinal),
        _ => false,
    };

    /// <summary>根据字段类型构造用于验证绑定的代表性值。</summary>
    private static JsonNode SampleValue(WorkflowExternalInput input) => input.Type switch
    {
        "string" => JsonValue.Create("external input")!,
        "image" => JsonValue.Create(
            "/api/v1/canvas/assets/00000000-0000-0000-0000-000000000001")!,
        "integer" => JsonValue.Create(input.Constraints.Minimum ?? 1)!,
        "boolean" => JsonValue.Create(false)!,
        "enum" => JsonValue.Create(input.Constraints.Values.First())!,
        _ => JsonValue.Create(string.Empty)!,
    };

    /// <summary>从 JSON 节点读取既有字符串列表表示。</summary>
    private static List<string> ReadStrings(JsonArray? values) => values is null
        ? []
        : [.. values.Select(ReadString).Where(value => value.Length > 0).Distinct(StringComparer.Ordinal)];

    /// <summary>从 JSON 或配置读取字符串字段，保留缺失及类型错误时的原有回退。</summary>
    private static string ReadString(JsonNode? value) =>
        TryString(value, out var text) ? text.Trim() : string.Empty;

    /// <summary>在 JSON 值确为字符串时提取文本。</summary>
    private static bool TryString(JsonNode? value, out string text)
    {
        text = string.Empty;
        return value is JsonValue json && json.TryGetValue(out text!);
    }

    /// <summary>从 JSON 或配置读取整数，保留原有默认值规则。</summary>
    private static int? ReadInt(JsonNode? value) =>
        value is JsonValue json && json.TryGetValue<int>(out var number) ? number : null;

    /// <summary>从 JSON 或配置读取布尔值，保留原有默认值规则。</summary>
    private static bool? ReadBool(JsonNode? value) =>
        value is JsonValue json && json.TryGetValue<bool>(out var result) ? result : null;

    /// <summary>按当前契约设置指定配置或节点字段。</summary>
    private static HashSet<string> Set(params string[] values) =>
        new(values, StringComparer.Ordinal);

    /// <summary>将校验失败映射为当前功能既有的错误结果，保留详情和错误分类。</summary>
    private static WorkflowExternalContractResult Invalid(string error) => new(null, [error]);

    /// <summary>构造外部输入绑定失败的校验结果。</summary>
    private static WorkflowExternalBindingResult BindingInvalid(string error) =>
        new(null, null, [error]);

    /// <summary>为节点绑定字段构造稳定的组合键。</summary>
    [GeneratedRegex("^[a-z][a-z0-9_]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ParameterKey();
}
