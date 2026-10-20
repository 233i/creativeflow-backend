using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using CreativeFlow.Api.Controllers;
using CreativeFlow.Api.Middleware;
using CreativeFlow.Api.Serialization;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Abstractions.Security;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Application.Features.Knowledge;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;
using CreativeFlow.Infrastructure.Ai;
using CreativeFlow.Infrastructure.Ai.Knowledge;
using CreativeFlow.UnitTests.Ai;
using CreativeFlow.UnitTests.Support;

namespace CreativeFlow.UnitTests.Api;

/// <summary>重排序控制器、静态 JSON 元数据、持久版本契约和请求前收据的完整 HTTP 边界。</summary>
public sealed class RerankHttpTests
{
    /// <summary>普通账号或匿名身份不能配置和调用，甚至不能读取模型仓储。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NonAdminsCannotReadSaveOrTest(bool account)
    {
        await using var f = await Fixture.CreateAsync(false, account);
        using var get = await f.Client.GetAsync(f.Path + "/settings", TestContext.Current.CancellationToken);
        using var put = await f.Client.PutAsJsonAsync(f.Path + "/settings", new RerankSettings(), TestContext.Current.CancellationToken);
        using var post = await f.Client.PostAsJsonAsync(f.Path + "/test", new RerankTestRequest("query", ["one"]), TestContext.Current.CancellationToken);
        Assert.All(new[] { get, put, post }, r => Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode));
        Assert.Equal(0, f.Providers.Reads); Assert.Equal(0, f.Transport.Calls);
    }
    /// <summary>保存仅追加配置，测试必须使用已保存版本；响应不含密钥或上游返回正文。</summary>
    [Fact]
    public async Task ExplicitTestUsesSavedConfigurationAndDoesNotRetry()
    {
        await using var f = await Fixture.CreateAsync(); var ct = TestContext.Current.CancellationToken;
        using var before = await f.Client.PostAsJsonAsync(f.Path + "/test", new RerankTestRequest("query", ["one", "two"]), ct);
        Assert.Equal(HttpStatusCode.BadRequest, before.StatusCode); Assert.Equal(0, f.Transport.Calls);
        using var saved = await f.Client.PutAsJsonAsync(f.Path + "/settings", new RerankSettings(), ct);
        saved.EnsureSuccessStatusCode(); Assert.Equal(0, f.Transport.Calls); Assert.True(saved.Headers.CacheControl!.NoStore);
        using var tested = await f.Client.PostAsJsonAsync(f.Path + "/test", new RerankTestRequest("客户编码", ["inventory", "customer_code"]), ct);
        tested.EnsureSuccessStatusCode(); var response = await tested.Content.ReadFromJsonAsync<RerankTestResponse>(ct);
        Assert.True(response!.Ok); Assert.Equal(f.Settings.Versions[0].Version, response.ConfigurationVersion); Assert.Equal(1, f.Transport.Calls);
        Assert.DoesNotContain("apiKey", await tested.Content.ReadAsStringAsync(ct));
        using var invalid = await f.Client.PutAsJsonAsync(f.Path + "/settings", new RerankSettings { EndpointPath = "../chat/completions" }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode); Assert.Single(f.Settings.Versions);
        using var empty = await f.Client.PostAsJsonAsync(f.Path + "/test", new RerankTestRequest("query", []), ct);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode); Assert.Equal(1, f.Transport.Calls);
    }
    /// <summary>真实重排序执行器先记录请求，再完成收据；配置变更后旧快照不可重新调用。</summary>
    [Fact]
    public async Task WorkflowReceiptAndConfigurationPinSurviveFailure()
    {
        await using var f = await Fixture.CreateAsync(); var ct = TestContext.Current.CancellationToken;
        var model = f.Providers.Provider.Models[0]; await f.Settings.SaveAsync(model.Id, new(), ct);
        var trace = new WorkflowModelCallTrace(); var receipts = new List<string>();
        trace.Bind((value, _) => { receipts.Add(value.ToJsonString()); return Task.CompletedTask; });
        var reranker = new KnowledgeReranker(f.Providers, f.Settings, new TextGenerationHttpFixture.TestProtector(new()),
            new RerankClient(f.Transport, RerankProtocolTests.Network()), RerankProtocolTests.Network(), trace);
        var snapshot = await reranker.CaptureAsync(model.Id.Value, ct);
        var response = await reranker.RankAsync(snapshot, "客户编码", ["库存", "customer_code"], ct, "retrieve");
        Assert.True(response.Ok); Assert.Equal(2, receipts.Count);
        Assert.Contains("requesting", receipts[0]); Assert.Contains("completed", receipts[1]); Assert.Contains("candidateCount", receipts[1]);
        Assert.DoesNotContain("customer_code", receipts[1]); Assert.DoesNotContain("synthetic-secret", snapshot);
        await f.Settings.SaveAsync(model.Id, new() { ModelRevision = "changed" }, ct);
        await Assert.ThrowsAsync<KnowledgeException>(() => reranker.RankAsync(snapshot, "query", ["one"], ct, "retrieve"));
        Assert.Equal(1, f.Transport.Calls);
    }
    /// <summary>合成不可变参数版本；每个测试只有自己的配置，不连接开发环境。</summary>
    private sealed class Settings : IRerankSettingsRepository
    {
        /// <summary>当前测试按顺序追加的不可变参数版本。</summary>
        public List<RerankSettingsResponse> Versions { get; } = [];
        /// <summary>读取指定模型最新参数版本，不覆盖历史记录。</summary>
        public Task<RerankSettingsResponse?> FindAsync(ModelId modelId, CancellationToken ct) => Task.FromResult(Versions.LastOrDefault());
        /// <summary>追加独立配置版本并返回收据，不覆盖旧记录或调用模型。</summary>
        public Task<RerankSettingsResponse> SaveAsync(ModelId modelId, RerankSettings value, CancellationToken ct)
        {
            var response = new RerankSettingsResponse(value, Guid.NewGuid().ToString()); Versions.Add(response); return Task.FromResult(response);
        }
    }
    /// <summary>保留生产 MVC 与序列化，只有身份和网络被替换，关闭时释放所有测试资源。</summary>
    private sealed class Fixture : IAsyncDisposable
    {
        /// <summary>仅供当前用例使用的 ASP.NET 测试宿主。</summary>
        private WebApplication app = null!;
        /// <summary>具有读取计数的合成模型仓储，用于验证未授权请求不访问配置。</summary>
        public EmbeddingFixtures.Providers Providers { get; } = new() { Provider = RerankProtocolTests.Request().Provider };
        /// <summary>当前用例隔离的参数仓储，不连接项目开发数据库。</summary>
        public Settings Settings { get; } = new();
        /// <summary>返回合成排名的计数传输层，不发送真实外部请求。</summary>
        public EmbeddingFixtures.Transport Transport { get; } = new() { Body = """{"results":[{"index":1,"relevance_score":0.9},{"index":0,"relevance_score":0.1}],"usage":{"total_tokens":15}}""" };
        /// <summary>当前用例访问真实 MVC 路由的测试客户端。</summary>
        public HttpClient Client { get; private set; } = null!;
        /// <summary>当前合成模型的管理员重排序 API 前缀。</summary>
        public string Path => $"/api/v1/admin/ai/models/{Providers.Provider.Models[0].Id}/rerank";
        /// <summary>保留生产控制器和静态 JSON 边界，仅替换身份及外部网络。</summary>
        public static async Task<Fixture> CreateAsync(bool admin = true, bool account = false)
        {
            var f = new Fixture(); var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.WebHost.UseTestServer(); builder.Logging.ClearProviders();
            builder.Services.AddSingleton<IProviderRepository>(f.Providers);
            builder.Services.AddSingleton<IRerankSettingsRepository>(f.Settings);
            builder.Services.AddSingleton<IApiKeyProtector>(new TextGenerationHttpFixture.TestProtector(new()));
            builder.Services.AddSingleton<IRerankClient>(new RerankClient(f.Transport, RerankProtocolTests.Network()));
            builder.Services.AddScoped<RerankModelService>();
            builder.Services.AddControllers().AddApplicationPart(typeof(AdminRerankController).Assembly)
                .AddJsonOptions(options => options.JsonSerializerOptions.TypeInfoResolverChain.Insert(0, ApiJsonContext.Default));
            f.app = builder.Build();
            f.app.Use(async (context, next) =>
            {
                if (account) context.Items[CreativeFlow.Api.Hubs.DeviceContextItems.AccountId] = AccountId.New();
                if (admin) context.Items[AdminContextItems.Admin] = new AdminAccount(Guid.NewGuid(), "test", "synthetic", false, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
                await next(context);
            });
            f.app.MapControllers(); await f.app.StartAsync(); f.Client = f.app.GetTestClient(); return f;
        }
        /// <summary>释放当前测试客户端、网络替身与宿主，不影响项目服务。</summary>
        public async ValueTask DisposeAsync() { Client.Dispose(); Transport.Dispose(); await app.DisposeAsync(); }
    }
}
