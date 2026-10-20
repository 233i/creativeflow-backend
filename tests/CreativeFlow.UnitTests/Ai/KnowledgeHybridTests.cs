using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Application.Features.Knowledge;
using CreativeFlow.UnitTests.Support;

namespace CreativeFlow.UnitTests.Ai;

/// <summary>隔离数据库与受控网络验证完整混合检索、费用边界、固定版本和交付授权。</summary>
[Collection("Knowledge index database lock")]
public sealed class KnowledgeHybridTests
{
    /// <summary>没有显式测试库时不得读取共享开发数据。</summary>
    public static bool NoDatabase => ImageJobDatabase.NoDatabase;
    /// <summary>当前测试取消令牌，避免隔离资源无限等待。</summary>
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>关键词排名尺度可以超过一，纯关键词查询不能产生向量费用。</summary>
    [Fact(Skip = "需要隔离测试库", SkipWhen = nameof(NoDatabase))]
    public async Task KeywordDoesNotEmbedAndPreservesBm25Scale()
    {
        await using var f = await Ready(); var chunks = await Chunks(f);
        f.Storage.KeywordHits = [new(chunks[1].Id, 8.5), new(chunks[0].Id, 4)];
        var calls = f.Model.Calls;
        var result = await Search(f, new() { Mode = "keyword", NeighborRadius = 0, ExactMatch = "off" });
        Assert.Equal(calls, f.Model.Calls); Assert.Equal(chunks[1].Id, result.Hits[0].Chunk.Id);
        Assert.Equal(8.5, result.Hits[0].Score); Assert.Equal("keyword", result.Hits[0].ScoreType);
        Assert.Equal(2, result.Trace!.KeywordCount); Assert.Equal(0, result.Trace.DenseCount);
    }

    /// <summary>两路相同候选按排名融合，BM25 原始数值不压倒向量与精确字段召回。</summary>
    [Fact(Skip = "需要隔离测试库", SkipWhen = nameof(NoDatabase))]
    public async Task FusionProtectsOriginalFieldAndUsesRankInsteadOfRawScores()
    {
        await using var f = await Ready(); var chunks = await Chunks(f);
        f.Storage.DenseHits = [new(chunks[0].Id, .9), new(chunks[1].Id, .8)];
        f.Storage.KeywordHits = [new(chunks[2].Id, 10000), new(chunks[1].Id, 1)];
        var result = await Search(f, new() { Mode = "hybrid", TopK = 1, NeighborRadius = 0 }, "客户编码", "按 customer_code 查询客户");
        Assert.Equal(chunks[1].Id, Assert.Single(result.Hits).Chunk.Id);
        Assert.Equal("fusion", result.Hits[0].ScoreType); Assert.InRange(result.Hits[0].Score, 0, .1);
        Assert.Equal("客户编码", f.Model.LastQuery);
        Assert.True(result.Trace!.Candidates.Single(c => c.Id == chunks[1].Id).ExactMatched);
        Assert.Equal("按 customer_code 查询客户", result.Trace.OriginalQuestion);
    }

    /// <summary>require 使用全部完整标识，伪造标识索引不能把不相关正文送给重排序服务。</summary>
    [Fact(Skip = "需要隔离测试库", SkipWhen = nameof(NoDatabase))]
    public async Task ExactRequirementAndSourceBodyAreChecked()
    {
        await using var f = await Ready(); var chunks = await Chunks(f);
        var settings = new KnowledgeRetrievalSettings { Mode = "hybrid", ExactMatch = "require", NeighborRadius = 0 };
        var result = await Search(f, settings, "customer_code");
        Assert.Equal(chunks[1].Id, Assert.Single(result.Hits).Chunk.Id);
        f.Storage.ExactHits = [new(chunks[0].Id, 1)];
        await Assert.ThrowsAsync<KnowledgeException>(() => Search(f, settings with { RerankModelId = Guid.NewGuid() }, "customer_code"));
        Assert.Equal(0, f.Reranking.Calls);
    }

    /// <summary>重排序采用原始问题、数据库正文和唯一候选编号，分数阈值不作用于 BM25。</summary>
    [Fact(Skip = "需要隔离测试库", SkipWhen = nameof(NoDatabase))]
    public async Task RerankProducesAuditableThresholdedResults()
    {
        await using var f = await Ready(); var chunks = await Chunks(f);
        f.Storage.DenseHits = [new(chunks[0].Id, .9), new(chunks[1].Id, .8)]; f.Storage.KeywordHits = [];
        f.Reranking.Result = new(true, [new(0, .1), new(1, .95)], null, null, 200, 27);
        var result = await Search(f, new() { Mode = "hybrid", ExactMatch = "off", RerankModelId = Guid.NewGuid(), MinRerankScore = .8, NeighborRadius = 0 }, "编码", "客户编码是什么？");
        Assert.Equal(chunks[1].Id, Assert.Single(result.Hits).Chunk.Id); Assert.Equal(.95, result.Hits[0].Score);
        Assert.Equal("客户编码是什么？", f.Reranking.Query); Assert.Contains(chunks[0].Text, f.Reranking.Documents![0]);
        Assert.Equal(1, f.Reranking.Calls); Assert.Equal("completed", result.Trace!.RerankStatus);
        Assert.Equal(27, result.Trace.RerankTokens); Assert.Contains(result.Trace.Candidates, c => c.Reason == "rerank_threshold");
    }

    /// <summary>失败默认中止；只有明确 fallback 才降级，两个分支都只发送一次请求。</summary>
    [Theory(Skip = "需要隔离测试库", SkipWhen = nameof(NoDatabase))]
    [InlineData("fail")]
    [InlineData("fallback")]
    public async Task TimeoutDoesNotRetryAndFallbackIsExplicit(string policy)
    {
        await using var f = await Ready();
        f.Reranking.Result = RerankResult.Failure("timeout_unknown", "synthetic timeout; acceptance unknown");
        var settings = new KnowledgeRetrievalSettings { Mode = "hybrid", ExactMatch = "off", RerankModelId = Guid.NewGuid(), RerankFailurePolicy = policy };
        if (policy == "fail") await Assert.ThrowsAsync<KnowledgeException>(() => Search(f, settings));
        else
        {
            var result = await Search(f, settings);
            Assert.Equal("fallback", result.Trace!.RerankStatus); Assert.Contains("timeout", result.Trace.RerankError);
            Assert.All(result.Hits, hit => Assert.Equal("fusion", hit.ScoreType));
        }
        Assert.Equal(1, f.Reranking.Calls);
    }

    /// <summary>配置或资源在调用期间改变，不能借 fallback 策略绕过复验交付。</summary>
    [Theory(Skip = "需要隔离测试库", SkipWhen = nameof(NoDatabase))]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RevocationOrConfigurationChangeBlocksDelivery(bool revokeLibrary)
    {
        await using var f = await Ready();
        f.Reranking.OnRank = async () =>
        {
            if (revokeLibrary) await f.Repository.SetEnabledAsync(f.Library.Id, false, Ct);
            else f.Reranking.Revision++;
        };
        await Assert.ThrowsAsync<KnowledgeException>(() => Search(f, new() { Mode = "hybrid", RerankModelId = Guid.NewGuid(), RerankFailurePolicy = "fallback" }));
        Assert.Equal(1, f.Reranking.Calls);
    }

    /// <summary>空标识、超限、旧索引和外来候选均不允许发送正文或重排请求。</summary>
    [Fact(Skip = "需要隔离测试库", SkipWhen = nameof(NoDatabase))]
    public async Task InvalidSettingsLegacyAndForeignCandidatesAreRejected()
    {
        await using var f = await Ready(); var calls = f.Model.Calls;
        await Assert.ThrowsAsync<KnowledgeException>(() => Search(f, new() { DenseCandidates = 0 }));
        await Assert.ThrowsAsync<KnowledgeException>(() => Search(f, new() { ExactMatch = "require" }, "客户编码"));
        Assert.Equal(calls, f.Model.Calls);
        f.Storage.KeywordHits = [new(Guid.NewGuid(), 2)];
        await Assert.ThrowsAsync<KnowledgeException>(() => Search(f, new() { Mode = "keyword", ExactMatch = "off", RerankModelId = Guid.NewGuid() }));
        Assert.Equal(calls, f.Model.Calls); Assert.Equal(0, f.Reranking.Calls);
        var version = (await f.Repository.FindAsync(f.Library.Id, Ct))!.ActiveVersion!.Value;
        await f.Database.SqlAsync($"UPDATE knowledge_version SET index_schema='dense-v1' WHERE id='{version}'");
        await Assert.ThrowsAsync<KnowledgeException>(() => Search(f, new() { Mode = "hybrid" }));
        Assert.Equal(calls, f.Model.Calls);
    }

    /// <summary>升级复用向量与稳定片段 ID，重复请求幂等，旧激活指针和发布引用不变。</summary>
    [Fact(Skip = "需要隔离测试库", SkipWhen = nameof(NoDatabase))]
    public async Task LegacyUpgradeReusesVectorsAndRequiresExplicitActivation()
    {
        await using var f = await Ready(); var chunks = await Chunks(f);
        var original = (await f.Repository.FindAsync(f.Library.Id, Ct))!.ActiveVersion!.Value;
        await f.Database.SqlAsync($"UPDATE knowledge_version SET index_schema='dense-v1' WHERE id='{original}'");
        var calls = f.Model.Calls;
        var candidate = await f.Service.RebuildHybridAsync(f.Library.Id, original, Ct);
        Assert.Equal(candidate.Id, (await f.Service.RebuildHybridAsync(f.Library.Id, original, Ct)).Id);
        Assert.Equal(chunks.Length, candidate.ReusedCount); Assert.Equal("hybrid-v1", candidate.IndexSchema);
        await f.Processor.ProcessNextAsync(Ct); Assert.Equal(calls, f.Model.Calls);
        Assert.Equal(original, (await f.Repository.FindAsync(f.Library.Id, Ct))!.ActiveVersion);
        Assert.Equal(chunks.Select(c => c.Id).Order(), (await f.Repository.ReadChunksAsync(f.Library.Id, candidate.Id, null, null, 0, 32, Ct)).Select(c => c.Id).Order());
        await f.Repository.ActivateAsync(f.Library.Id, candidate.Id, Ct);
        Assert.Equal(candidate.Id, (await Search(f, new() { Mode = "hybrid" })).VersionId);
    }

    /// <summary>同题三路仅向量化一次，人工标注参与指标，外来标注在模型调用前拒绝。</summary>
    [Fact(Skip = "需要隔离测试库", SkipWhen = nameof(NoDatabase))]
    public async Task EvaluationReusesQueryVectorsAndRejectsForeignLabels()
    {
        await using var f = await Ready(); var chunks = await Chunks(f);
        var evaluation = new KnowledgeEvaluationService(f.Repository, f.Model, f.Retrieval);
        var settings = new KnowledgeRetrievalSettings { Mode = "hybrid", RerankModelId = Guid.NewGuid(), NeighborRadius = 0, ExactMatch = "off" };
        var calls = f.Model.Calls;
        await Assert.ThrowsAsync<KnowledgeException>(() => evaluation.EvaluateAsync(f.Library.Id, new([new("query", [Guid.NewGuid()])], settings, true), Ct));
        Assert.Equal(calls, f.Model.Calls);
        var result = await evaluation.EvaluateAsync(f.Library.Id, new([new("客户", [chunks[0].Id])], settings, true), Ct);
        Assert.Equal(calls + 1, f.Model.Calls); Assert.Equal(1, f.Reranking.Calls); Assert.Equal(3, result.Metrics.Length);
        Assert.All(result.Metrics, metric => Assert.Equal(1, metric.Recall));
        Assert.Equal(1, result.Metrics[0].Mrr);
    }

    /// <summary>数据库中的三个独立段落便于控制排名与标识。</summary>
    private static async Task<KnowledgeFixture> Ready()
    {
        var f = await KnowledgeFixture.CreateAsync();
        try
        {
            var version = await f.ImportAsync("# 概览\n\n" + new string('甲', 160) + "\n\n# 字段\n\ncustomer_code 是客户编码。" + new string('乙', 150) + "\n\n# 产品\n\nSKU-42 是纺织品货号。" + new string('丙', 150));
            await f.Processor.ProcessNextAsync(Ct); await f.Repository.ActivateAsync(f.Library.Id, version.Id, Ct);
            return f;
        }
        catch { await f.DisposeAsync(); throw; }
    }
    /// <summary>同一固定库与版本的合成片段，用于索引升级和真实正文校验。</summary>
    private static async Task<KnowledgeChunk[]> Chunks(KnowledgeFixture f)
    {
        var active = (await f.Repository.FindAsync(f.Library.Id, Ct))!.ActiveVersion!.Value;
        return await f.Repository.ReadChunksAsync(f.Library.Id, active, null, null, 0, 32, Ct);
    }
    /// <summary>以固定版本和测试授权执行共享管线，不调用开发或生产服务。</summary>
    private static Task<KnowledgeSearchResponse> Search(KnowledgeFixture f, KnowledgeRetrievalSettings settings, string query = "客户", string? original = null) =>
        f.Service.SearchAsync(f.Library.Id, new(query) { Settings = settings, OriginalQuestion = original }, Ct);
}

/// <summary>无数据库的排名算法、标识归一化与边界测试。</summary>
public sealed class KnowledgeRetrievalPolicyTests
{
    /// <summary>中文引号、大小写、完整字段和带连字符编码可稳定保护。</summary>
    [Fact]
    public void IdentifiersNormalizeWithoutDestroyingNames()
    {
        Assert.Equal(new[] { "customer_code", "sku-42", "销售金额" }, KnowledgeIdentifiers.Extract("CUSTOMER_CODE 和 SKU-42，`销售金额`，普通中文"));
    }
    /// <summary>相邻支持不计算人工标注排名，指标采用主命中顺序。</summary>
    [Fact]
    public void RankingMetricsUseBinaryGroundTruth()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var c = Guid.NewGuid();
        var metric = KnowledgeEvaluationService.Metrics([c, a], [a, b], 2);
        Assert.Equal(.5, metric.Recall); Assert.Equal(.5, metric.Mrr); Assert.InRange(metric.Ndcg, .38, .39);
        Assert.Null(KnowledgeRetrievalPolicy.Validate(new()));
        Assert.NotNull(KnowledgeRetrievalPolicy.Validate(new() { MinRerankScore = .5 }));
    }
}
