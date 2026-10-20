using System.Text.Json.Nodes;
using CreativeFlow.Application.Features.Workflow;

namespace CreativeFlow.Infrastructure.Ai.Workflows;

/// <summary>媒体节点的纯输入绑定与配置读取，保留顺序、默认值和错误规则。</summary>
internal static class WorkflowMediaInputs
{
    /// <summary>按图连线顺序汇集已有节点输出。</summary>
    internal static WorkflowNodeValue GatherInputs(
        WorkflowPlanNode node,
        IReadOnlyDictionary<string, WorkflowNodeValue> values)
    {
        var text = new List<string>();
        var images = new List<string>();
        var videos = new List<string>();
        foreach (var edge in node.Inputs)
        {
            if (!values.TryGetValue(edge.Source, out var value))
            {
                throw new WorkflowExecutionException(
                    $"Upstream node '{edge.Source}' did not produce a value.");
            }

            switch (edge.ValueType)
            {
                case WorkflowValueType.Text:
                    text.AddRange(value.Texts);
                    break;
                case WorkflowValueType.Image:
                    images.AddRange(value.Images);
                    break;
                case WorkflowValueType.Video:
                    videos.AddRange(value.Videos);
                    break;
            }
        }

        return new WorkflowNodeValue(text, images, videos);
    }

    /// <summary>仅从既有站内素材路径解析素材标识。</summary>
    internal static bool TryAssetId(string url, out Guid assetId)
    {
        const string prefix = "/api/v1/canvas/assets/";
        assetId = default;
        return url.StartsWith(prefix, StringComparison.Ordinal)
               && Guid.TryParse(url[prefix.Length..].Split('?')[0], out assetId);
    }

    /// <summary>将工作流宽高比映射为既有图片尺寸选项。</summary>
    internal static string? SizeFor(string aspect) => aspect switch
    {
        "1:1" => "1024x1024",
        "9:16" or "2:3" or "3:4" => "1024x1536",
        "16:9" or "3:2" or "4:3" => "1536x1024",
        _ => null,
    };

    /// <summary>将媒体类型映射为当前支持的文件扩展名，并保留未知类型的默认值。</summary>
    internal static string ExtensionFor(string mediaType) => mediaType switch
    {
        "image/jpeg" => ".jpg",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        _ => ".png",
    };

    /// <summary>从 JSON 或配置读取字符串字段，保留缺失及类型错误时的原有回退。</summary>
    internal static string ReadString(JsonObject data, string property)
    {
        try { return data[property]?.GetValue<string>()?.Trim() ?? string.Empty; }
        catch (InvalidOperationException) { return string.Empty; }
    }

    /// <summary>从 JSON 或配置读取整数，保留原有默认值规则。</summary>
    internal static int? ReadInt(JsonObject data, string property)
    {
        try { return data[property]?.GetValue<int>(); }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>从 JSON 或配置读取布尔值，保留原有默认值规则。</summary>
    internal static bool? ReadBool(JsonObject data, string property)
    {
        try { return data[property]?.GetValue<bool>(); }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>把空字符串转换为未提供值，不改变非空内容。</summary>
    internal static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}
