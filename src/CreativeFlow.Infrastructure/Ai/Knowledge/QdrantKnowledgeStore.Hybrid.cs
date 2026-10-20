using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Application.Features.Knowledge;

namespace CreativeFlow.Infrastructure.Ai.Knowledge;

internal sealed partial class QdrantKnowledgeStore
{
    /// <summary>新候选使用命名向量和本地 BM25 推理；已存在的不同布局绝不原地覆盖。</summary>
    public async Task PrepareHybridAsync(Guid library, Guid version, int dimensions, CancellationToken ct)
    {
        if (dimensions is < 1 or > 8192) throw new KnowledgeException("向量维度无效。");
        var path = Collection(version);
        await RequireHybridEngineAsync(ct);
        var existing = await SendAsync(HttpMethod.Get, path, null, ct, allowMissing: true);
        if (existing is null)
            await SendAsync(HttpMethod.Put, path, new JsonObject
            {
                ["vectors"] = new JsonObject { ["dense"] = new JsonObject { ["size"] = dimensions, ["distance"] = "Cosine", ["on_disk"] = true } },
                ["sparse_vectors"] = new JsonObject { ["bm25"] = new JsonObject { ["modifier"] = "idf" } }, ["shard_number"] = 1,
            }, ct);
        else if (ReadPath(existing, "config", "params", "vectors", "dense", "size") is not JsonValue size
            || !size.TryGetValue<int>(out var stored) || stored != dimensions
            || Text(ReadPath(existing, "config", "params", "vectors", "dense", "distance")) != "Cosine"
            || Text(ReadPath(existing, "config", "params", "sparse_vectors", "bm25", "modifier")) != "idf")
            throw new KnowledgeException("候选集合不是匹配的混合索引，禁止覆盖；请创建新候选版本。");
        foreach (var field in new[] { "library", "version", "identifiers" })
            await SendAsync(HttpMethod.Put, path + "/index?wait=true", new JsonObject { ["field_name"] = field, ["field_schema"] = "keyword" }, ct);
    }

    /// <summary>在收费向量化之前确认原生多语言 BM25 能力所需的最低服务版本，旧索引查询不受影响。</summary>
    private async Task RequireHybridEngineAsync(CancellationToken ct)
    {
        var info = await SendAsync(HttpMethod.Get, "/", null, ct);
        var reported = Text(ReadPath(info, "version"));
        if (reported is null || !System.Version.TryParse(reported.Split('-')[0], out var version)
            || version < new System.Version(1, 19, 1))
            throw new KnowledgeException("混合索引需要 Qdrant 1.19.1 或更新版本；尚未调用向量模型，请先升级索引服务。");
    }

    /// <summary>同一幂等写入中提交语义向量、BM25 和标识；Qdrant 在本机计算关键词表示，不另调收费模型。</summary>
    public async Task WriteHybridAsync(Guid library, Guid version, IReadOnlyList<EmbeddingVector> vectors, IReadOnlyList<KnowledgeChunk> chunks, CancellationToken ct)
    {
        if (vectors.Count is < 1 or > 32 || chunks.Count != vectors.Count || chunks.Select(c => c.Id).Distinct().Count() != chunks.Count)
            throw new KnowledgeException("混合索引写入批次无效。");
        var byId = chunks.ToDictionary(c => c.Id.ToString());
        var points = new JsonArray();
        foreach (var vector in vectors)
        {
            if (!byId.TryGetValue(vector.Id, out var chunk) || vector.Values.Length is < 1 or > 8192 || vector.Values.Any(value => !float.IsFinite(value)))
                throw new KnowledgeException("混合索引向量和正文 ID 不一致。");
            points.Add((JsonNode)new JsonObject
            {
                ["id"] = vector.Id,
                ["vector"] = new JsonObject { ["dense"] = new JsonArray(vector.Values.Select(value => (JsonNode)JsonValue.Create(value)!).ToArray()),
                    ["bm25"] = Bm25(chunk.SectionPath + "\n" + chunk.Text) },
                ["payload"] = new JsonObject { ["library"] = library.ToString(), ["version"] = version.ToString(),
                    ["identifiers"] = new JsonArray(KnowledgeIdentifiers.Extract(chunk.SectionPath + "\n" + chunk.Text).Select(term => (JsonNode)JsonValue.Create(term)!).ToArray()) },
            });
        }
        var result = await SendAsync(HttpMethod.Put, Collection(version) + "/points?wait=true", new JsonObject { ["points"] = points }, ct);
        if (Text(ReadPath(result, "status")) != "completed") throw new KnowledgeException("Qdrant 未确认混合索引写入，可从已保存向量检查点重新写入。");
    }

    /// <summary>只统计指定库与版本中同时存在语义和 BM25 向量的点。</summary>
    public async Task<int> CountHybridAsync(Guid library, Guid version, CancellationToken ct)
    {
        var filter = Filter(library, version);
        filter["must"]!.AsArray().Add((JsonNode)new JsonObject { ["has_vector"] = "dense" });
        filter["must"]!.AsArray().Add((JsonNode)new JsonObject { ["has_vector"] = "bm25" });
        var result = await SendAsync(HttpMethod.Post, Collection(version) + "/points/count", new JsonObject { ["filter"] = filter, ["exact"] = true }, ct);
        return ReadPath(result, "count") is JsonValue value && value.TryGetValue<int>(out var count) && count >= 0 ? count
            : throw new KnowledgeException("Qdrant 未确认完整混合索引数量。");
    }

    /// <summary>验证查询向量后在同一库、版本和全部必需标识范围内召回语义候选。</summary>
    public Task<KnowledgeIndexHit[]> SearchDenseAsync(Guid library, Guid version, float[] query, int limit, string[] requiredTerms, CancellationToken ct)
    {
        if (query.Length is < 1 or > 8192 || query.Any(value => !float.IsFinite(value))) throw new KnowledgeException("查询向量无效。");
        return QueryAsync(library, version, new JsonArray(query.Select(value => (JsonNode)JsonValue.Create(value)!).ToArray()), "dense", limit, requiredTerms, ct);
    }

    /// <summary>使用与入库相同的多语言 BM25，限定查询长度和候选范围。</summary>
    public Task<KnowledgeIndexHit[]> SearchKeywordsAsync(Guid library, Guid version, string query, int limit, string[] requiredTerms, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 4001) throw new KnowledgeException("关键词检索词无效。");
        return QueryAsync(library, version, Bm25(query), "bm25", limit, requiredTerms, ct);
    }

    /// <summary>执行有界命名向量查询，不返回向量，不用 payload 代替数据库正文。</summary>
    private async Task<KnowledgeIndexHit[]> QueryAsync(Guid library, Guid version, JsonNode query, string vector, int limit, string[] requiredTerms, CancellationToken ct)
    {
        if (limit is < 1 or > 64 || requiredTerms.Length > 16) throw new KnowledgeException("召回范围无效。");
        var result = await SendAsync(HttpMethod.Post, Collection(version) + "/points/query", new JsonObject
        {
            ["query"] = query, ["using"] = vector, ["filter"] = TermFilter(library, version, requiredTerms),
            ["limit"] = limit, ["with_payload"] = true, ["with_vector"] = false,
        }, ct);
        return ParseHits(result, library, version, limit);
    }

    /// <summary>按完整标识召回指定版本候选，支持任一或全部匹配，不将 ID 顺序当相关性。</summary>
    public async Task<KnowledgeIndexHit[]> SearchExactAsync(Guid library, Guid version, string[] terms, int limit, bool requireAll, CancellationToken ct)
    {
        if (terms.Length is < 1 or > 16 || limit is < 1 or > 64) throw new KnowledgeException("精确标识检索范围无效。");
        var filter = TermFilter(library, version, requireAll ? terms : []);
        if (!requireAll) filter["must"]!.AsArray().Add((JsonNode)new JsonObject { ["key"] = "identifiers", ["match"] = new JsonObject
        { ["any"] = new JsonArray(terms.Select(term => (JsonNode)JsonValue.Create(term)!).ToArray()) } });
        var result = await SendAsync(HttpMethod.Post, Collection(version) + "/points/scroll", new JsonObject
        { ["filter"] = filter, ["limit"] = limit, ["with_payload"] = true, ["with_vector"] = false }, ct);
        return ParseHits(result, library, version, limit, exact: true);
    }

    /// <summary>核对候选数量、唯一性和库版本归属，跨资源或错误分数立即拒绝。</summary>
    private static KnowledgeIndexHit[] ParseHits(JsonNode? result, Guid library, Guid version, int limit, bool exact = false)
    {
        if (ReadPath(result, "points") is not JsonArray points || points.Count > limit) throw new KnowledgeException("候选索引响应无效。");
        var hits = new List<KnowledgeIndexHit>();
        foreach (var point in points)
        {
            var score = exact ? 1 : ReadPath(point, "score") is JsonValue value && value.TryGetValue<double>(out var number) ? number : double.NaN;
            if (Text(ReadPath(point, "payload", "library")) != library.ToString() || Text(ReadPath(point, "payload", "version")) != version.ToString()
                || !Guid.TryParse(Text(ReadPath(point, "id")), out var id) || !double.IsFinite(score) || hits.Any(hit => hit.Id == id))
                throw new KnowledgeException("召回片段归属或分数无效，未交付正文。");
            hits.Add(new(id, score));
        }
        return hits.ToArray();
    }

    /// <summary>入库和查询均采用相同的多语言分词，关闭英文词干和停用词默认值以保护业务词。</summary>
    private static JsonObject Bm25(string text) => new() { ["text"] = text, ["model"] = "qdrant/bm25",
        ["options"] = new JsonObject { ["tokenizer"] = "multilingual", ["stemmer"] = new JsonObject { ["type"] = "none" }, ["stopwords"] = new JsonObject(),
            ["k"] = 1.2, ["b"] = 0.75, ["avg_len"] = 256 } };

    /// <summary>将每个必需标识以 AND 方式加入已有库和固定版本过滤。</summary>
    private static JsonObject TermFilter(Guid library, Guid version, string[] requiredTerms)
    {
        var filter = Filter(library, version);
        foreach (var term in requiredTerms) filter["must"]!.AsArray().Add((JsonNode)new JsonObject { ["key"] = "identifiers", ["match"] = new JsonObject { ["value"] = term } });
        return filter;
    }
}
