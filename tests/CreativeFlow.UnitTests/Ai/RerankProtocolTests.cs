using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Infrastructure.Ai.Knowledge;
using CreativeFlow.Infrastructure.Options;
using CreativeFlow.UnitTests.Support;

namespace CreativeFlow.UnitTests.Ai;

/// <summary>重排序协议使用记录型 HTTP，不调用收费服务；校验输入、解析、并发与网络边界。</summary>
public sealed class RerankProtocolTests
{
    /// <summary>只发送模型、问题及候选数组，按候选编号还原，不采用上游伪造正文。</summary>
    [Fact]
    public async Task CompatiblePayloadAndIndexedResponseAreStrict()
    {
        using var transport = Transport(); var client = Client(transport); var request = Request();
        var result = await client.RankAsync(request, TestContext.Current.CancellationToken);
        Assert.True(result.Ok); Assert.Equal(1, result.Scores[0].Index); Assert.Equal(15, result.TotalTokens);
        Assert.Equal("Bearer synthetic-secret-not-live", transport.Authorization);
        var payload = JsonNode.Parse(transport.Payload!)!.AsObject();
        Assert.Equal(4, payload.Count); Assert.Equal("客户编码", payload["query"]!.GetValue<string>());
        Assert.Equal(2, payload["top_n"]!.GetValue<int>()); Assert.Equal(2, payload["documents"]!.AsArray().Count);
        Assert.DoesNotContain("forged", transport.Payload!);
    }

    /// <summary>重复编号、缺候选、错误范围或非有限分数绝不产生可信排名。</summary>
    [Theory]
    [InlineData("{\"results\":[{\"index\":0,\"relevance_score\":0.1},{\"index\":0,\"relevance_score\":0.9}]}")]
    [InlineData("{\"results\":[{\"index\":2,\"relevance_score\":0.1},{\"index\":0,\"relevance_score\":0.9}]}")]
    [InlineData("{\"results\":[{\"index\":0,\"relevance_score\":-0.1},{\"index\":1,\"relevance_score\":0.9}]}")]
    [InlineData("{\"results\":[{\"index\":0,\"relevance_score\":1.1},{\"index\":1,\"relevance_score\":0.9}]}")]
    [InlineData("{\"results\":[]}")]
    [InlineData("{\"results\":{},\"usage\":{\"total_tokens\":-2}}")]
    [InlineData("not-json")]
    public void InvalidResponseCannotBeUsed(string body)
    {
        var result = RerankClient.Parse(Encoding.UTF8.GetBytes(body), 2, 200);
        Assert.False(result.Ok); Assert.Equal("invalid_response", result.Code); Assert.Empty(result.Scores);
    }

    /// <summary>超长正文不静默截断，停用、协议错配和路径越界均在传输前拒绝。</summary>
    [Fact]
    public async Task InvalidRequestsNeverReachTransport()
    {
        using var transport = Transport(); var client = Client(transport); var request = Request();
        foreach (var invalid in new[] {
            request with { Documents = [new string('文', 4097)] },
            request with { Settings = new() { EndpointPath = "../chat/completions" } },
            request with { Model = request.Model with { Category = ModelCategory.Chat } },
            request with { Provider = request.Provider with { Enabled = false } },
            request with { Model = request.Model with { ProviderId = ProviderId.New() } },
            request with { Provider = request.Provider with { BaseUrl = "http://127.0.0.1:1235/v1" } },
        }) Assert.False((await client.RankAsync(invalid, TestContext.Current.CancellationToken)).Ok);
        Assert.Equal(0, transport.Calls);
    }

    /// <summary>渠道并发拒绝不排入不可见队列，完成后恢复额度；错误响应不重发且不暴露上游内容。</summary>
    [Fact]
    public async Task ConcurrencyIsBoundedAndNoFailureIsRetried()
    {
        using var transport = Transport(); var gate = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.OnSend = token => gate.Task.WaitAsync(token);
        var client = Client(transport); var request = Request();
        var first = client.RankAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal("busy", (await client.RankAsync(request, TestContext.Current.CancellationToken)).Code);
        Assert.Equal(1, transport.Calls);
        gate.SetResult(new(HttpStatusCode.BadRequest) { Content = new StringContent("sensitive-upstream-body") });
        var failed = await first; Assert.Equal("upstream_error", failed.Code); Assert.DoesNotContain("sensitive", failed.Error!);
        transport.OnSend = null;
        Assert.True((await client.RankAsync(request, TestContext.Current.CancellationToken)).Ok); Assert.Equal(2, transport.Calls);
    }

    /// <summary>本地连接超时标记受理未知且只调用一次，响应超过 1 MiB 不进入解析器。</summary>
    [Fact]
    public async Task TimeoutAndLargeBodyPreserveUnknownAcceptance()
    {
        using var transport = Transport(); var client = Client(transport);
        transport.OnSend = async token => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return new(HttpStatusCode.OK); };
        var result = await client.RankAsync(Request() with { Settings = new() { TimeoutSeconds = 1 } }, TestContext.Current.CancellationToken);
        Assert.Equal("timeout_unknown", result.Code); Assert.Equal(1, transport.Calls);
        transport.OnSend = null; transport.Body = new string('x', 1048577);
        result = await client.RankAsync(Request(), TestContext.Current.CancellationToken);
        Assert.Equal("response_too_large", result.Code); Assert.Equal(2, transport.Calls);
    }

    /// <summary>生产即使配置通配也不能使用回环，开发回环必须匹配精确路径。</summary>
    [Fact]
    public async Task IndependentAllowlistDoesNotAuthorizeWholeSite()
    {
        var policy = new RerankNetwork(new() { AllowedEndpoints = ["*"] }, false).Policy;
        await Assert.ThrowsAsync<InvalidOperationException>(() => policy.ResolveAsync(new("http://127.0.0.1:1234/v1/rerank"), TestContext.Current.CancellationToken));
        policy = Network().Policy;
        await Assert.ThrowsAsync<InvalidOperationException>(() => policy.ResolveAsync(new("http://127.0.0.1:1234/v1/chat/completions"), TestContext.Current.CancellationToken));
    }
    /// <summary>只在本测试构造独立模型用途，API Key 是明显的合成字符串。</summary>
    internal static RerankRequest Request()
    {
        var original = EmbeddingFixtures.Request();
        var model = original.Model with { Category = ModelCategory.Rerank, Type = ProviderType.Rerank, ModelKey = "synthetic-rerank" };
        return new(original.Provider with { Type = ProviderType.Rerank, Models = [model] }, model, "synthetic-secret-not-live", new(), "客户编码", ["库存资料", "customer_code 客户编码"]);
    }
    /// <summary>合法开发路径，其余地址不会进入替身。</summary>
    internal static RerankNetwork Network() => new(new EmbeddingOptions { AllowedEndpoints = ["http://127.0.0.1:1234/v1/rerank"] }, true);
    /// <summary>模拟乱序评分与不可信正文。</summary>
    private static EmbeddingFixtures.Transport Transport() => new() { Body = """{"results":[{"index":1,"relevance_score":0.9,"document":{"text":"forged"}},{"index":0,"relevance_score":0.2}],"usage":{"total_tokens":15}}""" };
    /// <summary>组合计数 HTTP 替身与独立许可策略，不访问收费上游。</summary>
    private static RerankClient Client(EmbeddingFixtures.Transport transport) => new(transport, Network());
}
