using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Ai;

namespace CreativeFlow.Application.Features.Workflow;

/// <summary>节点级重试配置和安全判定；默认不自动重发，只有节点显式声明才会尝试。</summary>
public static class WorkflowRetryPolicy
{
    public const int DefaultMaxAttempts = 1;
    public const int MaxAttemptsLimit = 5;
    public const int MaxBackoffMilliseconds = 120_000;

    /// <summary>验证图中的重试配置，拒绝无界等待和未知策略字段。</summary>
    public static string? Validate(JsonNode? value)
    {
        if (value is null) return null;
        if (value is not JsonObject retry || retry.Any(pair => pair.Key is not ("maxAttempts" or "backoffMs" or "maxBackoffMs" or "retryOn")))
            return "retry must be an object with bounded attempt settings";
        if (Number(retry["maxAttempts"]) is not { } attempts || attempts is < 1 or > MaxAttemptsLimit || attempts != Math.Truncate(attempts))
            return $"retry.maxAttempts must be an integer from 1 to {MaxAttemptsLimit}";
        if (Number(retry["backoffMs"]) is not { } backoff || backoff is < 0 or > MaxBackoffMilliseconds || backoff != Math.Truncate(backoff))
            return $"retry.backoffMs must be an integer from 0 to {MaxBackoffMilliseconds}";
        if (Number(retry["maxBackoffMs"]) is not { } cap || cap < backoff || cap > MaxBackoffMilliseconds || cap != Math.Truncate(cap))
            return $"retry.maxBackoffMs must be an integer between backoffMs and {MaxBackoffMilliseconds}";
        if (retry["retryOn"] is not JsonArray codes || codes.Count > 8 || codes.Any(code => Text(code).Length is < 1 or > 80))
            return "retry.retryOn must contain at most eight error codes";
        return null;
    }

    /// <summary>读取有界 retry 配置；缺省值保持现有一次执行语义。</summary>
    public static WorkflowRetrySettings Read(JsonObject data)
    {
        if (data["retry"] is not JsonObject retry) return new(DefaultMaxAttempts, 1000, MaxBackoffMilliseconds, new HashSet<string>(StringComparer.Ordinal));
        var maxAttempts = Number(retry["maxAttempts"]) is { } attempts
            ? Math.Clamp((int)Math.Truncate(attempts), 1, MaxAttemptsLimit)
            : DefaultMaxAttempts;
        var backoff = Number(retry["backoffMs"]) is { } delay
            ? Math.Clamp((int)Math.Truncate(delay), 0, MaxBackoffMilliseconds)
            : 1000;
        var maxBackoff = Number(retry["maxBackoffMs"]) is { } cap
            ? Math.Clamp((int)Math.Truncate(cap), backoff, MaxBackoffMilliseconds)
            : MaxBackoffMilliseconds;
        var retryOn = retry["retryOn"] is JsonArray values
            ? values.Select(Text).Where(value => value.Length > 0).Take(8).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        return new(maxAttempts, backoff, maxBackoff, retryOn);
    }

    /// <summary>只允许可确定未受理的瞬态错误自动重试；未知结果或副作用请求永不自动重发。</summary>
    public static bool ShouldRetry(Exception exception, WorkflowRetrySettings settings)
    {
        if (exception is WorkflowHttpException http && http.MayHaveExecuted) return false;
        if (exception is WorkflowV2Exception v2 && v2.Message.Contains("状态不明确", StringComparison.Ordinal)) return false;
        var code = exception is WorkflowHttpException known ? known.Code : exception.GetType().Name;
        if (settings.RetryOn.Count > 0 && !settings.RetryOn.Contains(code)) return false;
        return exception is TimeoutException
            || exception is HttpRequestException
            || exception is IOException
            || exception is OperationCanceledException
            || exception is WorkflowHttpException { MayHaveExecuted: false };
    }

    /// <summary>指数退避保持在节点配置上限内。</summary>
    public static TimeSpan Backoff(WorkflowRetrySettings settings, int attempt)
    {
        if (settings.BackoffMilliseconds == 0) return TimeSpan.Zero;
        var exponent = Math.Clamp(attempt - 1, 0, 8);
        var milliseconds = Math.Min(settings.MaxBackoffMilliseconds, settings.BackoffMilliseconds * Math.Pow(2, exponent));
        return TimeSpan.FromMilliseconds(milliseconds);
    }

    private static double? Number(JsonNode? node) => node is JsonValue value && value.GetValueKind() == System.Text.Json.JsonValueKind.Number
        && double.TryParse(value.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number)
        && double.IsFinite(number) ? number : null;

    private static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : string.Empty;
}

/// <param name="MaxAttempts">包含第一次执行在内的最大尝试次数。</param>
/// <param name="BackoffMilliseconds">第一次重试前的退避时间。</param>
/// <param name="MaxBackoffMilliseconds">指数退避上限。</param>
/// <param name="RetryOn">可选的稳定错误码白名单。</param>
public sealed record WorkflowRetrySettings(
    int MaxAttempts,
    int BackoffMilliseconds,
    int MaxBackoffMilliseconds,
    IReadOnlySet<string> RetryOn);
