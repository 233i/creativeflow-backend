using System.Text.RegularExpressions;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Contracts.Ai;

namespace CreativeFlow.Application.Features.Knowledge;

/// <summary>保存和执行共用向量参数约束，防止直接 API 调用绕过页面限制。</summary>
public static partial class EmbeddingValidation
{
    /// <summary>校验固定路径、维度与资源上限；空返回表示有效。</summary>
    public static string? ValidateSettings(EmbeddingSettings settings)
    {
        // 路径只允许普通相对片段；禁止编码、点段、斜杠开头和任意查询，避免跨源或越权路由。
        if (string.IsNullOrWhiteSpace(settings.EndpointPath) || settings.EndpointPath.Length > 128
            || !EndpointPathPattern().IsMatch(settings.EndpointPath))
            return "Endpoint path must contain only relative alphanumeric, dash or underscore segments.";
        if (settings.ExpectedDimensions is < 1 or > 16384 || settings.RequestedDimensions is < 1 or > 16384)
            return "Dimensions must be between 1 and 16384.";
        if (settings.ExpectedDimensions is { } expected && settings.RequestedDimensions is { } requested
            && expected != requested) return "Expected and requested dimensions must match when both are specified.";
        if (settings.ModelRevision is null || settings.ModelRevision.Length > 256
            || settings.DocumentPrefix is null || settings.DocumentPrefix.Length > 1024
            || settings.QueryPrefix is null || settings.QueryPrefix.Length > 1024)
            return "Model revision or purpose prefix exceeds its limit.";
        if (settings.MaxBatchSize is < 1 or > 32 || settings.MaxInputCharacters is < 1 or > 16000
            || settings.TimeoutSeconds is < 1 or > 600 || settings.Concurrency is < 1 or > 4)
            return "Batch, text length, timeout or concurrency is outside the supported range.";
        return null;
    }

    /// <summary>校验稳定标识、用途与包含前缀后的长度，不自动拆批或截断用户文本。</summary>
    public static string? ValidateInputs(EmbeddingSettings settings, IReadOnlyList<EmbeddingInput>? inputs, string? purpose)
    {
        if (purpose is not ("document" or "query")) return "Purpose must be document or query.";
        if (inputs is null || inputs.Count < 1 || inputs.Count > settings.MaxBatchSize)
            return "Input count exceeds the configured batch limit or is empty.";
        var prefix = purpose == "query" ? settings.QueryPrefix : settings.DocumentPrefix;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var input in inputs)
        {
            if (input is null || string.IsNullOrWhiteSpace(input.Id) || input.Id.Length > 128 || !ids.Add(input.Id))
                return "Input IDs must be nonempty and unique within the batch.";
            if (string.IsNullOrWhiteSpace(input.Text) || input.Text.Length + prefix.Length > settings.MaxInputCharacters)
                return "Input text is empty or exceeds the configured character limit including its prefix.";
        }
        return null;
    }

    /// <summary>生成允许的固定相对请求路径表达式，避免反射和动态正则编译。</summary>
    [GeneratedRegex("^[A-Za-z0-9_-]+(?:/[A-Za-z0-9_-]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex EndpointPathPattern();
}
