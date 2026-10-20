using CreativeFlow.Application.Features.Workflow;
using System.Net;
using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Features.Knowledge;
using CreativeFlow.Infrastructure.Ai;
using CreativeFlow.Infrastructure.Ai.Knowledge;

namespace CreativeFlow.UnitTests.Ai;

/// <summary>保留生产 REST 适配器，通过内存传输验证资源隔离、边界和幂等协议。</summary>
public sealed class QdrantKnowledgeTests
{
    /// <summary>开发之外、任意公网地址、凭据 URL 及路径前缀均不能启用本试点客户端。</summary>
    [Theory]
    [InlineData("http://127.0.0.1:16333", "cfdev_test", false)]
    [InlineData("http://192.0.2.10:6333", "cfdev_test", true)]
    [InlineData("http://key@127.0.0.1:16333", "cfdev_test", true)]
    [InlineData("http://127.0.0.1:16333/admin", "cfdev_test", true)]
    [InlineData("http://127.0.0.1:16333", "prod", true)]
    [InlineData("http://127.0.0.1:16333", "cfprod_test", false)]
    [InlineData("http://127.0.0.1:6333", "cfdev_test", false)]
    public async Task EndpointAndNamespaceCannotEscapePilot(string endpoint, string ns, bool development)
    {
        using var transport = new Transport();
        var store = new QdrantKnowledgeStore(transport, new() { Enabled = true, Development = development, Endpoint = endpoint, Namespace = ns });
        Assert.False(store.Available);
        await Assert.ThrowsAsync<KnowledgeException>(() => store.PrepareAsync(Guid.NewGuid(), Guid.NewGuid(), 3, TestContext.Current.CancellationToken));
        Assert.Empty(transport.Requests);
    }

    /// <summary>生产只允许服务器回环的 6333 端口和 cfprod 命名空间，不会误连开发集合。</summary>
    [Fact]
    public void ProductionLoopbackConfigurationIsAvailable()
    {
        using var transport = new Transport();
        var store = new QdrantKnowledgeStore(transport, new()
        {
            Enabled = true, Development = false, Endpoint = "http://127.0.0.1:6333", Namespace = "cfprod_test"
        });
        Assert.True(store.Available);
    }

    /// <summary>创建、过滤索引、稳定 ID 写入、精确计数与检索请求都固定库和版本。</summary>
    [Fact]
    public async Task RequestsCarryVersionFilterAndNeverReturnVectors()
    {
        using var transport = new Transport(); var store = new QdrantKnowledgeStore(transport, Options());
        var ct = TestContext.Current.CancellationToken; var library = Guid.NewGuid(); var version = Guid.NewGuid(); var chunk = Guid.NewGuid();
        transport.Reply = (_, _) => (HttpStatusCode.NotFound, "{}");
        var sent = 0;
        transport.Reply = (method, path) => ++sent == 1 ? (HttpStatusCode.NotFound, "{}") : (HttpStatusCode.OK, """{"status":"ok","result":true}""");
        await store.PrepareAsync(library, version, 3, ct);
        Assert.Equal(4, transport.Requests.Count); Assert.Contains("Cosine", transport.Requests[1].Body!);
        transport.Reply = (_, _) => (HttpStatusCode.OK, """{"status":"ok","result":{"status":"completed"}}""");
        await store.WriteAsync(library, version, [new(chunk.ToString(), [1, 0, 0])], ct);
        Assert.Contains("wait=true", transport.Requests[^1].Uri); Assert.Contains(library.ToString(), transport.Requests[^1].Body!);
        Assert.DoesNotContain("text", transport.Requests[^1].Body!);
        transport.Reply = (_, _) => (HttpStatusCode.OK, """{"status":"ok","result":{"count":1}}""");
        Assert.Equal(1, await store.CountAsync(library, version, ct)); Assert.Contains("\"exact\":true", transport.Requests[^1].Body!);
        transport.Reply = (_, _) => (HttpStatusCode.OK, new JsonObject { ["status"] = "ok", ["result"] = new JsonObject { ["points"] = new JsonArray(new JsonObject
        { ["id"] = chunk.ToString(), ["score"] = 0.9, ["payload"] = new JsonObject { ["library"] = library.ToString(), ["version"] = version.ToString() } }) } }.ToJsonString());
        Assert.Equal(chunk, Assert.Single(await store.SearchAsync(library, version, [1, 0, 0], 4, ct)).Id);
        Assert.Contains("\"with_vector\":false", transport.Requests[^1].Body!);
        Assert.All(transport.Requests, request => { Assert.Contains("/collections/cfdev_test_" + version.ToString("N"), request.Uri); Assert.Equal("synthetic-key", request.Key); });
    }

    /// <summary>重定向、业务错误和缺字段均失败，不把原始服务端信息带给用户。</summary>
    [Theory]
    [InlineData(302, "secret-redirect")]
    [InlineData(200, "{\"status\":\"error\",\"message\":\"secret-error\"}")]
    [InlineData(200, "{\"status\":\"ok\",\"result\":{\"points\":[{}]}}")]
    public async Task UnsafeResponsesAreNotUsedOrRetried(int status, string body)
    {
        using var transport = new Transport { Reply = (_, _) => ((HttpStatusCode)status, body) };
        var store = new QdrantKnowledgeStore(transport, Options());
        var error = await Assert.ThrowsAsync<KnowledgeException>(() => store.SearchAsync(Guid.NewGuid(), Guid.NewGuid(), [1, 0, 0], 4, TestContext.Current.CancellationToken));
        Assert.Single(transport.Requests); Assert.DoesNotContain("secret", error.Message);
    }

    /// <summary>隧道断开时仅尝试一次查询；工作流保留适配器的安全连接提示，不泄漏原始连接信息。</summary>
    [Fact]
    public async Task DisconnectedTunnelProducesActionableWorkflowErrorWithoutRetry()
    {
        using var transport = new Transport
        {
            Reply = (_, _) => throw new HttpRequestException("connection refused; api-key=synthetic-secret"),
        };
        var store = new QdrantKnowledgeStore(transport, Options());
        var error = await Assert.ThrowsAsync<KnowledgeException>(() => store.SearchAsync(
            Guid.NewGuid(), Guid.NewGuid(), [1, 0, 0], 4, TestContext.Current.CancellationToken));

        Assert.Single(transport.Requests);
        Assert.Equal("Qdrant 连接或响应无效，请检查开发隧道和服务配置。", WorkflowExecutionErrors.UserFacingError(error));
        Assert.DoesNotContain("synthetic-secret", error.Message);
    }

    /// <summary>合成端点不会真的建立连接，密钥仅验证请求头作用域。</summary>
    private static KnowledgeStorageOptions Options() => new() { Enabled = true, Development = true, Endpoint = "http://127.0.0.1:16333", Namespace = "cfdev_test", ApiKey = "synthetic-key" };

    /// <summary>可记录所有协议请求的内存传输。</summary>
    private sealed class Transport : HttpMessageHandler, IHttpClientFactory
    {
        /// <summary>固定或按路径生成的响应。</summary>
        public Func<HttpMethod, string, (HttpStatusCode Status, string Body)> Reply { get; set; } = (_, _) => (HttpStatusCode.OK, "{}");
        /// <summary>合成请求记录，仅包含本测试生成的数据。</summary>
        public List<(string Uri, string? Body, string? Key)> Requests { get; } = [];
        /// <summary>返回共享内存处理器，禁止实际访问网络。</summary>
        public HttpClient CreateClient(string name) => new(this, false);
        /// <summary>记录请求并返回单次应答，不自动重试或跟随重定向。</summary>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!.ToString();
            Requests.Add((uri, request.Content is null ? null : await request.Content.ReadAsStringAsync(ct), request.Headers.TryGetValues("api-key", out var values) ? values.Single() : null));
            var response = Reply(request.Method, uri);
            return new HttpResponseMessage(response.Status) { Content = new StringContent(response.Body) };
        }
    }
}
