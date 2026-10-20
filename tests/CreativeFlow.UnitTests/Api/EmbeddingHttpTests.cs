using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
using CreativeFlow.Domain.Identity;
using CreativeFlow.Infrastructure.Ai.Embeddings;
using CreativeFlow.UnitTests.Support;

namespace CreativeFlow.UnitTests.Api;

/// <summary>通过真实 MVC 与源生成序列化验证向量配置、显式测试和管理员边界。</summary>
public sealed class EmbeddingHttpTests
{
    /// <summary>三个新接口在普通用户或匿名身份下都不能读取仓储或调用模型。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlyAdminsCanConfigureAndTest(bool ordinaryAccount)
    {
        await using var fixture = await Fixture.CreateAsync(false, ordinaryAccount);
        using var get = await fixture.Client.GetAsync(fixture.Path + "/settings", TestContext.Current.CancellationToken);
        using var put = await fixture.Client.PutAsJsonAsync(fixture.Path + "/settings", new EmbeddingSettings(), TestContext.Current.CancellationToken);
        using var post = await fixture.Client.PostAsJsonAsync(fixture.Path + "/test", new EmbeddingTestRequest(["one", "two"]), TestContext.Current.CancellationToken);
        Assert.All(new[] { get, put, post }, response => Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode));
        Assert.Equal(0, fixture.Providers.Reads);
        Assert.Equal(0, fixture.Transport.Calls);
    }

    /// <summary>保存参数仅追加版本；测试使用已保存版本，HTTP 响应只交付摘要。</summary>
    [Fact]
    public async Task SaveAndTestKeepVersionsAndNeverExposeVectors()
    {
        await using var fixture = await Fixture.CreateAsync();
        var initial = await fixture.Client.GetFromJsonAsync<EmbeddingSettingsResponse>(fixture.Path + "/settings", TestContext.Current.CancellationToken);
        Assert.Null(initial!.Version);
        using var before = await fixture.Client.PostAsJsonAsync(fixture.Path + "/test", new EmbeddingTestRequest(["a", "b"]), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, before.StatusCode);
        var saved = await fixture.SaveAsync(new() { ExpectedDimensions = 3 });
        Assert.Equal(0, fixture.Transport.Calls);
        var second = await fixture.SaveAsync(new() { ExpectedDimensions = 3, DocumentPrefix = "doc: " });
        Assert.NotEqual(saved.Version, second.Version);
        Assert.Equal("", fixture.Settings.Versions[0].Settings.DocumentPrefix);
        using var response = await fixture.Client.PostAsJsonAsync(fixture.Path + "/test", new EmbeddingTestRequest(["one", "two"]), TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.CacheControl!.NoStore);
        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var result = JsonSerializer.Deserialize<EmbeddingTestResponse>(json, ApiJsonContext.Default.EmbeddingTestResponse)!;
        Assert.True(result.Ok);
        Assert.Equal(3, result.Dimensions);
        Assert.Equal(2, result.Count);
        Assert.Equal(second.Version, result.ConfigurationVersion);
        Assert.DoesNotContain("vectors", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("apiKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, fixture.Transport.Calls);
        Assert.Contains("doc: one", fixture.Transport.Payload!);
    }

    /// <summary>非法配置、错误用途、空输入及未选模型不能触发向量请求。</summary>
    [Fact]
    public async Task InvalidInputFailsBeforeSubmission()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var invalid = await fixture.Client.PutAsJsonAsync(fixture.Path + "/settings", new EmbeddingSettings { EndpointPath = "../admin" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Empty(fixture.Settings.Versions);
        await fixture.SaveAsync(new());
        foreach (var input in new[] { new EmbeddingTestRequest([], "document"), new EmbeddingTestRequest(["one"], "other"), new EmbeddingTestRequest([null!]) })
        {
            using var response = await fixture.Client.PostAsJsonAsync(fixture.Path + "/test", input, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        using var missing = await fixture.Client.GetAsync($"/api/v1/admin/ai/models/{Guid.NewGuid()}/embedding/settings", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(0, fixture.Transport.Calls);
    }

    /// <summary>装配独立内存 MVC，不启动 Worker 或读取用户开发配置。</summary>
    private sealed class Fixture : IAsyncDisposable
    {
        /// <summary>测试创建并负责停止的 Web 宿主。</summary>
        private WebApplication _app = null!;
        /// <summary>合成模型仓储。</summary>
        public EmbeddingFixtures.Providers Providers { get; } = new();
        /// <summary>合成不可变配置仓储。</summary>
        public EmbeddingFixtures.Settings Settings { get; } = new();
        /// <summary>合成上游传输记录器。</summary>
        public EmbeddingFixtures.Transport Transport { get; } = new();
        /// <summary>内存 HTTP 客户端。</summary>
        public HttpClient Client { get; private set; } = null!;
        /// <summary>当前合成模型的管理路由。</summary>
        public string Path => $"/api/v1/admin/ai/models/{Providers.Provider.Models[0].Id}/embedding";

        /// <summary>替换鉴权上下文，保留生产控制器、用例、分派和 JSON 元数据。</summary>
        public static async Task<Fixture> CreateAsync(bool admin = true, bool account = false)
        {
            var fixture = new Fixture();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton<IProviderRepository>(fixture.Providers);
            builder.Services.AddSingleton<IEmbeddingSettingsRepository>(fixture.Settings);
            builder.Services.AddSingleton<IApiKeyProtector>(new TextGenerationHttpFixture.TestProtector(new()));
            builder.Services.AddSingleton<IEmbeddingDispatcher>(new EmbeddingDispatcher([new OpenAiEmbeddingClient(fixture.Transport, EmbeddingFixtures.Policy())]));
            builder.Services.AddScoped<EmbeddingModelService>();
            builder.Services.AddControllers().AddApplicationPart(typeof(AdminEmbeddingController).Assembly)
                .AddJsonOptions(options => options.JsonSerializerOptions.TypeInfoResolverChain.Insert(0, ApiJsonContext.Default));
            var app = builder.Build();
            // 身份替身只改变本测试的上下文，不添加任何生产认证旁路。
            app.Use(async (context, next) =>
            {
                if (account) context.Items[CreativeFlow.Api.Hubs.DeviceContextItems.AccountId] = AccountId.New();
                if (admin) context.Items[AdminContextItems.Admin] = new AdminAccount(Guid.NewGuid(), "test", "unused", false, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
                await next(context);
            });
            app.MapControllers();
            await app.StartAsync();
            fixture._app = app;
            fixture.Client = app.GetTestClient();
            return fixture;
        }

        /// <summary>通过生产 PUT 路由保存并读取版本返回体。</summary>
        public async Task<EmbeddingSettingsResponse> SaveAsync(EmbeddingSettings settings)
        {
            using var response = await Client.PutAsJsonAsync(Path + "/settings", settings, TestContext.Current.CancellationToken);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<EmbeddingSettingsResponse>(TestContext.Current.CancellationToken))!;
        }

        /// <summary>释放测试连接、记录器和宿主。</summary>
        public async ValueTask DisposeAsync()
        {
            Client.Dispose(); Transport.Dispose(); await _app.DisposeAsync();
        }
    }
}
