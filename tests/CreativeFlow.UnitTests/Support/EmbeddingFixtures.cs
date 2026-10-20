using System.Net;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Infrastructure.Ai.Embeddings;
using CreativeFlow.Infrastructure.Options;

namespace CreativeFlow.UnitTests.Support;

/// <summary>提供不访问网络或数据库的向量配置和可记录传输替身。</summary>
internal static class EmbeddingFixtures
{
    /// <summary>构造固定回环地址的合成渠道与向量模型，不包含用户密钥。</summary>
    public static EmbeddingRequest Request()
    {
        var id = ProviderId.New();
        var model = new AiModel(ModelId.New(), id, "test-embedding", "Test embedding", true,
            ProviderType.OpenAiEmbeddings, ModelCategory.Embedding, null, null, null,
            false, false, false, 0, DateTimeOffset.UnixEpoch);
        var provider = new AiProvider(id, null, "Test local", null, null, ProviderType.OpenAiEmbeddings,
            null, "http://127.0.0.1:1234/v1", true, null, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)
            { Models = [model] };
        return new(provider, model, "", new(), [new("a", "first"), new("b", "second")], "document");
    }

    /// <summary>提供开发精确路径许可，只解析数字回环地址。</summary>
    public static EmbeddingEndpointPolicy Policy(bool development = true) => new(new EmbeddingOptions
    {
        AllowedEndpoints = ["http://127.0.0.1:1234/v1/embeddings", "http://127.0.0.1:1234/v1/models"],
    }, development);

    /// <summary>记录请求次数、正文和凭据，默认返回乱序的两个三维向量。</summary>
    internal sealed class Transport : HttpMessageHandler, IHttpClientFactory
    {
        /// <summary>合成上游 JSON，可注入损坏结构。</summary>
        public string Body { get; set; } = """{"data":[{"index":1,"embedding":[0,1,0]},{"index":0,"embedding":[1,0,0]}],"usage":{"total_tokens":4}}""";
        /// <summary>合成 HTTP 状态。</summary>
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        /// <summary>传输实际收到的请求次数。</summary>
        public int Calls { get; private set; }
        /// <summary>最近发送的请求正文。</summary>
        public string? Payload { get; private set; }
        /// <summary>最近请求携带的鉴权值，用于验证可选密钥。</summary>
        public string? Authorization { get; private set; }
        /// <summary>可控异步应答，用于超时及并发测试。</summary>
        public Func<CancellationToken, Task<HttpResponseMessage>>? OnSend { get; set; }

        /// <summary>返回共享记录器的客户端，客户端释放不销毁该记录器。</summary>
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };

        /// <summary>只记录并返回合成响应；此替身不进行 DNS 或真实 HTTP 连接。</summary>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Payload = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Authorization = request.Headers.Authorization?.ToString();
            if (OnSend is not null) return await OnSend(ct);
            return new(Status) { Content = new StringContent(Body) };
        }
    }

    /// <summary>只返回合成全局模型，其他仓储写入沿用基类的拒绝行为。</summary>
    internal sealed class Providers : StubProviderRepository
    {
        /// <summary>当前可读合成渠道，可修改禁用状态用于测试。</summary>
        public AiProvider Provider { get; set; } = Request().Provider;
        /// <summary>累计资源读取次数，用于确认鉴权先于仓储。</summary>
        public int Reads { get; private set; }
        /// <summary>按指定全局模型 ID 返回合成模型。</summary>
        public override Task<AiModel?> FindModelAsync(ModelId modelId, CancellationToken ct)
        {
            Reads++;
            return Task.FromResult(Provider.Models.FirstOrDefault(model => model.Id == modelId));
        }
        /// <summary>仅返回 ID 匹配的全局渠道。</summary>
        public override Task<AiProvider?> FindAsync(ProviderId providerId, CancellationToken ct)
        {
            Reads++;
            return Task.FromResult(providerId == Provider.Id ? Provider : null);
        }
    }

    /// <summary>在内存中追加向量参数版本，不连接 PostgreSQL。</summary>
    internal sealed class Settings : IEmbeddingSettingsRepository
    {
        /// <summary>已保存的不可变参数快照，供版本递增断言使用。</summary>
        public List<EmbeddingSettingsResponse> Versions { get; } = [];
        /// <summary>读取最后追加的合成版本。</summary>
        public Task<EmbeddingSettingsResponse?> FindAsync(ModelId modelId, CancellationToken ct) =>
            Task.FromResult(Versions.LastOrDefault());
        /// <summary>追加新的参数与版本标识，不修改之前条目。</summary>
        public Task<EmbeddingSettingsResponse> SaveAsync(ModelId modelId, EmbeddingSettings settings, CancellationToken ct)
        {
            var result = new EmbeddingSettingsResponse(settings, Guid.NewGuid().ToString());
            Versions.Add(result);
            return Task.FromResult(result);
        }
    }
}
