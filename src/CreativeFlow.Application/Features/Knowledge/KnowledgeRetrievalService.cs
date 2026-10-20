using System.Diagnostics;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Contracts.Ai;

namespace CreativeFlow.Application.Features.Knowledge;

/// <summary>管理测试与工作流唯一的检索管线；授权在外部调用和交付前复验，正文只从数据库取回。</summary>
public sealed class KnowledgeRetrievalService(IKnowledgeRepository repository, IKnowledgeEmbedding embedding,
    IKnowledgeIndexStore index, IKnowledgeReranker reranker)
{
    /// <summary>冻结重排序配置且验证索引能力，提交时不调用向量或重排序模型。</summary>
    public async Task<string?> CaptureAsync(KnowledgeVersion version, KnowledgeRetrievalSettings settings, CancellationToken ct)
    {
        if (KnowledgeRetrievalPolicy.Validate(settings) is { } error) throw new KnowledgeException(error);
        if (version.IndexSchema != "hybrid-v1" && (settings.Mode != "vector" || settings.ExactMatch == "require"))
            throw new KnowledgeException("该固定版本只有向量索引；请在知识库版本管理中重建混合索引，再明确选择新版本。");
        return settings.RerankModelId is { } id ? await reranker.CaptureAsync(id, ct) : null;
    }

    /// <summary>复用或继续运行时检查重排序模型仍属于相同冻结配置，禁用和配置变化不允许降级绕过。</summary>
    public async Task ValidateRerankerAsync(string? snapshot, CancellationToken ct)
    {
        if (snapshot is null) return;
        var saved = System.Text.Json.Nodes.JsonNode.Parse(snapshot)!;
        if (!System.Text.Json.Nodes.JsonNode.DeepEquals(saved, System.Text.Json.Nodes.JsonNode.Parse(await reranker.CaptureAsync(Guid.Parse(saved["modelId"]!.GetValue<string>()), ct))))
            throw new KnowledgeException("重排序模型已停用或配置已变化，不能执行或复用该运行。");
    }

    /// <summary>召回与排序不会生成回答；重排序失败仅按节点显式策略中止或记录降级，不自动重试。</summary>
    public async Task<KnowledgeSearchResponse> SearchAsync(KnowledgeVersion version, string query, string? originalQuestion,
        KnowledgeRetrievalSettings settings, string? rerankSnapshot, Func<CancellationToken, Task> authorize,
        CancellationToken ct, Func<double, CancellationToken, Task>? progress = null, string? nodeId = null, float[]? evaluationVector = null)
    {
    // 调用上游前校验策略和冻结快照；候选正文只能从授权版本读取，外部调用后及交付前再次复验权限。
        if (string.IsNullOrWhiteSpace(query) || query.Length > 2000 || originalQuestion is not null
            && (string.IsNullOrWhiteSpace(originalQuestion) || originalQuestion.Length > 2000)) throw new KnowledgeException("检索词与原始问题应为 1–2000 字符。");
        if (KnowledgeRetrievalPolicy.Validate(settings) is { } error) throw new KnowledgeException(error);
        if (version.IndexSchema != "hybrid-v1" && (settings.Mode != "vector" || settings.ExactMatch == "require"))
            throw new KnowledgeException("该版本尚无关键词索引，请重建混合索引并明确选择新版本。");
        if ((settings.RerankModelId is null) != (rerankSnapshot is null)) throw new KnowledgeException("重排序配置快照缺失或不匹配。");
        if (rerankSnapshot is not null && System.Text.Json.Nodes.JsonNode.Parse(rerankSnapshot)?["modelId"]?.GetValue<string>() != settings.RerankModelId?.ToString())
            throw new KnowledgeException("重排序模型与冻结配置不匹配。");
        var terms = settings.ExactMatch == "off" ? [] : KnowledgeIdentifiers.Extract(query + "\n" + originalQuestion);
        if (terms.Length > 16 || settings.ExactMatch == "require" && terms.Length == 0) throw new KnowledgeException("精确匹配需要 1–16 个字段名、编码或引号中的标识。");
        var started = Stopwatch.GetTimestamp();
        await authorize(ct); await ValidateRerankerAsync(rerankSnapshot, ct);
        var recalled = await RecallAsync(version, query, originalQuestion, terms, settings, authorize, ct, progress, evaluationVector);
        await authorize(ct);
        var ids = recalled.Candidates.Select(item => item.Id).ToArray();
        var rows = ids.Length == 0 ? [] : await repository.ReadChunksAsync(version.BaseId, version.Id, ids, null, 0, ids.Length, ct);
        if (rows.Length != ids.Length || rows.Select(row => row.Id).Distinct().Count() != ids.Length) throw new KnowledgeException("候选片段与授权版本不一致，未发送正文。");
        var chunks = rows.ToDictionary(row => row.Id);
        VerifyExact(recalled, chunks, terms, settings.ExactMatch == "require");
        await authorize(ct);
        if (progress is not null) await progress(0.5, ct);
        var ranked = await RankAsync(recalled.Candidates, chunks, originalQuestion ?? query, settings, rerankSnapshot, authorize, ct, progress, nodeId);
        await authorize(ct);
        var selected = await SelectAsync(version, ranked.Candidates, chunks, settings, ranked.ScoreType, ct);
        await authorize(ct);
        await ValidateRerankerAsync(rerankSnapshot, ct);
        var selectedIds = selected.Hits.Select(hit => hit.Chunk.Id).ToHashSet();
        var candidateTrace = recalled.Candidates.Select(item => ranked.Candidates.FirstOrDefault(candidate => candidate.Id == item.Id) ?? item)
            .Select(item => item with { Selected = selectedIds.Contains(item.Id), Reason = selectedIds.Contains(item.Id) ? "selected"
                : item.RerankRank is not null && item.RerankScore < settings.MinRerankScore ? "rerank_threshold"
                : settings.RerankModelId is not null && ranked.Status == "completed" && item.RerankRank is null ? "rerank_candidate_limit" : "topk_or_context_limit" }).ToList();
        candidateTrace.AddRange(selected.Hits.Where(hit => hit.Role == "supporting").Select(hit => new KnowledgeCandidateTrace(hit.Chunk.Id) { Selected = true, Reason = "neighbor" }));
        var trace = new KnowledgeRetrievalTrace(settings.Mode, query, originalQuestion)
        {
            DenseCount = recalled.DenseCount, KeywordCount = recalled.KeywordCount, ExactCount = recalled.ExactCount,
            FusedCount = recalled.Candidates.Length, RerankedCount = ranked.Status == "completed" ? ranked.Candidates.Length : 0,
            DeliveredCount = selected.Hits.Length, RerankStatus = ranked.Status, RerankError = ranked.Error,
            RerankModelId = settings.RerankModelId?.ToString(), RerankConfigurationVersion = SnapshotVersion(rerankSnapshot),
            RerankTokens = ranked.Tokens, EmbeddingMilliseconds = recalled.EmbeddingMs, RecallMilliseconds = recalled.RecallMs,
            RerankMilliseconds = ranked.ElapsedMs, TotalMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            Candidates = candidateTrace.ToArray(),
        };
        if (progress is not null) await progress(0.98, ct);
        return selected with { Trace = trace };
    }

    /// <summary>并行召回同一固定版本内的语义、关键词和完整标识候选，关键词模式跳过向量模型。</summary>
    private async Task<RecallResult> RecallAsync(KnowledgeVersion version, string query, string? original, string[] terms,
        KnowledgeRetrievalSettings settings, Func<CancellationToken, Task> authorize, CancellationToken ct, Func<double, CancellationToken, Task>? progress, float[]? evaluationVector)
    {
    // 关键词模式跳过收费向量化，各路并行请求仍使用相同资源过滤；不同分数通过名次融合而非直接相加。
        float[]? vector = null; long embeddingMs = 0;
        if (progress is not null) await progress(0.05, ct);
        if (settings.Mode != "keyword" && evaluationVector is not null) vector = evaluationVector;
        else if (settings.Mode != "keyword")
        {
            var clock = Stopwatch.GetTimestamp();
            var embedded = await embedding.EmbedAsync(version.ModelSnapshot, [new("query", query)], "query", ct);
            embeddingMs = (long)Stopwatch.GetElapsedTime(clock).TotalMilliseconds;
            if (!embedded.Ok || embedded.Vectors.Count != 1 || embedded.Vectors[0].Id != "query")
                throw new KnowledgeException(embedded.Error ?? "检索词向量化未确认成功，未自动重试。");
            vector = embedded.Vectors[0].Values;
            await authorize(ct);
        }
        if (progress is not null) await progress(0.25, ct);
        var started = Stopwatch.GetTimestamp();
        var required = settings.ExactMatch == "require" ? terms : [];
        var denseTask = vector is null ? Task.FromResult<KnowledgeIndexHit[]>([]) : version.IndexSchema == "hybrid-v1"
            ? index.SearchDenseAsync(version.BaseId, version.Id, vector, settings.DenseCandidates, required, ct)
            : index.SearchAsync(version.BaseId, version.Id, vector, Math.Min(32, settings.DenseCandidates), ct);
        var keywordTask = settings.Mode == "vector" ? Task.FromResult<KnowledgeIndexHit[]>([])
            : index.SearchKeywordsAsync(version.BaseId, version.Id, original is null || original == query ? query : query + "\n" + original, settings.KeywordCandidates, required, ct);
        var exactTask = version.IndexSchema == "hybrid-v1" && terms.Length > 0 && (settings.Mode != "vector" || settings.ExactMatch == "require")
            ? index.SearchExactAsync(version.BaseId, version.Id, terms, settings.KeywordCandidates, settings.ExactMatch == "require", ct)
            : Task.FromResult<KnowledgeIndexHit[]>([]);
        await Task.WhenAll(denseTask, keywordTask, exactTask);
        var dense = await denseTask; var keyword = await keywordTask; var exact = await exactTask;
        ValidateHits(dense, settings.DenseCandidates, cosine: true); ValidateHits(keyword, settings.KeywordCandidates, cosine: false); ValidateHits(exact, settings.KeywordCandidates, cosine: false);
        dense = dense.Where(hit => hit.Score >= settings.MinVectorScore).ToArray();
        var candidates = Fuse(dense, keyword, exact, settings.Mode);
        return new(candidates, dense.Length, keyword.Length, exact.Length, embeddingMs, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    /// <summary>以排名融合而非原始分数相加；精确命中统一提升，不按 scroll 的 UUID 顺序制造相关性。</summary>
    internal static KnowledgeCandidateTrace[] Fuse(KnowledgeIndexHit[] dense, KnowledgeIndexHit[] keyword, KnowledgeIndexHit[] exact, string mode)
    {
        var denseRanks = dense.Select((hit, i) => (hit.Id, Rank: i + 1, hit.Score)).ToDictionary(item => item.Id);
        var keywordRanks = keyword.Select((hit, i) => (hit.Id, Rank: i + 1, hit.Score)).ToDictionary(item => item.Id);
        var exactIds = exact.Select(hit => hit.Id).ToHashSet();
        return dense.Select(hit => hit.Id).Concat(keyword.Select(hit => hit.Id)).Concat(exactIds).Distinct().Select(id =>
        {
            var d = denseRanks.GetValueOrDefault(id); var k = keywordRanks.GetValueOrDefault(id);
            var score = (d.Rank > 0 ? 1d / (60 + d.Rank) : 0) + (k.Rank > 0 ? 1d / (60 + k.Rank) : 0) + (exactIds.Contains(id) ? 1d / 61 : 0);
            return new KnowledgeCandidateTrace(id) { DenseRank = d.Rank > 0 ? d.Rank : null, KeywordRank = k.Rank > 0 ? k.Rank : null,
                DenseScore = d.Rank > 0 ? d.Score : null, KeywordScore = k.Rank > 0 ? k.Score : null, FusionScore = score, ExactMatched = exactIds.Contains(id) };
        }).OrderByDescending(item => mode == "vector" && exact.Length == 0 ? item.DenseScore : item.FusionScore)
            .ThenBy(item => mode == "vector" && exact.Length == 0 ? item.DenseRank : 0)
            .ThenBy(item => item.Id).Select((item, i) => item with { FusionRank = i + 1 }).ToArray();
    }

    /// <summary>验证冻结配置后仅调用一次，将稳定候选编号与完整正文对应，并记录结果。</summary>
    private async Task<RankResult> RankAsync(KnowledgeCandidateTrace[] candidates, Dictionary<Guid, KnowledgeChunk> chunks,
        string query, KnowledgeRetrievalSettings settings, string? snapshot, Func<CancellationToken, Task> authorize,
        CancellationToken ct, Func<double, CancellationToken, Task>? progress, string? nodeId)
    {
        var scoreType = settings.Mode == "vector" ? "vector" : settings.Mode == "keyword" ? "keyword" : "fusion";
        if (snapshot is null) return new(candidates, "disabled", null, scoreType, 0, null);
        if (candidates.Length == 0) return new([], "no_candidates", null, scoreType, 0, null);
        var pool = candidates.Where(candidate => candidate.ExactMatched).Take(Math.Max(1, settings.RerankCandidates / 4))
            .Concat(candidates).DistinctBy(candidate => candidate.Id).Take(settings.RerankCandidates).ToArray();
        await authorize(ct);
        if (progress is not null) await progress(0.65, ct);
        var started = Stopwatch.GetTimestamp();
        var result = await reranker.RankAsync(snapshot, query, pool.Select(item => chunks[item.Id].SectionPath + "\n" + chunks[item.Id].Text).ToArray(), ct, nodeId);
        var elapsed = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        await authorize(ct);
        if (!result.Ok)
        {
            if (settings.RerankFailurePolicy == "fail") throw new KnowledgeException(result.Error ?? "重排序失败，未自动重试。");
            return new(candidates, "fallback", result.Error ?? "重排序失败，已按配置降级为融合排名。", scoreType, elapsed, result.TotalTokens);
        }
        if (result.Scores.Length != pool.Length || result.Scores.Select(item => item.Index).Distinct().Count() != pool.Length
            || result.Scores.Any(item => item.Index < 0 || item.Index >= pool.Length || !double.IsFinite(item.Score) || item.Score is < 0 or > 1))
            throw new KnowledgeException("重排序候选编号和分数无效，未交付任何正文。");
        var ranked = result.Scores.OrderByDescending(item => item.Score).ThenBy(item => item.Index)
            .Select((item, i) => pool[item.Index] with { RerankRank = i + 1, RerankScore = item.Score }).ToArray();
        return new(ranked, "completed", null, "rerank", elapsed, result.TotalTokens);
    }

    /// <summary>先保留主命中，再补充同文档相邻片段；按数量裁剪且不截断正文。</summary>
    private async Task<KnowledgeSearchResponse> SelectAsync(KnowledgeVersion version, KnowledgeCandidateTrace[] ranked,
        Dictionary<Guid, KnowledgeChunk> chunks, KnowledgeRetrievalSettings settings, string scoreType, CancellationToken ct)
    {
        var primary = ranked.Where(item => settings.MinRerankScore is null || item.RerankScore is null || item.RerankScore >= settings.MinRerankScore)
            .Take(settings.TopK).ToArray();
        var hits = primary.Select(item => new KnowledgeSearchHit(chunks[item.Id], Score(item, scoreType)) { ScoreType = scoreType }).ToList();
        if (primary.Length > 0 && settings.NeighborRadius > 0)
        {
            var adjacent = await repository.ReadAdjacentChunksAsync(version.BaseId, version.Id, primary.Select(item => item.Id).ToArray(), settings.NeighborRadius, ct);
            var seen = hits.Select(hit => hit.Chunk.Id).ToHashSet();
            foreach (var row in adjacent)
            {
                if (!seen.Add(row.Id)) continue;
                var nearest = hits.Where(hit => hit.Role == "primary" && row.DocumentId is not null && row.DocumentId == hit.Chunk.DocumentId)
                    .OrderBy(hit => Math.Abs(hit.Chunk.Ordinal - row.Ordinal)).FirstOrDefault();
                if (nearest is null) continue;
                var score = scoreType == "vector" ? Math.Clamp(nearest.Score - .02 * Math.Abs(row.Ordinal - nearest.Chunk.Ordinal), -1, 1) : nearest.Score;
                hits.Add(new(row, score) { Role = "supporting", ScoreType = scoreType });
            }
        }
        return new(version.Id, hits.Take(settings.MaxContextChunks).ToArray())
        { MaxContextChunks = settings.MaxContextChunks, OmittedCount = Math.Max(0, hits.Count - settings.MaxContextChunks) };
    }

    /// <summary>根据实际策略选择对应分数，绝不混用 Cosine、BM25、RRF 和重排尺度。</summary>
    private static double Score(KnowledgeCandidateTrace item, string type) => type switch
    { "vector" => item.DenseScore ?? 0, "keyword" => item.KeywordScore ?? 0, "rerank" => item.RerankScore ?? 0, _ => item.FusionScore ?? 0 };
    /// <summary>从非秘密快照读取重排参数版本，供运行诊断核对。</summary>
    private static string? SnapshotVersion(string? snapshot) => snapshot is null ? null : System.Text.Json.Nodes.JsonNode.Parse(snapshot)?["configurationVersion"]?.GetValue<string>();
    /// <summary>拒绝重复 ID、超量结果、非有限数值或错误分数尺度。</summary>
    private static void ValidateHits(KnowledgeIndexHit[] hits, int limit, bool cosine)
    {
        if (hits.Length > limit || hits.Select(hit => hit.Id).Distinct().Count() != hits.Length || hits.Any(hit => !double.IsFinite(hit.Score)
            || cosine && (hit.Score is < -1.000001 or > 1.000001) || !cosine && hit.Score < 0)) throw new KnowledgeException("候选 ID、数量或分数尺度无效。");
    }
    /// <summary>以授权数据库中的完整正文复核精确标识，防止错误索引绕过必须匹配条件。</summary>
    private static void VerifyExact(RecallResult recalled, Dictionary<Guid, KnowledgeChunk> chunks, string[] terms, bool required)
    {
        foreach (var item in recalled.Candidates.Where(item => item.ExactMatched || required))
        {
            var actual = KnowledgeIdentifiers.Extract(chunks[item.Id].SectionPath + "\n" + chunks[item.Id].Text).ToHashSet(StringComparer.Ordinal);
            if (required ? terms.Any(term => !actual.Contains(term)) : !terms.Any(actual.Contains)) throw new KnowledgeException("精确标识索引与授权正文不一致，未交付片段。");
        }
    }
    /// <param name="Candidates">去重融合后的候选诊断。</param>
    /// <param name="DenseCount">通过阈值的向量候选数。</param>
    /// <param name="KeywordCount">关键词候选数。</param>
    /// <param name="ExactCount">完整标识候选数。</param>
    /// <param name="EmbeddingMs">问题向量化耗时。</param>
    /// <param name="RecallMs">并行召回耗时。</param>
    private sealed record RecallResult(KnowledgeCandidateTrace[] Candidates, int DenseCount, int KeywordCount, int ExactCount, long EmbeddingMs, long RecallMs);
    /// <param name="Candidates">最终候选顺序及各路分数。</param>
    /// <param name="Status">完成、未启用、无候选或降级状态。</param>
    /// <param name="Error">安全失败说明。</param>
    /// <param name="ScoreType">当前排序的分数尺度。</param>
    /// <param name="ElapsedMs">重排耗时。</param>
    /// <param name="Tokens">上游报告的 token 数。</param>
    private sealed record RankResult(KnowledgeCandidateTrace[] Candidates, string Status, string? Error, string ScoreType, long ElapsedMs, long? Tokens);
}
