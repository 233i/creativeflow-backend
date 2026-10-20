using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Infrastructure.Ai.Knowledge;
using CreativeFlow.UnitTests.Support;

namespace CreativeFlow.UnitTests.Ai;

/// <summary>真实 Qdrant 集成验证；只在显式回环测试地址和随机独立集合运行，无收费模型。</summary>
public sealed class QdrantHybridIntegrationTests
{
    /// <summary>不支持新分词的服务器在任何集合写入和收费向量化前被明确拒绝。</summary>
    [Theory]
    [InlineData("1.18.0")]
    [InlineData("1.19.0")]
    [InlineData("unknown")]
    public async Task OlderEnginesAreRejectedBeforeIndexWrites(string version)
    {
        using var transport = new EmbeddingFixtures.Transport { Body = System.Text.Json.Nodes.JsonValue.Create(version)!.ToJsonString() };
        transport.Body = "{\"version\":" + transport.Body + "}";
        var store = new QdrantKnowledgeStore(transport, new()
        { Enabled = true, Development = true, Endpoint = "http://127.0.0.1:63549", Namespace = "cfdev_ragtest_isolated" });
        var error = await Assert.ThrowsAsync<CreativeFlow.Application.Features.Knowledge.KnowledgeException>(() =>
            store.PrepareHybridAsync(Guid.NewGuid(), Guid.NewGuid(), 3, TestContext.Current.CancellationToken));
        Assert.Contains("尚未调用向量模型", error.Message); Assert.Equal(1, transport.Calls);
    }
    /// <summary>未指定独立 Qdrant 时不借用开发或生产配置。</summary>
    public static bool NoQdrant => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CREATIVEFLOW_TEST_QDRANT"));
    /// <summary>原生多语言 BM25、命名稠密向量、精确标识及布局复验必须在实际服务通过。</summary>
    [Fact(Skip = "需要独立 Qdrant CREATIVEFLOW_TEST_QDRANT", SkipWhen = nameof(NoQdrant))]
    public async Task NativeBm25ChineseAndIdentifierFiltersWork()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new QdrantKnowledgeStore(new Clients(), new()
        {
            Enabled = true, Development = true, Endpoint = Environment.GetEnvironmentVariable("CREATIVEFLOW_TEST_QDRANT")!,
            Namespace = "cfdev_ragtest_" + Guid.NewGuid().ToString("N")[..12],
        });
        Assert.True(store.Available);
        var library = Guid.NewGuid(); var version = Guid.NewGuid();
        var chunks = new[] {
            new KnowledgeChunk(Guid.NewGuid(), "schema.md", 1, 1, "客户销售金额100万。customer_code 是客户编码，sales_amount 是销售金额。"),
            new KnowledgeChunk(Guid.NewGuid(), "schema.md", 2, 2, "库存余量与仓库补货策略。warehouse_code 是仓库编码。"),
            new KnowledgeChunk(Guid.NewGuid(), "products.md", 1, 1, "SKU-42 是纺织品货号，支持库存查询。"),
        };
        try
        {
            await store.PrepareHybridAsync(library, version, 3, ct);
            await store.WriteHybridAsync(library, version, chunks.Select((chunk, i) => new EmbeddingVector(chunk.Id.ToString(), i == 0 ? [1, 0, 0] : [0, 1, 0])).ToArray(), chunks, ct);
            Assert.Equal(3, await store.CountHybridAsync(library, version, ct));
            await store.PrepareHybridAsync(library, version, 3, ct);
            Assert.Equal(chunks[0].Id, (await store.SearchKeywordsAsync(library, version, "客户销售金额", 8, [], ct))[0].Id);
            Assert.Equal(chunks[0].Id, Assert.Single(await store.SearchExactAsync(library, version, ["customer_code"], 8, true, ct)).Id);
            Assert.Equal(chunks[2].Id, Assert.Single(await store.SearchExactAsync(library, version, ["sku-42"], 8, true, ct)).Id);
            Assert.Single(await store.SearchDenseAsync(library, version, [1, 0, 0], 8, ["customer_code"], ct));
            Assert.Empty(await store.SearchKeywordsAsync(Guid.NewGuid(), version, "销售金额", 8, [], ct));
            Assert.Empty(await store.SearchExactAsync(library, version, ["customer_code", "warehouse_code"], 8, true, ct));
            await Assert.ThrowsAsync<CreativeFlow.Application.Features.Knowledge.KnowledgeException>(() => store.PrepareHybridAsync(library, version, 4, ct));
        }
        finally { await store.DeleteAsync(library, version, CancellationToken.None); }
    }
    /// <summary>不使用应用配置或代理，不跟随重定向，只访问显式测试回环端口。</summary>
    private sealed class Clients : IHttpClientFactory
    {
        /// <summary>为隔离 Qdrant 构造短超时客户端，不读取真实服务密钥。</summary>
        public HttpClient CreateClient(string name) => new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(30) };
    }
}
