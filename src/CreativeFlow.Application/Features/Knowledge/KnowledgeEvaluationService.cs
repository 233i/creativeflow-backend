using System.Diagnostics;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Contracts.Ai;

namespace CreativeFlow.Application.Features.Knowledge;

/// <summary>在同一固定版本上对照人工标注题；每题只向量化一次，不调用回答模型或自动重试。</summary>
public sealed class KnowledgeEvaluationService(IKnowledgeRepository repository, IKnowledgeEmbedding embedding, KnowledgeRetrievalService retrieval)
{
    /// <summary>输出主命中的 Recall、MRR、NDCG 与逐题诊断；模型重排序必须显式确认。</summary>
    public async Task<KnowledgeEvaluationResponse> EvaluateAsync(Guid id, KnowledgeEvaluationRequest request, CancellationToken ct)
    {
    // 先核对全部标注所属固定版本；每题仅向量化一次供策略复用，任何状态变化都阻止整批交付。
        if (request.Cases is not { Length: >= 1 and <= 16 } || request.Cases.Any(item => item is null
            || string.IsNullOrWhiteSpace(item.Query) || item.Query.Length > 2000 || item.RelevantChunkIds is not { Length: >= 1 and <= 32 }
            || item.RelevantChunkIds.Distinct().Count() != item.RelevantChunkIds.Length))
            throw new KnowledgeException("评测需要 1–16 个问题，每题标注 1–32 个当前版本的相关片段。");
        var library = await repository.FindAsync(id, ct);
        if (library is not { Enabled: true, ActiveVersion: { } active }) throw new KnowledgeException("请先激活可用的知识版本。");
        var version = await repository.FindReadableVersionAsync(id, active, ct) ?? throw new KnowledgeException("固定版本不可用。");
        if (version.IndexSchema != "hybrid-v1") throw new KnowledgeException("对照评测需要已激活的混合索引。");
        if (request.IncludeRerank && request.Settings.RerankModelId is null) throw new KnowledgeException("请先选择重排序模型再确认调用。");
        var baseline = request.Settings with { Mode = "vector", RerankModelId = null, MinRerankScore = null, ExactMatch = "off" };
        var hybrid = request.Settings with { Mode = "hybrid", RerankModelId = null, MinRerankScore = null };
        var strategies = new List<(string Name, KnowledgeRetrievalSettings Settings, string? Snapshot)>
        {
            ("vector", baseline, await retrieval.CaptureAsync(version, baseline, ct)),
            ("hybrid", hybrid, await retrieval.CaptureAsync(version, hybrid, ct)),
        };
        if (request.IncludeRerank)
        {
            var ranked = request.Settings with { Mode = "hybrid" };
            strategies.Add(("hybrid_rerank", ranked, await retrieval.CaptureAsync(version, ranked, ct)));
        }
        foreach (var item in request.Cases)
            if ((await repository.ReadChunksAsync(id, active, item.RelevantChunkIds, null, 0, item.RelevantChunkIds.Length, ct)).Length != item.RelevantChunkIds.Length)
                throw new KnowledgeException("评测标注包含不属于当前固定版本的片段。");
        var outputs = strategies.ToDictionary(strategy => strategy.Name, _ => new List<KnowledgeSearchResponse>());
        var elapsed = strategies.ToDictionary(strategy => strategy.Name, _ => 0L);
        foreach (var item in request.Cases)
        {
            await Authorize(ct);
            var embedded = await embedding.EmbedAsync(version.ModelSnapshot, [new("query", item.Query)], "query", ct);
            if (!embedded.Ok || embedded.Vectors.Count != 1 || embedded.Vectors[0].Id != "query")
                throw new KnowledgeException(embedded.Error ?? "评测问题向量化失败，未自动重试。");
            await Authorize(ct);
            foreach (var strategy in strategies)
            {
                var started = Stopwatch.GetTimestamp();
                outputs[strategy.Name].Add(await retrieval.SearchAsync(version, item.Query, null, strategy.Settings, strategy.Snapshot,
                    Authorize, ct, evaluationVector: embedded.Vectors[0].Values));
                elapsed[strategy.Name] += (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }
        }
        await Authorize(ct);
        return new(active, strategies.Select(strategy =>
        {
            var results = outputs[strategy.Name].ToArray();
            var metrics = results.Select((result, i) => Metrics(result.Hits.Where(hit => hit.Role == "primary").Select(hit => hit.Chunk.Id).ToArray(),
                request.Cases[i].RelevantChunkIds, strategy.Settings.TopK)).ToArray();
            return new KnowledgeEvaluationMetric(strategy.Name, metrics.Average(metric => metric.Recall), metrics.Average(metric => metric.Mrr),
                metrics.Average(metric => metric.Ndcg), elapsed[strategy.Name], results);
        }).ToArray());

        // 每次外部调用和交付前复验库的启用状态、版本摘要及冻结重排配置。
        async Task Authorize(CancellationToken token)
        {
            if (await repository.FindAsync(id, token) is not { Enabled: true } current || current.ActiveVersion != active
                || await repository.FindReadyDigestAsync(id, active, token) != version.Digest)
                throw new KnowledgeException("评测期间知识库状态或激活版本改变，结果未交付。");
            foreach (var strategy in strategies) await retrieval.ValidateRerankerAsync(strategy.Snapshot, token);
        }
    }

    /// <summary>仅计算主命中，不用相邻支持片段虚增召回或排名；相关性采用人工二元标注。</summary>
    internal static (double Recall, double Mrr, double Ndcg) Metrics(Guid[] ranked, Guid[] relevant, int topK)
    {
        var truth = relevant.ToHashSet(); var hits = ranked.Select((id, i) => (Relevant: truth.Contains(id), Rank: i + 1)).ToArray();
        var first = hits.FirstOrDefault(item => item.Relevant).Rank;
        var dcg = hits.Where(item => item.Relevant).Sum(item => 1 / Math.Log2(item.Rank + 1));
        var ideal = Enumerable.Range(1, Math.Min(topK, truth.Count)).Sum(rank => 1 / Math.Log2(rank + 1));
        return ((double)hits.Count(item => item.Relevant) / truth.Count, first == 0 ? 0 : 1d / first, ideal == 0 ? 0 : dcg / ideal);
    }
}
