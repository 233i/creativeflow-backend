using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using CreativeFlow.Api.Controllers;
using CreativeFlow.Api.Hubs;
using CreativeFlow.Api.Middleware;
using CreativeFlow.Api.Serialization;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Contracts.Admin;
using CreativeFlow.Application.Features.Administration;
using CreativeFlow.Domain.Identity;
using CreativeFlow.UnitTests.Support;

namespace CreativeFlow.UnitTests.Api;

/// <summary>使用真实 MVC 路由验证管理员边界、JSON 契约与视频 Range 响应。</summary>
public sealed class AdminRequestsHttpTests
{
    /// <summary>普通用户身份不能访问列表、详情或媒体，且鉴权失败前不读取记录。</summary>
    [Fact]
    public async Task OrdinaryUserCannotReadAnyAdminRequestEndpoint()
    {
        await using var fixture = await Fixture.CreateAsync(false);
        var id = Guid.NewGuid();
        foreach (var path in new[] { "?source=canvas", $"/canvas/{id}", $"/workflow/{id}/assets/{Guid.NewGuid()}" })
        {
            using var response = await fixture.Client.GetAsync("/api/v1/admin/requests" + path, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        Assert.Equal(0, fixture.Repository.ReadCount);
        Assert.Equal(0, fixture.Store.Reads);
    }

    /// <summary>分页契约通过生产源生成元数据输出，完整保留精确用户筛选。</summary>
    [Fact]
    public async Task AdminCanFilterAndReadTextDetail()
    {
        await using var fixture = await Fixture.CreateAsync();
        var document = RecordingRequestHistory.CreateDocument(kind: "text", output: "{\"text\":\"实际生成正文\"}");
        fixture.Repository.Document = document;
        var query = $"/api/v1/admin/requests?source=canvas&accountId={document.Summary.AccountId}&search=test&status=failed&offset=30&limit=30";
        using var response = await fixture.Client.GetAsync(query, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var page = await response.Content.ReadFromJsonAsync<AdminRequestPage>(TestContext.Current.CancellationToken);
        Assert.Equal(document.Summary.Id, Assert.Single(page!.Items).Id);
        Assert.Equal(Guid.Parse(document.Summary.AccountId!), fixture.Repository.Query!.AccountId);
        Assert.Equal("failed", fixture.Repository.Query.Status);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var detail = await fixture.Client.GetFromJsonAsync<AdminRequestDetail>($"/api/v1/admin/requests/canvas/{document.Summary.Id}", TestContext.Current.CancellationToken);
        Assert.Equal("实际生成正文", Assert.Single(detail!.Artifacts).Text);
    }

    /// <summary>无效筛选返回错误，不能悄悄变成全部用户查询。</summary>
    [Theory]
    [InlineData("source=other")]
    [InlineData("source=canvas&accountId=invalid")]
    [InlineData("source=workflow&status=unknown")]
    [InlineData("source=canvas&limit=101")]
    [InlineData("source=canvas&offset=-1")]
    public async Task InvalidFiltersAreRejected(string query)
    {
        await using var fixture = await Fixture.CreateAsync();
        using var response = await fixture.Client.GetAsync("/api/v1/admin/requests?" + query, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, fixture.Repository.ReadCount);
    }

    /// <summary>已关联自有视频支持分段读取，其他素材不能通过替换 URL 标识读取。</summary>
    [Fact]
    public async Task AssetPreviewRequiresARecordReferenceAndSupportsRanges()
    {
        await using var fixture = await Fixture.CreateAsync();
        var assetId = Guid.NewGuid();
        var document = RecordingRequestHistory.CreateDocument(kind: "video", output: $"{{\"assetId\":\"{assetId}\"}}");
        fixture.Repository.Document = document;
        fixture.Repository.Assets.Add(new(assetId, new DeviceId(document.DeviceId), "video", "video/mp4", "private/video", 4, null, DateTimeOffset.UnixEpoch));
        var prefix = $"/api/v1/admin/requests/canvas/{document.Summary.Id}/assets/";
        using var request = new HttpRequestMessage(HttpMethod.Get, prefix + assetId);
        request.Headers.Range = new(0, 1);
        using var response = await fixture.Client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(new byte[] { 1, 2 }, await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        Assert.True(response.Headers.CacheControl!.NoStore);
        using var other = await fixture.Client.GetAsync(prefix + Guid.NewGuid(), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        Assert.Equal(1, fixture.Store.Reads);
    }

    /// <summary>仅用已验证身份标记替换认证中间件，其余 HTTP 行为沿用生产控制器。</summary>
    private sealed class Fixture : IAsyncDisposable
    {
        /// <summary>内存宿主。</summary>
        private WebApplication _app = null!;
        /// <summary>测试请求记录。</summary>
        public RecordingRequestHistory Repository { get; } = new();
        /// <summary>只读媒体替身。</summary>
        public ReadOnlyAssets Store { get; } = new();
        /// <summary>测试 HTTP 客户端。</summary>
        public HttpClient Client { get; private set; } = null!;

        /// <summary>启动仅含管理读取所需依赖的测试宿主。</summary>
        public static async Task<Fixture> CreateAsync(bool admin = true)
        {
            var fixture = new Fixture();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton<IRequestHistoryRepository>(fixture.Repository);
            builder.Services.AddSingleton<IAssetStore>(fixture.Store);
            builder.Services.AddScoped<RequestHistoryService>();
            builder.Services.AddControllers().AddApplicationPart(typeof(AdminRequestsController).Assembly)
                .AddJsonOptions(options => options.JsonSerializerOptions.TypeInfoResolverChain.Insert(0, ApiJsonContext.Default));
            var app = builder.Build();
            app.Use(async (context, next) =>
            {
                context.Items[DeviceContextItems.DeviceId] = DeviceId.New();
                context.Items[DeviceContextItems.AccountId] = AccountId.New();
                if (admin) context.Items[AdminContextItems.Admin] = new AdminAccount(Guid.NewGuid(), "admin", "unused", false, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
                await next(context);
            });
            app.MapControllers();
            await app.StartAsync();
            fixture._app = app;
            fixture.Client = app.GetTestClient();
            return fixture;
        }

        /// <summary>释放客户端和内存宿主。</summary>
        public async ValueTask DisposeAsync() { Client.Dispose(); await _app.DisposeAsync(); }
    }

    /// <summary>保存读取次数以确认鉴权和引用校验发生在存储之前。</summary>
    private sealed class ReadOnlyAssets : CreativeFlow.UnitTests.Support.TestAssetStore
    {
        /// <summary>存储读取次数。</summary>
        public int Reads { get; private set; }
        /// <summary>统计素材读取次数并返回固定字节流，供后台产物下载鉴权测试使用。</summary>
        public override Task<Stream?> OpenReadAsync(string storagePath, CancellationToken ct) { Reads++; return Task.FromResult<Stream?>(new MemoryStream([1, 2, 3, 4])); }
        /// <summary>拒绝后台请求详情测试意外写入素材字节。</summary>
        public override Task<StoredAsset> SaveAsync(byte[] bytes, string mediaType, string extensionHint, CancellationToken ct) => throw new NotSupportedException();
        /// <summary>拒绝后台请求详情测试意外删除素材字节。</summary>
        public override Task DeleteAsync(string storagePath, CancellationToken ct) => throw new NotSupportedException();
    }
}
