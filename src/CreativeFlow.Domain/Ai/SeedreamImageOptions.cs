using System.Globalization;

namespace CreativeFlow.Domain.Ai;

/// <summary>Seedream 原生模型的尺寸与提示词优化能力；不参与 OpenAI 图片参数解释。</summary>
public static class SeedreamImageOptions
{
    /// <summary>只暴露可从型号确认的分辨率；自定义端点别名使用各版本共有的 2K。</summary>
    public static IReadOnlyList<string> Resolutions(string model) => Version(model) switch
    {
        "pro" or "flash" => ["1K", "1.5K", "2K"],
        "lite" => ["2K", "3K", "4K"],
        "4-5" => ["2K", "4K"],
        "4-0" => ["1K", "2K", "4K"],
        _ => ["2K"],
    };

    /// <summary>只有 Pro 与 4.0 支持极速提示词优化，其他型号使用标准模式。</summary>
    public static IReadOnlyList<string> PromptModes(string model) =>
        Version(model) is "pro" or "4-0" ? ["standard", "fast"] : ["standard"];

    /// <summary>将分辨率和比例一起转换为合法像素；2K 保留已有尺寸，4K 横竖屏避免超过像素上限。</summary>
    public static string SizeForAspect(string? aspect, string? resolution = null)
    {
        var (width, height) = aspect switch
        {
            "16:9" => (2816, 1584), "9:16" => (1584, 2816),
            "4:3" => (2304, 1728), "3:4" => (1728, 2304),
            "3:2" => (2496, 1664), "2:3" => (1664, 2496), _ => (2048, 2048),
        };
        if (resolution == "4K" && aspect is "16:9" or "9:16")
            return aspect == "16:9" ? "5376x3024" : "3024x5376";
        var scale = resolution switch { "1K" => 0.5, "1.5K" => 0.75, "3K" => 1.5, "4K" => 2, _ => 1 };
        return $"{(int)(width * scale)}x{(int)(height * scale)}";
    }

    /// <summary>在发起收费请求前校验模式、分辨率档位或自定义像素，不改写不受支持的输入。</summary>
    public static string? Validate(string model, string? size, string? promptMode)
    {
        if (promptMode is not null && !PromptModes(model).Contains(promptMode))
            return "当前 Seedream 模型不支持该生成模式，请选择可用的标准或极速模式。";
        if (string.IsNullOrWhiteSpace(size) || Resolutions(model).Contains(size)) return null;
        var dimensions = size.Split('x');
        if (dimensions.Length != 2
            || !int.TryParse(dimensions[0], NumberStyles.None, CultureInfo.InvariantCulture, out var width)
            || !int.TryParse(dimensions[1], NumberStyles.None, CultureInfo.InvariantCulture, out var height)
            || width <= 0 || height <= 0)
            return "Seedream 分辨率不受当前模型支持，或图片尺寸格式无效。";
        var version = Version(model);
        var minimum = version is "pro" or "flash" or "4-0" ? 921600 : 3686400;
        var maximum = version is "lite" or "4-5" or "4-0" ? 16777216 : 4624220;
        var pixels = (long)width * height;
        return pixels < minimum || pixels > maximum || (double)width / height is < 0.0625 or > 16
            ? "Seedream 图片尺寸超出当前模型允许的总像素或宽高比范围。" : null;
    }

    /// <summary>兼容官方点号、横线与带日期型号；无法确认的别名不推断额外能力。</summary>
    private static string Version(string model)
    {
        var key = model.Replace('.', '-').ToLowerInvariant();
        if (key.Contains("seedream-5-0-pro", StringComparison.Ordinal)) return "pro";
        if (key.Contains("seedream-5-0-flash", StringComparison.Ordinal)) return "flash";
        if (key.Contains("seedream-5-0", StringComparison.Ordinal)) return "lite";
        if (key.Contains("seedream-4-5", StringComparison.Ordinal)) return "4-5";
        return key.Contains("seedream-4-0", StringComparison.Ordinal) ? "4-0" : "unknown";
    }
}
