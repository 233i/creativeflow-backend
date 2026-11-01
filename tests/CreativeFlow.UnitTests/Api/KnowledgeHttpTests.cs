using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using CreativeFlow.Api.Controllers;
using CreativeFlow.Api.Middleware;
using CreativeFlow.Api.Serialization;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Application.Features.Knowledge;
using CreativeFlow.Domain.Identity;
using CreativeFlow.UnitTests.Support;

namespace CreativeFlow.UnitTests.Api;

/// <summary>真实 MVC、静态 JSON 及数据库集成；不调用开发或收费模型。</summary>
[Collection("Knowledge index database lock")]
public sealed class KnowledgeHttpTests
{
    /// <summary>禁止在未指定测试库时自动借用开发环境。</summary>
    public static bool NoDatabase => ImageJobDatabase.NoDatabase;

    /// <summary>管理员之外的主体及未启用环境都不能触发知识用例。</summary>
    [Theory(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    [InlineData(false, false, true, 401)]
    [InlineData(false, true, true, 401)]
    [InlineData(true, false, false, 404)]
    public async Task AuthenticationAndFeatureFlagAreEnforced(bool admin, bool account, bool enabled, int status)
    {
        await using var fixture = await Fixture.CreateAsync(admin, account, enabled);
        var ct = TestContext.Current.CancellationToken;
        using var list = await fixture.Client.GetAsync(Fixture.Path, ct);
        using var preview = await fixture.Client.PostAsJsonAsync(Fixture.Path + "/preview", new KnowledgeImportRequest([new("x.txt", "data")]), ct);
        using var search = await fixture.Client.PostAsJsonAsync($"{Fixture.Path}/{fixture.Data.Library.Id}/search", new KnowledgeSearchRequest("question"), ct);
        Assert.All(new[] { list, preview, search }, response => Assert.Equal(status, (int)response.StatusCode));
        Assert.Equal(0, fixture.Data.Model.Calls); Assert.Empty(fixture.Data.Storage.Points);
    }

    /// <summary>预览不调用模型，导入 202 去重、查询脱敏、激活与停用均使用生产路由。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task PreviewImportPollActivateSearchAndDisable()
    {
        await using var fixture = await Fixture.CreateAsync(); var ct = TestContext.Current.CancellationToken;
        var path = $"{Fixture.Path}/{fixture.Data.Library.Id}";
        var input = new KnowledgeImportRequest([new("guide.md", "# 合成手册\n维修期限为两年。")]);
        using var pre = await fixture.Client.PostAsJsonAsync(Fixture.Path + "/preview", input, ct);
        pre.EnsureSuccessStatusCode(); Assert.True(pre.Headers.CacheControl!.NoStore);
        var preview = (await pre.Content.ReadFromJsonAsync<KnowledgePreview>(ct))!;
        Assert.Equal(0, fixture.Data.Model.Calls);
        using var invalid = await fixture.Client.PostAsJsonAsync(path + "/versions", input, ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var submitted = await fixture.Client.PostAsJsonAsync(path + "/versions", input with { PreviewDigest = preview.Digest }, ct);
        Assert.Equal(HttpStatusCode.Accepted, submitted.StatusCode); Assert.NotNull(submitted.Headers.Location);
        var version = (await submitted.Content.ReadFromJsonAsync<KnowledgeVersionResponse>(ct))!;
        Assert.Equal("queued", version.Status); Assert.Equal(0, fixture.Data.Model.Calls);
        await fixture.Data.Processor.ProcessNextAsync(ct);
        var detail = (await fixture.Client.GetFromJsonAsync<KnowledgeDetailResponse>(path, ct))!;
        Assert.Equal(1, Assert.Single(detail.Versions).EmbeddedCount); Assert.Null(detail.Library.ActiveVersion);
        var detailJson = JsonSerializer.Serialize(detail, ApiJsonContext.Default.KnowledgeDetailResponse);
        Assert.DoesNotContain("vectors", detailJson, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("维修", detailJson);
        using var activate = await fixture.Client.PostAsync(path + $"/versions/{version.Id}/activate", null, ct); Assert.Equal(HttpStatusCode.NoContent, activate.StatusCode);
        using var search = await fixture.Client.PostAsJsonAsync(path + "/search", new KnowledgeSearchRequest("维修多久"), ct);
        search.EnsureSuccessStatusCode(); var result = (await search.Content.ReadFromJsonAsync<KnowledgeSearchResponse>(ct))!;
        Assert.Contains("两年", Assert.Single(result.Hits).Chunk.Text);
        using var other = await fixture.Client.GetAsync($"{Fixture.Path}/{Guid.NewGuid()}/versions/{version.Id}/chunks", ct); Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        using var disabled = await fixture.Client.PutAsJsonAsync(path + "/enabled", new SetKnowledgeEnabledRequest(false), ct); Assert.Equal(HttpStatusCode.NoContent, disabled.StatusCode);
        using var hidden = await fixture.Client.GetAsync(path + $"/versions/{version.Id}/chunks", ct); Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        using var refused = await fixture.Client.PostAsJsonAsync(path + "/search", new KnowledgeSearchRequest("again"), ct); Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(2, fixture.Data.Model.Calls);
    }

    /// <summary>真实文档 API 只按需交付正文，变更返回 202，清理需确认并由 Worker 完成。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task DocumentManagementAndCleanupUseGuardedProductionRoutes()
    {
        await using var f = await Fixture.CreateAsync(); var ct = TestContext.Current.CancellationToken;
        var version = await f.Data.ImportAsync(); await f.Data.Processor.ProcessNextAsync(ct);
        var path = $"{Fixture.Path}/{f.Data.Library.Id}";
        var docs = (await f.Client.GetFromJsonAsync<KnowledgeDocumentSummary[]>(path + $"/versions/{version.Id}/documents", ct))!;
        var doc = Assert.Single(docs);
        using var meta = await f.Client.GetAsync(path + $"/versions/{version.Id}/documents", ct);
        Assert.DoesNotContain("正文", await meta.Content.ReadAsStringAsync(ct)); Assert.True(meta.Headers.CacheControl!.NoStore);
        var original = await f.Client.GetFromJsonAsync<KnowledgeDocumentInput>(path + $"/versions/{version.Id}/documents/{doc.Id}", ct);
        Assert.Contains("两年", original!.Text);
        using var invalidPage = await f.Client.GetAsync(path + $"/versions/{version.Id}/chunks?limit=129", ct); Assert.Equal(HttpStatusCode.BadRequest, invalidPage.StatusCode);
        var input = new KnowledgeDocumentChangeRequest(version.Id, [new("add", "added.md", Text: "合成新增文档")], 200, 0);
        var previewResponse = await f.Client.PostAsJsonAsync(path + "/document-changes/preview", input, ct); previewResponse.EnsureSuccessStatusCode();
        var preview = (await previewResponse.Content.ReadFromJsonAsync<KnowledgePreview>(ct))!;
        using var missing = await f.Client.PostAsJsonAsync(path + "/document-changes", input, ct); Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        using var accepted = await f.Client.PostAsJsonAsync(path + "/document-changes", input with { PreviewDigest = preview.Digest }, ct);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode); var created = (await accepted.Content.ReadFromJsonAsync<KnowledgeVersionResponse>(ct))!;
        Assert.Equal(version.Id, created.SourceVersionId); Assert.Equal(1, created.ReusedCount); Assert.Equal(1, f.Data.Model.Calls);
        await f.Data.Processor.ProcessNextAsync(ct);
        var check = await f.Client.GetFromJsonAsync<KnowledgeCleanupPreview>(path + $"/versions/{version.Id}/cleanup", ct); Assert.True(check!.Allowed);
        using var badReason = await f.Client.PostAsJsonAsync(path + $"/versions/{version.Id}/cleanup", new KnowledgeCleanupRequest(""), ct);
        Assert.Equal(HttpStatusCode.BadRequest, badReason.StatusCode);
        using var clean = await f.Client.PostAsJsonAsync(path + $"/versions/{version.Id}/cleanup", new KnowledgeCleanupRequest("清理合成旧版本"), ct); Assert.Equal(HttpStatusCode.Accepted, clean.StatusCode);
        var receipt = (await clean.Content.ReadFromJsonAsync<JsonObject>(ct))!;
        Assert.Equal(version.Id.ToString(), receipt["versionId"]!.GetValue<string>());
        Assert.True(receipt["accepted"]!.GetValue<bool>());
        Assert.Equal(path, clean.Headers.Location!.ToString());
        await f.Data.Processor.ProcessNextAsync(ct);
        Assert.Equal("deleted", (await f.Data.Repository.FindVersionAsync(f.Data.Library.Id, version.Id, ct))!.Status);
        Assert.Equal(2, f.Data.Model.Calls);
        await f.Client.PutAsJsonAsync(path + "/enabled", new SetKnowledgeEnabledRequest(false), ct);
        using var hidden = await f.Client.GetAsync(path + $"/versions/{created.Id}/documents", ct); Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
    }

    /// <summary>普通账号不能使用管理员文档变更、正文和清理接口。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task DocumentEndpointsRejectNonAdminBeforeReadingOrMutating()
    {
        await using var f = await Fixture.CreateAsync(false, true); var ct = TestContext.Current.CancellationToken;
        var path = $"{Fixture.Path}/{f.Data.Library.Id}"; var version = Guid.NewGuid();
        using var docs = await f.Client.GetAsync(path + $"/versions/{version}/documents", ct);
        using var preview = await f.Client.PostAsJsonAsync(path + "/document-changes/preview", new KnowledgeDocumentChangeRequest(version, [new("add", "x.md", Text: "synthetic")]), ct);
        using var clean = await f.Client.PostAsJsonAsync(path + $"/versions/{version}/cleanup", new KnowledgeCleanupRequest("test"), ct);
        Assert.All(new[] { docs, preview, clean }, response => Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode));
        Assert.Equal(0, f.Data.Model.Calls);
    }

    /// <summary>真实序列化、ZIP 下载及删除确认共享管理员边界，更新冲突不覆盖新值。</summary>
    [Fact(Skip = "需要隔离测试库", SkipWhen = nameof(NoDatabase))]
    public async Task ManagementRoutesExportCompareAndDeleteWithAudit()
    {
        await using var f = await Fixture.CreateAsync(); var ct = TestContext.Current.CancellationToken;
        var path = $"{Fixture.Path}/{f.Data.Library.Id}";
        using var update = await f.Client.PatchAsJsonAsync(path, new UpdateKnowledgeMetadataRequest("HTTP 更新", "用途", 1), ct);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var library = (await update.Content.ReadFromJsonAsync<KnowledgeBaseResponse>(ct))!; Assert.Equal(2, library.Revision);
        using var stale = await f.Client.PatchAsJsonAsync(path, new UpdateKnowledgeMetadataRequest("旧请求", "", 1), ct);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var version = await f.Data.ImportAsync(); await f.Data.Processor.ProcessNextAsync(ct);
        using var archive = await f.Client.GetAsync(path + $"/export?versionId={version.Id}", ct);
        Assert.Equal(HttpStatusCode.OK, archive.StatusCode); Assert.Equal("application/zip", archive.Content.Headers.ContentType!.MediaType);
        Assert.NotNull(archive.Content.Headers.ContentDisposition); Assert.True(archive.Headers.CacheControl!.Private); Assert.True(archive.Headers.CacheControl.NoStore);
        using var compare = await f.Client.GetAsync(path + $"/compare?from={version.Id}&to={version.Id}", ct);
        Assert.Equal(HttpStatusCode.OK, compare.StatusCode);
        var difference = (await compare.Content.ReadFromJsonAsync<KnowledgeVersionComparison>(ct))!;
        Assert.Equal("unchanged", Assert.Single(difference.Documents).Change);
        var preview = (await f.Client.GetFromJsonAsync<KnowledgeDeletionPreview>(path + "/deletion", ct))!;
        using var deletion = new HttpRequestMessage(HttpMethod.Delete, path)
        { Content = JsonContent.Create(new DeleteKnowledgeRequest(preview.Name, "HTTP 验收", preview.Revision, preview.ImpactToken)) };
        using var accepted = await f.Client.SendAsync(deletion, ct); Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        await f.Data.Processor.ProcessNextAsync(ct);
        using var blocked = await f.Client.GetAsync(path + "/export", ct); Assert.Equal(HttpStatusCode.BadRequest, blocked.StatusCode);
        var audit = (await f.Client.GetFromJsonAsync<KnowledgeManagementAudit[]>(path + "/audit", ct))!;
        Assert.Contains(audit, item => item.Action == "metadata_updated"); Assert.Contains(audit, item => item.Action == "exported");
        Assert.Contains(audit, item => item.Action == "delete_completed"); Assert.Equal(1, f.Data.Model.Calls);
    }

    /// <summary>全部新增管理路由在访问数据库正文、变更元数据或创建归档前验证管理员身份和功能开关。</summary>
    [Theory(Skip = "需要隔离测试库", SkipWhen = nameof(NoDatabase))]
    [InlineData(false, true, 401)]
    [InlineData(true, false, 404)]
    public async Task ManagementRoutesRejectOtherSubjects(bool admin, bool enabled, int status)
    {
        await using var f = await Fixture.CreateAsync(admin, !admin, enabled); var ct = TestContext.Current.CancellationToken;
        var path = $"{Fixture.Path}/{f.Data.Library.Id}";
        foreach (var endpoint in new[] { "/deletion", "/audit", "/export", $"/compare?from={Guid.NewGuid()}&to={Guid.NewGuid()}" })
        { using var response = await f.Client.GetAsync(path + endpoint, ct); Assert.Equal(status, (int)response.StatusCode); }
        using var update = await f.Client.PatchAsJsonAsync(path, new UpdateKnowledgeMetadataRequest("禁止修改", "", 1), ct); Assert.Equal(status, (int)update.StatusCode);
        using var deletion = new HttpRequestMessage(HttpMethod.Delete, path) { Content = JsonContent.Create(new DeleteKnowledgeRequest(f.Data.Library.Name, "禁止删除", 1, "test")) };
        using var removed = await f.Client.SendAsync(deletion, ct); Assert.Equal(status, (int)removed.StatusCode);
        Assert.Equal(0, f.Data.Model.Calls); Assert.Empty(await f.Data.Repository.AuditAsync(f.Data.Library.Id, ct));
    }

    /// <summary>测试持有独立数据库和内存 HTTP 宿主。</summary>
    private sealed class Fixture : IAsyncDisposable
    {
        /// <summary>生产控制器路由基址。</summary>
        public const string Path = "/api/v1/admin/knowledge-bases";
        /// <summary>本测试创建的隔离存储与替身。</summary>
        public KnowledgeFixture Data { get; private set; } = null!;
        /// <summary>本测试的 MVC 宿主。</summary>
        private WebApplication _app = null!;
        /// <summary>只访问内存服务的 HTTP 客户端。</summary>
        public HttpClient Client { get; private set; } = null!;
        /// <summary>仅在隔离 schema 创建的合法审计主体。</summary>
        private Guid AdminId { get; } = Guid.NewGuid();

        /// <summary>仅替换身份、模型和向量网络，不启动任何真实后台 Worker。</summary>
        public static async Task<Fixture> CreateAsync(bool admin = true, bool account = false, bool enabled = true)
        {
            var fixture = new Fixture { Data = await KnowledgeFixture.CreateAsync() };
            await fixture.Data.Database.SqlAsync($"INSERT INTO creativeflow_admin(admin_id,username,password_hash,created_at,updated_at) VALUES ('{fixture.AdminId}','test-admin','synthetic-unused-password-hash',now(),now())");
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.WebHost.UseTestServer(); builder.Logging.ClearProviders();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Knowledge:Enabled"] = enabled.ToString() });
            builder.Services.AddSingleton<IKnowledgeRepository>(fixture.Data.Repository);
            builder.Services.AddSingleton<IKnowledgeManagementRepository>(fixture.Data.Repository);
            builder.Services.AddScoped<KnowledgeManagementService>();
            builder.Services.AddSingleton<IKnowledgeEmbedding>(fixture.Data.Model);
            builder.Services.AddSingleton<IKnowledgeIndexStore>(fixture.Data.Storage);
            builder.Services.AddScoped<KnowledgeLibraryService>();
            builder.Services.AddSingleton<IKnowledgeReranker>(fixture.Data.Reranking);
            builder.Services.AddScoped<KnowledgeRetrievalService>();
            builder.Services.AddScoped<KnowledgeEvaluationService>();
            builder.Services.AddControllers().AddApplicationPart(typeof(AdminKnowledgeController).Assembly)
                .AddJsonOptions(options => options.JsonSerializerOptions.TypeInfoResolverChain.Insert(0, ApiJsonContext.Default));
            var app = builder.Build();
            app.Use(async (context, next) =>
            {
                if (account) context.Items[CreativeFlow.Api.Hubs.DeviceContextItems.AccountId] = AccountId.New();
                if (admin) context.Items[AdminContextItems.Admin] = new AdminAccount(fixture.AdminId, "test", "unused", false, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
                await next(context);
            });
            app.MapControllers(); await app.StartAsync(); fixture._app = app; fixture.Client = app.GetTestClient(); return fixture;
        }

        /// <summary>关闭测试 HTTP 和随机 schema，不触碰开发资源。</summary>
        public async ValueTask DisposeAsync() { Client.Dispose(); await _app.DisposeAsync(); await Data.DisposeAsync(); }
    }
}
