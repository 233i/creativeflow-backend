using System.Collections.Concurrent;
using System.Threading.RateLimiting;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Infrastructure.Options;

namespace CreativeFlow.Api.Http;

public enum ExternalWorkflowRateLimitKind
{
    /// <summary>外部工作流运行创建类操作的限流分区。</summary>
    CreateRun,
    /// <summary>外部工作流图片上传类操作的限流分区。</summary>
    UploadImage,
    /// <summary>外部工作流状态读取类操作的限流分区。</summary>
    ReadStatus,
}

/// <summary>
/// Limits requests only after an API key has been authenticated. This prevents callers from
/// evading a key-level limit by rotating syntactically valid but nonexistent token prefixes.
/// </summary>
public sealed class ExternalWorkflowRateLimiter(
    ExternalWorkflowApiOptions options) : IDisposable
{
    /// <summary>按已验证 API Key 和操作种类索引的限流器。</summary>
    private readonly ConcurrentDictionary<LimiterKey, FixedWindowRateLimiter> _limiters = new();
    private readonly Dictionary<WorkflowApiKeyId, int> _streams = new();
    private int _streamCount;

    /// <summary>长连接另有限额，断开或失败时释放；不因订阅而占用工作流执行配额。</summary>
    public IDisposable? AcquireStream(WorkflowApiKeyId key)
    {
        lock (_streams)
        {
            if (_streamCount >= options.MaxConcurrentStreams || _streams.GetValueOrDefault(key) >= options.MaxStreamsPerApiKey) return null;
            _streamCount++; _streams[key] = _streams.GetValueOrDefault(key) + 1;
        }
        return new StreamLease(() =>
        {
            lock (_streams)
            {
                _streamCount--;
                if (--_streams[key] == 0) _streams.Remove(key);
            }
        });
    }

    private sealed class StreamLease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }

    /// <summary>按既有令牌、窗口或过期规则尝试取得限流/锁额度。</summary>
    public bool TryAcquire(
        WorkflowApiKeyId apiKeyId,
        ExternalWorkflowRateLimitKind kind,
        out TimeSpan? retryAfter)
    {
        var limiter = _limiters.GetOrAdd(
            new LimiterKey(apiKeyId, kind),
            key => CreateLimiter(key.Kind));
        using var lease = limiter.AttemptAcquire();
        retryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out var duration)
            ? duration
            : null;
        return lease.IsAcquired;
    }

    /// <summary>释放各外部调用限流器并清空按调用方维护的限流实例。</summary>
    public void Dispose()
    {
        foreach (var limiter in _limiters.Values) limiter.Dispose();
        _limiters.Clear();
    }

    /// <summary>按操作类型创建每分钟固定窗口限流器，超额请求不进入等待队列。</summary>
    private FixedWindowRateLimiter CreateLimiter(ExternalWorkflowRateLimitKind kind) => new(
        new FixedWindowRateLimiterOptions
        {
            AutoReplenishment = true,
            PermitLimit = kind switch
            {
                ExternalWorkflowRateLimitKind.CreateRun => options.CreateRunPermitLimitPerMinute,
                ExternalWorkflowRateLimitKind.UploadImage => options.ImageUploadPermitLimitPerMinute,
                _ => options.StatusPermitLimitPerMinute,
            },
            QueueLimit = 0,
            Window = TimeSpan.FromMinutes(1),
        });

    /// <param name="ApiKeyId">工作流 API Key 的标识。</param>
    /// <param name="Kind">当前资源或数据项的种类。</param>
    private readonly record struct LimiterKey(
        WorkflowApiKeyId ApiKeyId,
        ExternalWorkflowRateLimitKind Kind);
}
