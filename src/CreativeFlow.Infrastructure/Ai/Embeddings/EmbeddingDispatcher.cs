using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Features.Knowledge;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Infrastructure.Ai.Embeddings;

/// <summary>只分派明确的向量协议；以非等待式背压限制全局及渠道并发，不创建第二套任务队列。</summary>
internal sealed class EmbeddingDispatcher(IEnumerable<IEmbeddingClient> clients) : IEmbeddingDispatcher
{
    /// <summary>按协议唯一索引实际客户端，重复注册会在装配时失败。</summary>
    private readonly IReadOnlyDictionary<ProviderType, IEmbeddingClient> _clients = clients.ToDictionary(client => client.Protocol);
    /// <summary>保护活跃调用计数，空闲渠道及时移除以免长期积累。</summary>
    private readonly object _gate = new();
    /// <summary>渠道当前活跃数及活跃调用声明的限制，避免修改配置绕过更小上限。</summary>
    private readonly Dictionary<ProviderId, List<int>> _active = [];
    /// <summary>当前进程的新向量请求总数，最多四个。</summary>
    private int _total;

    /// <summary>在发送前拒绝禁用资源、错误分类/协议、无效批次及超过并发上限的请求。</summary>
    public async Task<EmbeddingResult> EmbedAsync(EmbeddingRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!request.Provider.Enabled || !request.Model.Enabled || request.Model.ProviderId != request.Provider.Id)
            return EmbeddingResult.Failure("model_unavailable", "Embedding provider or model is disabled or mismatched.");
        if (request.Model.Category != ModelCategory.Embedding
            || !_clients.TryGetValue(request.Provider.ResolveProtocol(request.Model), out var client))
            return EmbeddingResult.Failure("unsupported_protocol", "Select an embedding model with the explicit openai-embeddings protocol.");
        var error = EmbeddingValidation.ValidateSettings(request.Settings)
            ?? EmbeddingValidation.ValidateInputs(request.Settings, request.Inputs, request.Purpose);
        if (error is not null) return EmbeddingResult.Failure("invalid_request", error);

        // 立即返回 busy，让未来持久化索引 Worker 自行背压；这里不持有无限等待的内存任务。
        lock (_gate)
        {
            var current = _active.GetValueOrDefault(request.Provider.Id);
            var limit = current is { Count: > 0 }
                ? Math.Min(current.Min(), request.Settings.Concurrency) : request.Settings.Concurrency;
            if (_total >= 4 || (current?.Count ?? 0) >= limit)
                return EmbeddingResult.Failure("busy", "Embedding concurrency limit reached; no request was sent.");
            current ??= _active[request.Provider.Id] = [];
            current.Add(request.Settings.Concurrency);
            _total++;
        }
        try { return await client.EmbedAsync(request, ct); }
        finally
        {
            lock (_gate)
            {
                var current = _active[request.Provider.Id];
                current.Remove(request.Settings.Concurrency);
                if (current.Count == 0) _active.Remove(request.Provider.Id);
                _total--;
            }
        }
    }
}
