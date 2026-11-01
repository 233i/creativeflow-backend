using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Application.Features.Knowledge;
using CreativeFlow.Infrastructure.Persistence;

namespace CreativeFlow.UnitTests.Support;

/// <summary>知识测试独占 PostgreSQL schema；模型与 Qdrant 均为可控替身。</summary>
internal sealed class KnowledgeFixture : IAsyncDisposable
{
    /// <summary>测试创建并释放的随机数据库范围。</summary>
    public ImageJobDatabase Database { get; private set; } = null!;
    /// <summary>验证真实迁移与 SQL 的生产仓储。</summary>
    public KnowledgeRepository Repository { get; private set; } = null!;
    /// <summary>可注入未知状态的向量模型。</summary>
    public Embeddings Model { get; } = new();
    /// <summary>可注入写失败及越权候选的向量存储。</summary>
    public Index Storage { get; } = new();
    /// <summary>生产知识管理用例。</summary>
    public KnowledgeLibraryService Service => new(Repository, Model, Storage, Retrieval, Quotas);
    /// <summary>后台与工作流共享的真实检索管线，外部网络只用替身。</summary>
    public KnowledgeRetrievalService Retrieval => new(Repository, Model, Storage, Reranking);
    /// <summary>记录实际单次调用并允许注入明确失败或配置变更。</summary>
    public Reranker Reranking { get; } = new();
    /// <summary>测试用部署预算，默认保持历史试点值。</summary>
    public KnowledgeQuotas Quotas { get; private set; } = new();
    /// <summary>生产检查点推进器。</summary>
    public KnowledgeIndexProcessor Processor => new(Repository, Model, Storage);
    /// <summary>合成模型 ID，不读取任何已有渠道。</summary>
    public Guid ModelId { get; } = Guid.NewGuid();
    /// <summary>当前测试用库。</summary>
    public KnowledgeBaseResponse Library { get; private set; } = null!;

    /// <summary>应用全部增量迁移，创建一份无敏感数据的模型和知识库。</summary>
    public static async Task<KnowledgeFixture> CreateAsync(KnowledgeQuotas? quotas = null, int? migrationsThrough = null)
    {
        var fixture = new KnowledgeFixture { Database = await ImageJobDatabase.CreateAsync(migrationsThrough), Quotas = quotas ?? new() };
        try
        {
            var provider = Guid.NewGuid();
            await fixture.Database.SqlAsync($$"""
                INSERT INTO ai_provider(provider_id,name,type,base_url) VALUES ('{{provider}}','test','openai-embeddings','http://127.0.0.1:1234/v1');
                INSERT INTO ai_model(model_id,provider_id,model_key,name,type,category) VALUES ('{{fixture.ModelId}}','{{provider}}','synthetic','test','openai-embeddings','embedding');
                """);
            fixture.Repository = new(fixture.Database.Source, fixture.Quotas);
            if (migrationsThrough is < 47)
            {
                var id = Guid.NewGuid();
                await fixture.Database.SqlAsync($"INSERT INTO knowledge_base(id,name,description,model_id) VALUES ('{id}','合成手册','test','{fixture.ModelId}')");
                fixture.Library = new(id, "合成手册", "test", fixture.ModelId, true, null);
            }
            else fixture.Library = await fixture.Service.CreateAsync(new("合成手册", "test", fixture.ModelId), TestContext.Current.CancellationToken);
            return fixture;
        }
        catch { await fixture.Database.DisposeAsync(); throw; }
    }

    /// <summary>通过真实预览摘要确认一个可控制分段数量的合成版本。</summary>
    public Task<KnowledgeVersionResponse> ImportAsync(string text = "CreativeFlow 合成产品手册：保修期限为两年。")
    {
        var input = new KnowledgeImportRequest([new("manual.md", text)], 200, 0);
        return Service.ImportAsync(Library.Id, input with { PreviewDigest = Service.Preview(input).Digest }, TestContext.Current.CancellationToken);
    }

    /// <summary>释放本实例创建的数据库，不清理共享开发或向量集合。</summary>
    public ValueTask DisposeAsync() => Database.DisposeAsync();

    /// <summary>只返回有限三维向量，记录每个批次以断言未重复调用。</summary>
    internal sealed class Embeddings : IKnowledgeEmbedding
    {
        /// <summary>向量请求计数。</summary>
        public int Calls { get; private set; }
        /// <summary>记录用于检索的实际文本，不把入库正文当作查询。</summary>
        public string? LastQuery { get; private set; }
        /// <summary>用于模拟模型配置变更。</summary>
        public int Revision { get; set; } = 1;
        /// <summary>指定调用次序失败，验证已成功批次保留。</summary>
        public int FailOnCall { get; set; }
        /// <summary>模拟本地超时、并发背压或 HTTP 错误。</summary>
        public string FailureCode { get; set; } = "timeout_unknown";
        /// <summary>返回不含凭据且维度已固定的合成配置。</summary>
        public Task<string> CaptureAsync(Guid modelId, CancellationToken ct) => Task.FromResult(new JsonObject
        { ["dimensions"] = 3, ["batchSize"] = 1, ["revision"] = Revision }.ToJsonString());
        /// <summary>返回稳定顺序和 ID，失败不在替身内部重试。</summary>
        public Task<EmbeddingResult> EmbedAsync(string snapshot, IReadOnlyList<EmbeddingInput> inputs, string purpose, CancellationToken ct)
        {
            Calls++;
            if (purpose == "query") LastQuery = inputs.Single().Text;
            return Task.FromResult(Calls == FailOnCall ? EmbeddingResult.Failure(FailureCode, "synthetic failure")
                : new EmbeddingResult(true, inputs.Select(input => new EmbeddingVector(input.Id, [1, 0, 0])).ToArray(), 3, "synthetic", 1, 200, null, null));
        }
    }

    /// <summary>按版本隔离的幂等内存索引，支持部分写入故障。</summary>
    internal sealed class Index : IKnowledgeIndexStore
    {
        /// <summary>同一固定库与版本的合成片段，用于索引升级和真实正文校验。</summary>
        public Dictionary<Guid, Dictionary<Guid, KnowledgeChunk>> Chunks { get; } = [];
        /// <summary>可覆盖的合成语义召回结果，不设置时使用测试默认相似度。</summary>
        public KnowledgeIndexHit[]? DenseHits { get; set; }
        /// <summary>可覆盖的合成 BM25 结果，验证不同分数尺度不能混用。</summary>
        public KnowledgeIndexHit[]? KeywordHits { get; set; }
        /// <summary>可覆盖的完整标识候选，用于验证归属和真实正文复核。</summary>
        public KnowledgeIndexHit[]? ExactHits { get; set; }
        /// <summary>替身配置可用性。</summary>
        public bool Available { get; set; } = true;
        /// <summary>每个版本的稳定片段 ID。</summary>
        public Dictionary<Guid, HashSet<Guid>> Points { get; } = [];
        /// <summary>记录实际写调用。</summary>
        public int Writes { get; private set; }
        /// <summary>写入部分点后报告失败。</summary>
        public bool FailWrite { get; set; }
        /// <summary>故意返回不属于该版本的 ID。</summary>
        public bool ForeignHit { get; set; }
        /// <summary>搜索返回前修改资源，用于验证撤权竞态。</summary>
        public Func<Task>? OnSearch { get; set; }
        /// <summary>测试删除失败后必须保留数据库内容及显式重试。</summary>
        public bool FailDelete { get; set; }
        /// <summary>仅移除测试当前版本的集合，重复删除也成功。</summary>
        public Task DeleteAsync(Guid library, Guid version, CancellationToken ct)
        {
            if (FailDelete) throw new KnowledgeException("synthetic cleanup failure");
            Points.Remove(version); return Task.CompletedTask;
        }
        /// <summary>仅创建当前版本的集合，不改变其他版本。</summary>
        public Task PrepareAsync(Guid library, Guid version, int dimensions, CancellationToken ct)
        { Points.TryAdd(version, []); return Task.CompletedTask; }
        /// <summary>准备测试候选索引并沿用既有故障注入策略。</summary>
        public Task PrepareHybridAsync(Guid library, Guid version, int dimensions, CancellationToken ct) => PrepareAsync(library, version, dimensions, ct);
        /// <summary>只统计指定库与版本中同时存在语义和 BM25 向量的点。</summary>
        public Task<int> CountHybridAsync(Guid library, Guid version, CancellationToken ct) => CountAsync(library, version, ct);
        /// <summary>以稳定 ID 保存合成片段与向量，支持索引写入故障注入。</summary>
        public Task WriteHybridAsync(Guid library, Guid version, IReadOnlyList<EmbeddingVector> vectors, IReadOnlyList<KnowledgeChunk> chunks, CancellationToken ct)
        {
            if (!Chunks.TryGetValue(version, out var stored)) Chunks[version] = stored = [];
            foreach (var chunk in chunks) stored[chunk.Id] = chunk;
            return WriteAsync(library, version, vectors, ct);
        }
        /// <summary>验证查询向量后在同一库、版本和全部必需标识范围内召回语义候选。</summary>
        public async Task<KnowledgeIndexHit[]> SearchDenseAsync(Guid library, Guid version, float[] query, int limit, string[] requiredTerms, CancellationToken ct)
        {
            var result = DenseHits ?? await SearchAsync(library, version, query, limit, ct);
            return result.Where(hit => requiredTerms.Length == 0 || HasTerms(version, hit.Id, requiredTerms, true)).Take(limit).ToArray();
        }
        /// <summary>使用与入库相同的多语言 BM25，限定查询长度和候选范围。</summary>
        public async Task<KnowledgeIndexHit[]> SearchKeywordsAsync(Guid library, Guid version, string query, int limit, string[] requiredTerms, CancellationToken ct)
        {
            if (OnSearch is not null) await OnSearch();
            var hits = KeywordHits ?? Chunks.GetValueOrDefault(version, []).Values.Where(chunk => chunk.Text.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select(chunk => new KnowledgeIndexHit(chunk.Id, 3)).ToArray();
            return hits.Where(hit => requiredTerms.Length == 0 || HasTerms(version, hit.Id, requiredTerms, true)).Take(limit).ToArray();
        }
        /// <summary>按完整标识召回指定版本候选，支持任一或全部匹配，不将 ID 顺序当相关性。</summary>
        public Task<KnowledgeIndexHit[]> SearchExactAsync(Guid library, Guid version, string[] terms, int limit, bool requireAll, CancellationToken ct) =>
            Task.FromResult((ExactHits ?? Points[version].Where(id => HasTerms(version, id, terms, requireAll)).Select(id => new KnowledgeIndexHit(id, 1)).ToArray()).Take(limit).ToArray());
        /// <summary>从合成完整正文验证全部必需标识，不只信任预设候选标签。</summary>
        private bool HasTerms(Guid version, Guid id, string[] terms, bool all)
        {
            if (!Chunks.GetValueOrDefault(version, []).TryGetValue(id, out var chunk)) return false;
            var found = KnowledgeIdentifiers.Extract(chunk.SectionPath + "\n" + chunk.Text).ToHashSet();
            return all ? terms.All(found.Contains) : terms.Any(found.Contains);
        }
        /// <summary>稳定 ID upsert 后注入可恢复写失败。</summary>
        public Task WriteAsync(Guid library, Guid version, IReadOnlyList<EmbeddingVector> vectors, CancellationToken ct)
        {
            Writes++; foreach (var vector in vectors) Points[version].Add(Guid.Parse(vector.Id));
            if (FailWrite) throw new KnowledgeException("synthetic write failure");
            return Task.CompletedTask;
        }
        /// <summary>按版本计算精确数量。</summary>
        public Task<int> CountAsync(Guid library, Guid version, CancellationToken ct) => Task.FromResult(Points[version].Count);
        /// <summary>返回受控候选或故意注入的越权 ID。</summary>
        public async Task<KnowledgeIndexHit[]> SearchAsync(Guid library, Guid version, float[] query, int topK, CancellationToken ct)
        {
            if (OnSearch is not null) await OnSearch();
            return ForeignHit ? [new(Guid.NewGuid(), 1)] : Points[version].Take(topK).Select(id => new KnowledgeIndexHit(id, 1)).ToArray();
        }
    }

    /// <summary>合成重排序返回受控候选排名；不会调用实际服务或包含任何密钥。</summary>
    internal sealed class Reranker : IKnowledgeReranker
    {
        /// <summary>实际模型提交计数，用于断言没有隐藏重试。</summary>
        public int Calls { get; private set; }
        /// <summary>可更改的合成版本号，测试快照失效后禁止执行和复用。</summary>
        public int Revision { get; set; } = 1;
        /// <summary>合成模型开关，用于测试停用后的执行被拒绝。</summary>
        public bool Enabled { get; set; } = true;
        /// <summary>下次合成调用返回的成功或失败结果。</summary>
        public RerankResult? Result { get; set; }
        /// <summary>外部调用时执行的测试钩子，模拟配置变更或知识库撤权。</summary>
        public Func<Task>? OnRank { get; set; }
        /// <summary>最近一次实际发送到合成重排模型的完整候选正文。</summary>
        public IReadOnlyList<string>? Documents { get; private set; }
        /// <summary>最近一次模型查询，用于核对原始问题与检索词分离。</summary>
        public string? Query { get; private set; }
        /// <summary>冻结可用模型身份、端点及配置版本，不保存明文凭据或触发推理。</summary>
        public Task<string> CaptureAsync(Guid modelId, CancellationToken ct) => !Enabled
            ? throw new KnowledgeException("合成模型已停用。")
            : Task.FromResult(new JsonObject { ["modelId"] = modelId.ToString(), ["configurationVersion"] = Revision.ToString() }.ToJsonString());
        /// <summary>验证冻结配置后仅调用一次，将稳定候选编号与完整正文对应，并记录结果。</summary>
        public async Task<RerankResult> RankAsync(string snapshot, string query, IReadOnlyList<string> documents, CancellationToken ct, string? nodeId = null)
        {
            Calls++; Documents = documents; Query = query;
            if (OnRank is not null) await OnRank();
            return Result ?? new(true, documents.Select((_, i) => new RerankScore(i, 1d / (i + 1))).ToArray(), null, null, 200, 12);
        }
    }
}
