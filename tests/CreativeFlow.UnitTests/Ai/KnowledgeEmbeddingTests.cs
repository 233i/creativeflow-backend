using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Security;
using CreativeFlow.Application.Features.Knowledge;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Infrastructure.Ai.Embeddings;
using CreativeFlow.UnitTests.Support;

namespace CreativeFlow.UnitTests.Ai;

/// <summary>知识模型绑定使用真实向量分派器和协议客户端，不以模型替身掩盖快照错误。</summary>
public sealed class KnowledgeEmbeddingTests
{
    /// <summary>jsonb 属性重排不影响绑定；配置变更即使维度相同也不能用于旧索引。</summary>
    [Fact]
    public async Task SnapshotAllowsJsonbReorderingButRejectsNewConfiguration()
    {
        var providers = new EmbeddingFixtures.Providers(); var settings = new EmbeddingFixtures.Settings();
        using var transport = new EmbeddingFixtures.Transport();
        var dispatcher = new EmbeddingDispatcher([new OpenAiEmbeddingClient(transport, EmbeddingFixtures.Policy())]);
        var service = new KnowledgeEmbedding(providers, settings, new Protector(), dispatcher);
        var id = providers.Provider.Models[0].Id; var ct = TestContext.Current.CancellationToken;
        await settings.SaveAsync(id, new() { ExpectedDimensions = 3 }, ct);
        var snapshot = JsonNode.Parse(await service.CaptureAsync(id.Value, ct))!.AsObject();
        var reordered = new JsonObject(); foreach (var pair in snapshot.Reverse()) reordered[pair.Key] = pair.Value?.DeepClone();
        var result = await service.EmbedAsync(reordered.ToJsonString(), [new("a", "first"), new("b", "second")], "document", ct);
        Assert.True(result.Ok); Assert.Equal(1, transport.Calls); Assert.Null(transport.Authorization);
        Assert.DoesNotContain("apiKey", snapshot.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        await settings.SaveAsync(id, new() { ExpectedDimensions = 3, QueryPrefix = "different" }, ct);
        await Assert.ThrowsAsync<KnowledgeException>(() => service.EmbedAsync(snapshot.ToJsonString(), [new("a", "first"), new("b", "second")], "query", ct));
        Assert.Equal(1, transport.Calls);
    }

    /// <summary>模型停用或没有经过配置的实测维度时不能建立可入库绑定。</summary>
    [Fact]
    public async Task DisabledOrUnconfiguredModelCannotBeCaptured()
    {
        var providers = new EmbeddingFixtures.Providers(); var settings = new EmbeddingFixtures.Settings();
        using var transport = new EmbeddingFixtures.Transport();
        var service = new KnowledgeEmbedding(providers, settings, new Protector(), new EmbeddingDispatcher([new OpenAiEmbeddingClient(transport, EmbeddingFixtures.Policy())]));
        var id = providers.Provider.Models[0].Id; var ct = TestContext.Current.CancellationToken;
        await Assert.ThrowsAsync<KnowledgeException>(() => service.CaptureAsync(id.Value, ct));
        await settings.SaveAsync(id, new() { ExpectedDimensions = 3 }, ct);
        providers.Provider = providers.Provider with { Enabled = false };
        await Assert.ThrowsAsync<KnowledgeException>(() => service.CaptureAsync(id.Value, ct)); Assert.Equal(0, transport.Calls);
    }

    /// <summary>合成数据中永不解密真实凭据。</summary>
    private sealed class Protector : IApiKeyProtector
    {
        /// <summary>测试禁止创建真实密钥密文。</summary>
        public ApiKeySecret Protect(string plaintext) => throw new NotSupportedException();
        /// <summary>测试渠道没有密钥，该方法若被调用即失败。</summary>
        public string Unprotect(ApiKeySecret secret) => throw new NotSupportedException();
        /// <summary>空密钥不提供提示。</summary>
        public string? Hint(ApiKeySecret? secret) => null;
    }
}
