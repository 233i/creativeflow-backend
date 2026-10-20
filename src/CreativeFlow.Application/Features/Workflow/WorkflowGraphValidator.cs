using System.Text.Json.Nodes;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Application.Features.Workflow;

public enum WorkflowValueType
{
    /// <summary>普通文本内容或值类型。</summary>
    Text,
    /// <summary>图片数据、素材或模型类别。</summary>
    Image,
    /// <summary>视频数据、素材或模型类别。</summary>
    Video,
    /// <summary>新版图中的单个字符串值。</summary>
    String,
    /// <summary>新版图中的有限数值。</summary>
    Number,
    /// <summary>新版图中的布尔值。</summary>
    Boolean,
    /// <summary>新版图中的有界 JSON 对象。</summary>
    Object,
    /// <summary>新版图中的有界 JSON 数组。</summary>
    Array,
}

/// <param name="Id">当前实体或契约项目的唯一标识。</param>
/// <param name="Source">连线、素材或操作的来源。</param>
/// <param name="SourceHandle">连线源节点的输出端口标识。</param>
/// <param name="Target">连线或操作的目标。</param>
/// <param name="TargetHandle">连线目标节点的输入端口标识。</param>
/// <param name="ValueType">工作流输入输出端口的数据类型。</param>
public sealed record WorkflowPlanEdge(
    string Id,
    string Source,
    string SourceHandle,
    string Target,
    string TargetHandle,
    WorkflowValueType ValueType)
{
    /// <summary>新版条件路由连接使用独立执行端口，不参与普通字段绑定；旧图没有此端口。</summary>
    public bool IsControl => TargetHandle == WorkflowBranchValues.ControlPort;
}

/// <param name="Id">当前实体或契约项目的唯一标识。</param>
/// <param name="Type">该数据项的协议、值或事件类型，沿用所属契约的取值。</param>
/// <param name="Data">该节点或结果携带的结构化数据。</param>
/// <param name="Inputs">当前运行或节点的输入集合。</param>
public sealed record WorkflowPlanNode(
    string Id,
    string Type,
    JsonObject Data,
    IReadOnlyList<WorkflowPlanEdge> Inputs);

/// <param name="Nodes">图计划或运行中的节点集合。</param>
public sealed record WorkflowPlan(IReadOnlyList<WorkflowPlanNode> Nodes);

/// <param name="Plan">通过校验后可执行的节点或操作计划。</param>
/// <param name="Errors">校验或执行过程中发现的错误集合。</param>
public sealed record WorkflowValidationResult(
    WorkflowPlan? Plan,
    IReadOnlyList<string> Errors)
{
    /// <summary>当前校验结果是否允许继续处理。</summary>
    public bool IsValid => Plan is not null && Errors.Count == 0;
}

/// <summary>
/// 验证并编译刻意精简、由服务器控制的工作流词汇表。
/// </summary>
/// <remarks>
/// 浏览器只是编辑器。此验证器是安全性和正确性边界：导入的图不能虚构自定义节点、
/// 连接不兼容的值或创建环路。
/// </remarks>
public static class WorkflowGraphValidator
{
    /// <summary>图或信号载荷的 schema 版本。</summary>
    public const int SchemaVersion = 1;
    /// <summary>工作流允许的最大节点数量。</summary>
    public const int MaxNodes = 40;
    /// <summary>工作流允许的最大连线数量。</summary>
    public const int MaxEdges = 100;

    /// <summary>工作流图当前允许的节点类型集合。</summary>
    private static readonly HashSet<string> SupportedTypes =
        new(StringComparer.Ordinal)
        {
            "text", "image", "imageGenerate", "videoGenerate", "videoExtend", "videoFrame",
            "imageOutput", "videoOutput", "output",
        };

    /// <summary>验证节点、连线、端口和必需数据，成功时产生拓扑执行计划。</summary>
    public static WorkflowValidationResult Validate(JsonNode? graph) =>
        Validate(graph, allowEmptyTemplateInputs: false);

    /// <summary>
    /// 验证可复用模板。文本和图片源节点可以刻意留空，
    /// 让用户在实例化模板后再提供这些输入。
    /// </summary>
    public static WorkflowValidationResult ValidateTemplate(JsonNode? graph) =>
        Validate(graph, allowEmptyTemplateInputs: true);

    /// <summary>验证节点、连线、端口和必需数据，成功时产生拓扑执行计划。</summary>
    private static WorkflowValidationResult Validate(
        JsonNode? graph,
        bool allowEmptyTemplateInputs)
    {
        // 图校验同时约束节点、端口与有向依赖；仅当所有检查通过后才返回可执行的拓扑计划。
        var errors = new List<string>();
        if (graph is not JsonObject root)
        {
            return Invalid("workflow graph must be an object");
        }

        if (root["nodes"] is not JsonArray nodesArray || root["edges"] is not JsonArray edgesArray)
        {
            return Invalid("workflow graph must contain nodes and edges arrays");
        }

        if (nodesArray.Count == 0) errors.Add("workflow must contain at least one node");
        if (nodesArray.Count > MaxNodes) errors.Add($"workflow may contain at most {MaxNodes} nodes");
        if (edgesArray.Count > MaxEdges) errors.Add($"workflow may contain at most {MaxEdges} edges");

        var nodes = new Dictionary<string, ParsedNode>(StringComparer.Ordinal);
        foreach (var item in nodesArray)
        {
            if (item is not JsonObject node)
            {
                errors.Add("every workflow node must be an object");
                continue;
            }

            var id = ReadString(node, "id");
            var type = ReadString(node, "type");
            if (string.IsNullOrWhiteSpace(id) || id.Length > 100)
            {
                errors.Add("every workflow node needs an id of at most 100 characters");
                continue;
            }

            if (!SupportedTypes.Contains(type))
            {
                errors.Add($"node '{id}' has unsupported type '{type}'");
                continue;
            }

            if (!nodes.TryAdd(id, new ParsedNode(id, type, node["data"] as JsonObject ?? [])))
            {
                errors.Add($"duplicate node id '{id}'");
                continue;
            }

            ValidateData(
                id,
                type,
                node["data"] as JsonObject,
                errors,
                allowEmptyTemplateInputs);
        }

        var edges = new List<WorkflowPlanEdge>();
        var edgeIds = new HashSet<string>(StringComparer.Ordinal);
        var connections = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in edgesArray)
        {
            if (item is not JsonObject edge)
            {
                errors.Add("every workflow edge must be an object");
                continue;
            }

            var id = ReadString(edge, "id");
            var source = ReadString(edge, "source");
            var target = ReadString(edge, "target");
            var sourceHandle = ReadString(edge, "sourceHandle");
            var targetHandle = ReadString(edge, "targetHandle");

            if (string.IsNullOrWhiteSpace(id) || !edgeIds.Add(id))
            {
                errors.Add(string.IsNullOrWhiteSpace(id) ? "every workflow edge needs an id" : $"duplicate edge id '{id}'");
                continue;
            }

            if (!nodes.TryGetValue(source, out var sourceNode)
                || !nodes.TryGetValue(target, out var targetNode))
            {
                errors.Add($"edge '{id}' references a missing node");
                continue;
            }

            if (source == target)
            {
                errors.Add($"edge '{id}' cannot connect a node to itself");
                continue;
            }

            var sourceType = SourcePort(sourceNode.Type, sourceHandle);
            var targetType = TargetPort(targetNode.Type, targetHandle);
            if (sourceType is null)
            {
                errors.Add($"edge '{id}' uses unknown output '{sourceHandle}' on node '{source}'");
                continue;
            }

            if (targetType is null)
            {
                errors.Add($"edge '{id}' uses unknown input '{targetHandle}' on node '{target}'");
                continue;
            }

            if (sourceType != targetType)
            {
                errors.Add($"edge '{id}' connects incompatible ports");
                continue;
            }

            var connection = $"{source}\n{sourceHandle}\n{target}\n{targetHandle}";
            if (!connections.Add(connection))
            {
                errors.Add($"edge '{id}' duplicates an existing connection");
                continue;
            }

            edges.Add(new WorkflowPlanEdge(
                id, source, sourceHandle, target, targetHandle, sourceType.Value));
        }

        foreach (var node in nodes.Values)
        {
            var inputs = edges.Where(edge => edge.Target == node.Id).ToList();
            if (node.Type is "imageGenerate" or "videoGenerate"
                && !inputs.Any(edge => edge.TargetHandle is "prompt" or "image"))
            {
                errors.Add($"node '{node.Id}' needs a text or image input");
            }
            else if (node.Type == "videoExtend")
            {
                if (!inputs.Any(edge => edge.TargetHandle == "prompt"))
                {
                    errors.Add($"node '{node.Id}' needs a text prompt input");
                }

                var videoInputCount = inputs.Count(edge => edge.TargetHandle == "video");
                if (videoInputCount != 1)
                {
                    errors.Add($"node '{node.Id}' needs exactly one video input");
                }
            }
            else if (node.Type == "videoFrame"
                     && !inputs.Any(edge => edge.TargetHandle == "video"))
            {
                errors.Add($"node '{node.Id}' needs a video input");
            }
            else if (IsOutputType(node.Type) && inputs.Count == 0)
            {
                errors.Add($"output node '{node.Id}' needs an input");
            }
        }

        if (!nodes.Values.Any(node => IsOutputType(node.Type)))
        {
            errors.Add("workflow needs at least one output node");
        }

        var ordered = TopologicalOrder(nodes, edges, errors);
        if (errors.Count > 0 || ordered is null)
        {
            return new WorkflowValidationResult(null, errors);
        }

        return new WorkflowValidationResult(
            new WorkflowPlan(
            [
                .. ordered.Select(node => new WorkflowPlanNode(
                    node.Id,
                    node.Type,
                    node.Data,
                    [.. edges.Where(edge => edge.Target == node.Id)])),
            ]),
            []);
    }

    /// <summary>根据节点依赖生成稳定执行顺序并检测环。</summary>
    private static IReadOnlyList<ParsedNode>? TopologicalOrder(
        IReadOnlyDictionary<string, ParsedNode> nodes,
        IReadOnlyList<WorkflowPlanEdge> edges,
        List<string> errors)
    {
        var incoming = nodes.Keys.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
        var outgoing = nodes.Keys.ToDictionary(
            id => id,
            _ => new List<string>(),
            StringComparer.Ordinal);

        foreach (var edge in edges)
        {
            incoming[edge.Target] += 1;
            outgoing[edge.Source].Add(edge.Target);
        }

        var ready = new Queue<string>(nodes.Keys.Where(id => incoming[id] == 0));
        var ordered = new List<ParsedNode>(nodes.Count);
        while (ready.TryDequeue(out var id))
        {
            ordered.Add(nodes[id]);
            foreach (var target in outgoing[id])
            {
                incoming[target] -= 1;
                if (incoming[target] == 0) ready.Enqueue(target);
            }
        }

        if (ordered.Count == nodes.Count) return ordered;

        errors.Add("workflow cannot contain cycles");
        return null;
    }

    /// <summary>检查当前节点类型要求的数据、模型和媒体参数。</summary>
    private static void ValidateData(
        string id,
        string type,
        JsonObject? data,
        List<string> errors,
        bool allowEmptyTemplateInputs)
    {
        // 节点类型决定必填字段与可选参数；未知或类型不符的输入沿用已有错误，不能在执行时默默补齐。
        data ??= [];
        switch (type)
        {
            case "text":
                {
                    var text = ReadString(data, "text");
                    if (!allowEmptyTemplateInputs && string.IsNullOrWhiteSpace(text))
                    {
                        errors.Add($"text node '{id}' is empty");
                    }
                    if (text.Length > 20_000) errors.Add($"text node '{id}' exceeds 20000 characters");
                    break;
                }
            case "image":
                {
                    var url = ReadString(data, "assetUrl");
                    const string prefix = "/api/v1/canvas/assets/";
                    if (!allowEmptyTemplateInputs
                        && (!url.StartsWith(prefix, StringComparison.Ordinal)
                            || !Guid.TryParse(url[prefix.Length..].Split('?')[0], out _)))
                    {
                        errors.Add($"image node '{id}' needs a stored image asset");
                    }
                    break;
                }
            case "imageGenerate":
                {
                    var count = ReadInt(data, "count") ?? 1;
                    if (count is < 1 or > 4) errors.Add($"node '{id}' count must be between 1 and 4");

                    var providerId = ReadString(data, "providerId");
                    var modelId = ReadString(data, "modelId");
                    if (providerId.Length != 0 || modelId.Length != 0)
                    {
                        if (!ProviderId.TryParse(providerId, out _)
                            || !ModelId.TryParse(modelId, out _))
                        {
                            errors.Add($"node '{id}' has an invalid image model selection");
                        }
                    }

                    var aspect = ReadString(data, "aspect");
                    if (aspect.Length > 0 && !AllowedAspects.Contains(aspect))
                    {
                        errors.Add($"node '{id}' has an invalid aspect ratio");
                    }
                    break;
                }
            case "videoGenerate":
                {
                    var duration = ReadInt(data, "duration")
                        ?? VideoGenerationLimits.DefaultDurationSeconds;
                    if (duration is < VideoGenerationLimits.MinimumDurationSeconds
                        or > VideoGenerationLimits.MaximumDurationSeconds)
                    {
                        errors.Add(
                            $"node '{id}' duration must be between " +
                            $"{VideoGenerationLimits.MinimumDurationSeconds} and " +
                            $"{VideoGenerationLimits.MaximumDurationSeconds} seconds");
                    }

                    var aspect = ReadString(data, "aspect");
                    if (aspect.Length > 0 && !AllowedAspects.Contains(aspect))
                    {
                        errors.Add($"node '{id}' has an invalid aspect ratio");
                    }

                    var inputMode = ReadString(data, "inputMode");
                    if (inputMode.Length > 0 && inputMode is not ("reference" or "first-last"))
                    {
                        errors.Add($"node '{id}' has an invalid video input mode");
                    }
                    if (data["reverseFrames"] is { } reverse
                        && (reverse is not JsonValue reverseValue || !reverseValue.TryGetValue<bool>(out _)))
                    {
                        errors.Add($"node '{id}' reverseFrames must be a boolean");
                    }
                    break;
                }
            case "videoExtend":
                {
                    var duration = ReadInt(data, "duration")
                        ?? VideoExtensionLimits.DefaultDurationSeconds;
                    if (duration is < VideoExtensionLimits.MinimumDurationSeconds
                        or > VideoExtensionLimits.MaximumDurationSeconds)
                    {
                        errors.Add(
                            $"node '{id}' extension duration must be between " +
                            $"{VideoExtensionLimits.MinimumDurationSeconds} and " +
                            $"{VideoExtensionLimits.MaximumDurationSeconds} seconds");
                    }
                    break;
                }
            case "videoFrame":
                {
                    var frame = ReadString(data, "frame");
                    if (frame.Length > 0 && frame != "last")
                    {
                        errors.Add($"node '{id}' has an unsupported frame position");
                    }
                    break;
                }
        }
    }

    /// <summary>按节点类型解析输出端口的既有数据类型。</summary>
    private static WorkflowValueType? SourcePort(string nodeType, string handle) =>
        (nodeType, handle) switch
        {
            ("text", "text") => WorkflowValueType.Text,
            ("image", "image") => WorkflowValueType.Image,
            ("imageGenerate", "image") => WorkflowValueType.Image,
            ("imageOutput", "image") => WorkflowValueType.Image,
            ("videoFrame", "image") => WorkflowValueType.Image,
            ("videoGenerate", "video") => WorkflowValueType.Video,
            ("videoExtend", "video") => WorkflowValueType.Video,
            ("videoOutput", "video") => WorkflowValueType.Video,
            _ => null,
        };

    /// <summary>按节点类型解析输入端口的既有数据类型。</summary>
    private static WorkflowValueType? TargetPort(string nodeType, string handle) =>
        (nodeType, handle) switch
        {
            ("imageGenerate", "prompt") => WorkflowValueType.Text,
            ("imageGenerate", "image") => WorkflowValueType.Image,
            ("videoGenerate", "prompt") => WorkflowValueType.Text,
            ("videoGenerate", "image") => WorkflowValueType.Image,
            ("videoExtend", "prompt") => WorkflowValueType.Text,
            ("videoExtend", "video") => WorkflowValueType.Video,
            ("videoFrame", "video") => WorkflowValueType.Video,
            ("imageOutput", "image") => WorkflowValueType.Image,
            ("videoOutput", "video") => WorkflowValueType.Video,
            ("output", "image") => WorkflowValueType.Image,
            ("output", "video") => WorkflowValueType.Video,
            _ => null,
        };

    /// <summary>判断节点是否为当前支持的输出节点类型。</summary>
    private static bool IsOutputType(string nodeType) =>
        nodeType is "imageOutput" or "videoOutput" or "output";

    /// <summary>从 JSON 或配置读取字符串字段，保留缺失及类型错误时的原有回退。</summary>
    private static string ReadString(JsonObject node, string property)
    {
        try
        {
            return node[property]?.GetValue<string>()?.Trim() ?? string.Empty;
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
    }

    /// <summary>从 JSON 或配置读取整数，保留原有默认值规则。</summary>
    private static int? ReadInt(JsonObject node, string property)
    {
        try
        {
            return node[property]?.GetValue<int>();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>将校验失败映射为当前功能既有的错误结果，保留详情和错误分类。</summary>
    private static WorkflowValidationResult Invalid(string error) => new(null, [error]);

    /// <summary>当前视频协议允许的宽高比值。</summary>
    private static readonly HashSet<string> AllowedAspects =
        new(StringComparer.Ordinal) { "1:1", "3:2", "2:3", "16:9", "9:16", "4:3", "3:4" };

    /// <param name="Id">当前实体或契约项目的唯一标识。</param>
    /// <param name="Type">该数据项的协议、值或事件类型，沿用所属契约的取值。</param>
    /// <param name="Data">该节点或结果携带的结构化数据。</param>
    private sealed record ParsedNode(string Id, string Type, JsonObject Data);
}
