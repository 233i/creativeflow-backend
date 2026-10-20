using System.Net;
using System.Net.Http.Json;
using System.IO.Compression;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using CreativeFlow.Api.Http;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Infrastructure.Ai;
using CreativeFlow.Infrastructure.Options;
using CreativeFlow.Infrastructure.Persistence;
using CreativeFlow.UnitTests.Support;

namespace CreativeFlow.UnitTests.Api;

/// <summary>用隔离真实数据库验证资产 HTTP、引用事务、共享字节和恢复路径，不调用模型。</summary>
public sealed class AssetCenterHttpTests
{
    public static bool NoDatabase => ImageJobDatabase.NoDatabase;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Admin = "/api/v1/admin/assets";
    private const string Library = "/api/v1/canvas/asset-library";

    private static async Task<AdminWorkflowApisHttpTests.Fixture> Fixture(IAssetStore? store = null)
    {
        var fixture = await AdminWorkflowApisHttpTests.Fixture.CreateAsync(v2: true, configure: (f, services) =>
        {
            services.AddScoped<IAssetCenterRepository, AssetCenterRepository>();
            services.AddSingleton<IAssetPreviewService, EmptyPreview>();
            services.AddSingleton(ExternalWorkflowHandlersTests.RepositoryProxy.Create<IVideoFrameService>((_, _) => throw new InvalidOperationException("Not a frame-generation test.")));
            if (store is FailingStore failing) failing.Inner = f.Db.Store;
            if (store is not null) services.AddSingleton(store);
            services.Configure<Microsoft.AspNetCore.Mvc.MvcOptions>(options => options.Filters.Add<AssetAvailabilityExceptionFilter>());
        });
        await fixture.Db.SqlAsync($"INSERT INTO creativeflow_admin(admin_id,username,password_hash,must_change_password,created_at,updated_at) VALUES ('{fixture.AdminId}','asset-test','synthetic-hash-not-usable',false,now(),now()); UPDATE asset_storage_policy SET retention_days=0");
        return fixture;
    }

    private static async Task<Guid> Seed(AdminWorkflowApisHttpTests.Fixture fixture, Guid? device = null, string name = "合成图片", string? path = null)
    {
        var id = Guid.NewGuid();
        var store = fixture.App.Services.GetRequiredService<IAssetStore>();
        path ??= (await store.SaveAsync([1, 2, 3], "image/png", ".png", Ct)).StoragePath;
        await fixture.Db.SqlAsync($"INSERT INTO canvas_asset(asset_id,device_id,kind,media_type,storage_path,byte_size,prompt,created_at,name,origin) VALUES ('{id}','{device ?? fixture.Db.Device}','image','image/png','{path}',3,'private prompt',now(),'{name}','upload')");
        return id;
    }

    private static async Task<AssetDetail> Detail(AdminWorkflowApisHttpTests.Fixture f, Guid id) =>
        (await f.Client.GetFromJsonAsync<AssetDetail>($"{Admin}/{id}", Ct))!;

    private static async Task<HttpResponseMessage> Act(AdminWorkflowApisHttpTests.Fixture f, Guid id, string action, bool history = false)
    {
        var detail = await Detail(f, id);
        return await f.Client.PostAsJsonAsync($"{Admin}/{id}/{action}", new AssetActionRequest(detail.Asset.Revision, "合成测试确认", detail.Asset.Name, history), Ct);
    }

    [Fact(Skip = "需要隔离测试库", SkipWhen = nameof(NoDatabase))]
    public async Task ListsAndFiltersServerSideWithoutLeakingOtherOwnersOrPrivatePrompts()
    {
        await using var f = await Fixture();
        var first = await Seed(f, name: "100% synthetic"); var second = await Seed(f, f.Db.OtherDevice);
        var page = await f.Client.GetFromJsonAsync<AssetPage>($"{Admin}?limit=1", Ct);
        Assert.Equal(2, page!.Total); Assert.Single(page.Items);
        var exact = await f.Client.GetFromJsonAsync<AssetPage>($"{Admin}?search=100%25", Ct);
        Assert.Equal(first.ToString(), Assert.Single(exact!.Items).Id);
        var start = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-1).ToOffset(TimeSpan.FromHours(8)).ToString("O"));
        var end = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(1).ToOffset(TimeSpan.FromHours(8)).ToString("O"));
        Assert.Equal(2, (await f.Client.GetFromJsonAsync<AssetPage>($"{Admin}?from={start}&to={end}", Ct))!.Total);
        f.Identity("user");
        var own = await f.Client.GetFromJsonAsync<AssetPage>(Library, Ct);
        Assert.Equal(first.ToString(), Assert.Single(own!.Items).Id);
        Assert.Equal(HttpStatusCode.NotFound, (await f.Client.GetAsync($"{Library}/{second}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.Client.GetAsync(Admin, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await f.Client.GetAsync($"{Library}?accountId={f.Db.OtherAccount}", Ct)).StatusCode);
        f.Identity("anonymous");
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.Client.GetAsync(Library, Ct)).StatusCode);
    }

    [Fact(Skip = "需要隔离测试库", SkipWhen = nameof(NoDatabase))]
    public async Task PublicReuseCreatesOwnedRecordWithoutDuplicateBytesAndUnpublishStopsNewAccess()
    {
        await using var f = await Fixture(); var id = await Seed(f);
        (await Act(f, id, "publish")).EnsureSuccessStatusCode();
        f.Identity("other");
        var page = (await f.Client.GetFromJsonAsync<AssetPage>(Library + "?public=true", Ct))!;
        var shared = Assert.Single(page.Items);
        Assert.Null(shared.AccountId); Assert.Null(shared.GroupId); Assert.False(shared.CanManage); Assert.Equal("公共素材", shared.Owner);
        var copy = await f.PostAsync<AssetItem>($"{Library}/public/{id}/reuse", new ReusePublicAssetRequest(shared.Revision));
        Assert.NotEqual(id.ToString(), copy.Id); Assert.Equal("reused", copy.Source);
        Assert.Equal(1, await f.Db.CountAsync("SELECT count(DISTINCT storage_path) FROM canvas_asset"));
        var privateCopy = (await f.Client.GetFromJsonAsync<AssetDetail>($"{Library}/{copy.Id}", Ct))!;
        Assert.Null(privateCopy.Prompt);
        Assert.Equal(HttpStatusCode.NotFound, (await f.Client.GetAsync($"{Library}/{id}", Ct)).StatusCode);
        f.Identity("admin"); (await Act(f, id, "unpublish")).EnsureSuccessStatusCode();
        f.Identity("other");
        Assert.Equal(HttpStatusCode.NotFound, (await f.Client.GetAsync($"{Library}/public/{id}/content", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await f.Client.PostAsJsonAsync($"{Library}/public/{id}/reuse", new ReusePublicAssetRequest(shared.Revision), Ct)).StatusCode);
        (await f.Client.GetAsync($"/api/v1/canvas/assets/{copy.Id}", Ct)).EnsureSuccessStatusCode();
    }

    [Fact(Skip = "需要隔离测试库", SkipWhen = nameof(NoDatabase))]
    public async Task RecycleRestoreAndOptimisticEditsPreserveOriginalWhileExcludingNewSelections()
    {
        await using var f = await Fixture(); var id = await Seed(f); var original = await Detail(f, id);
        var edit = new UpdateAssetRequest("新名称", ["tag"], original.Asset.Revision, "测试整理");
        (await f.Client.PatchAsJsonAsync($"{Admin}/{id}", edit, Ct)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await f.Client.PatchAsJsonAsync($"{Admin}/{id}", edit, Ct)).StatusCode);
        (await Act(f, id, "recycle")).EnsureSuccessStatusCode();
        f.Identity("user");
        Assert.Empty((await f.Client.GetFromJsonAsync<AssetPage>(Library, Ct))!.Items);
        (await f.Client.GetAsync($"/api/v1/canvas/assets/{id}", Ct)).EnsureSuccessStatusCode();
        var recycled = Assert.Single((await f.Client.GetFromJsonAsync<AssetPage>(Library + "?state=recycled", Ct))!.Items);
        (await f.Client.PostAsJsonAsync($"{Library}/{id}/restore", new AssetActionRequest(recycled.Revision, "恢复合成素材"), Ct)).EnsureSuccessStatusCode();
        Assert.Single((await f.Client.GetFromJsonAsync<AssetPage>(Library, Ct))!.Items);
        f.Identity("admin"); Assert.Equal(3, (await Detail(f, id)).Audit.Length);
    }

    [Fact(Skip = "需要隔离测试库", SkipWhen = nameof(NoDatabase))]
    public async Task ActiveRunsBlockRecycleAndHistoricalOriginalRequiresExplicitSeparateConfirmation()
    {
        await using var f = await Fixture(); var id = await Seed(f); var workflow = Guid.NewGuid(); var run = Guid.NewGuid();
        await f.Db.SqlAsync($$"""
            INSERT INTO workflow(workflow_id,device_id,name,created_at,updated_at) VALUES ('{{workflow}}','{{f.Db.Device}}','合成流程',now(),now());
            INSERT INTO workflow_run(run_id,workflow_id,device_id,graph_snapshot,status,created_at,updated_at)
            VALUES ('{{run}}','{{workflow}}','{{f.Db.Device}}','{"assetId":"{{id}}"}','queued',now(),now());
            """);
        Assert.Equal(HttpStatusCode.Conflict, (await Act(f, id, "recycle")).StatusCode);
        await f.Db.SqlAsync($"UPDATE workflow_run SET status='succeeded' WHERE run_id='{run}'");
        (await Act(f, id, "recycle")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await Act(f, id, "purge")).StatusCode);
        (await Act(f, id, "purge", true)).EnsureSuccessStatusCode();
        Assert.Equal("purged", (await Detail(f, id)).Asset.State);
        Assert.Equal(1, await f.Db.CountAsync($"SELECT count(*) FROM workflow_run WHERE run_id='{run}'"));
        Assert.Equal(HttpStatusCode.NotFound, (await f.Client.GetAsync($"{Admin}/{id}/content", Ct)).StatusCode);
        var exception = await Assert.ThrowsAsync<PostgresException>(() => f.Db.SqlAsync($$"""UPDATE workflow SET graph='{"assetId":"{{id}}"}' WHERE workflow_id='{{workflow}}'"""));
        Assert.Equal("asset_not_available", exception.ConstraintName);
    }

    [Fact(Skip = "需要隔离测试库", SkipWhen = nameof(NoDatabase))]
    public async Task DraftReferencesAndRetentionCannotBeOverriddenByHistoryFlag()
    {
        await using var f = await Fixture(); var id = await Seed(f);
        await f.Db.SqlAsync($$$"""UPDATE canvas SET graph='{"nodes":[{"data":{"assetUrl":"/api/v1/canvas/assets/{{{id}}}"}}]}' WHERE canvas_id='{{{f.Db.Canvas}}}'""");
        (await Act(f, id, "recycle")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await Act(f, id, "purge", true)).StatusCode);
        await f.Db.SqlAsync($"UPDATE canvas SET graph='{{}}' WHERE canvas_id='{f.Db.Canvas}'; UPDATE asset_storage_policy SET retention_days=7");
        Assert.Equal(HttpStatusCode.Conflict, (await Act(f, id, "purge", true)).StatusCode);
        await f.Db.SqlAsync("UPDATE asset_storage_policy SET retention_days=0");
        (await Act(f, id, "purge")).EnsureSuccessStatusCode();
        Assert.Equal(1, await f.Db.CountAsync("SELECT count(*) FROM asset_management_audit WHERE action='purged'"));
        (await Act(f, id, "purge")).EnsureSuccessStatusCode();
        Assert.Equal(1, await f.Db.CountAsync("SELECT count(*) FROM asset_management_audit WHERE action='purged'"));
    }

    [Fact(Skip = "需要隔离测试库", SkipWhen = nameof(NoDatabase))]
    public async Task FailedStorageDeletionStaysDurableAndRetryDoesNotRegenerate()
    {
        var store = new FailingStore(); await using var f = await Fixture(store); var id = await Seed(f);
        (await Act(f, id, "recycle")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Act(f, id, "purge")).StatusCode);
        Assert.Equal("cleaning", (await Detail(f, id)).Asset.State);
        Assert.Equal(1, await f.Db.CountAsync("SELECT count(*) FROM asset_storage_cleanup"));
        store.Fail = false; (await Act(f, id, "purge")).EnsureSuccessStatusCode();
        Assert.Equal("purged", (await Detail(f, id)).Asset.State); Assert.Equal(1, store.Saves);
    }

    [Fact(Skip = "需要隔离测试库", SkipWhen = nameof(NoDatabase))]
    public async Task BatchReturnsPerItemResultsAndDownloadsAllOrNothing()
    {
        await using var f = await Fixture(); var a = await Seed(f); var b = await Seed(f);
        (await Act(f, b, "publish")).EnsureSuccessStatusCode(); var da = await Detail(f, a); var db = await Detail(f, b);
        var batch = await f.PostAsync<AssetActionResult[]>(Admin + "/batch", new AssetBatchRequest("recycle", [a.ToString(), b.ToString()], "合成批量", Revisions: new() { [a.ToString()] = da.Asset.Revision, [b.ToString()] = db.Asset.Revision }));
        Assert.Equal("recycled", batch[0].Status); Assert.Equal("blocked", batch[1].Status);
        using var response = await f.Client.PostAsJsonAsync(Admin + "/download", new DownloadAssetsRequest([a.ToString(), b.ToString()]), Ct);
        response.EnsureSuccessStatusCode();
        using var zip = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync(Ct)));
        Assert.Equal(2, zip.Entries.Count);
        Assert.Equal(HttpStatusCode.NotFound, (await f.Client.PostAsJsonAsync(Admin + "/download", new DownloadAssetsRequest([a.ToString(), Guid.NewGuid().ToString()]), Ct)).StatusCode);
        (await f.Client.GetAsync(Admin + "/facets", Ct)).EnsureSuccessStatusCode();
        var storage = (await f.Client.GetFromJsonAsync<AssetStorageReport>(Admin + "/storage", Ct))!;
        Assert.Equal(3, storage.ActiveBytes); Assert.Equal(3, storage.RecycleBytes);
    }

    [Fact(Skip = "需要隔离测试库", SkipWhen = nameof(NoDatabase))]
    public async Task AdminUploadAndThumbnailStayAuthenticatedAndDoNotRequireUserLogin()
    {
        await using var f = await Fixture();
        var upload = await f.UploadAsync<GeneratedAsset>(Admin + "/upload");
        var detail = await Detail(f, Guid.Parse(upload.AssetId));
        Assert.Equal("input.png", detail.Asset.Name); Assert.Equal("upload", detail.Asset.Source);
        f.Identity("other"); Assert.Equal(HttpStatusCode.NotFound, (await f.Client.GetAsync($"/api/v1/canvas/assets/{upload.AssetId}/thumbnail", Ct)).StatusCode);
        f.Identity("anonymous"); Assert.Equal(HttpStatusCode.Unauthorized, (await f.Client.GetAsync($"{Admin}/{upload.AssetId}/thumbnail", Ct)).StatusCode);
    }

    [Fact(Skip = "需要隔离测试库", SkipWhen = nameof(NoDatabase))]
    public async Task ContentAddressedStorePreservesAliasedBytesUntilLastApprovedRemoval()
    {
        await using var db = await ImageJobDatabase.CreateAsync();
        var directory = Path.Combine(Path.GetTempPath(), "asset-store-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = new AiOptions { AssetRoot = directory };
            var store = new FileSystemAssetStore(db.Source, new AssetFileSystem(options), options, NullLogger<FileSystemAssetStore>.Instance);
            var first = await store.SaveAsync([1, 2, 3], "image/png", ".png", Ct);
            var second = await store.SaveAsync([1, 2, 3], "image/png", ".png", Ct); Assert.Equal(first.StoragePath, second.StoragePath);
            var a = Guid.NewGuid(); var b = Guid.NewGuid();
            await db.SqlAsync($"INSERT INTO canvas_asset(asset_id,device_id,kind,media_type,storage_path,byte_size,created_at) SELECT x,'{db.Device}','image','image/png','{first.StoragePath}',3,now() FROM unnest(ARRAY['{a}'::uuid,'{b}'::uuid]) x; UPDATE storage_object SET retain_until='-infinity'");
            await db.SqlAsync($"UPDATE canvas_asset SET deleted_at=now(),purged_at=now() WHERE asset_id='{a}'");
            await store.DeleteAsync(first.StoragePath, Ct); await using (var stream = await store.OpenReadAsync(first.StoragePath, Ct)) Assert.NotNull(stream);
            await db.SqlAsync($"UPDATE canvas_asset SET deleted_at=now(),purged_at=now() WHERE asset_id='{b}'");
            await store.DeleteAsync(first.StoragePath, Ct); Assert.Null(await store.OpenReadAsync(first.StoragePath, Ct));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed class EmptyPreview : IAssetPreviewService
    {
        public Task<Stream?> OpenThumbnailAsync(AssetContent asset, CancellationToken ct) => Task.FromResult<Stream?>(null);
    }

    [Fact(Skip = "需要隔离测试库", SkipWhen = nameof(NoDatabase))]
    public async Task FileBackedContentSupportsRangeWithoutBypassingOwnership()
    {
        await using var f = await Fixture(); var id = await Seed(f);
        await f.Db.SqlAsync($"UPDATE canvas_asset SET kind='video',media_type='video/mp4' WHERE asset_id='{id}'");
        f.Identity("user");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/canvas/assets/{id}");
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(1, 2);
        using var response = await f.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);
        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("bytes 1-2/3", response.Content.Headers.ContentRange!.ToString());
        Assert.Equal(2, response.Content.Headers.ContentLength);
        Assert.Equal(new byte[] { 2, 3 }, await response.Content.ReadAsByteArrayAsync(Ct));
        using var invalid = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/canvas/assets/{id}");
        invalid.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(10, 20);
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, (await f.Client.SendAsync(invalid, Ct)).StatusCode);
        f.Identity("other");
        using var foreign = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/canvas/assets/{id}");
        foreign.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(1, 2);
        Assert.Equal(HttpStatusCode.NotFound, (await f.Client.SendAsync(foreign, Ct)).StatusCode);
    }

    [Fact(Skip = "需要隔离测试库", SkipWhen = nameof(NoDatabase))]
    public async Task ReferenceAdmissionSerializesWithRecycleAndRejectsLateRuns()
    {
        await using var f = await Fixture(); var id = await Seed(f); var workflow = Guid.NewGuid(); var run = Guid.NewGuid();
        await f.Db.SqlAsync($"INSERT INTO workflow(workflow_id,device_id,name,created_at,updated_at) VALUES ('{workflow}','{f.Db.Device}','并发合成流程',now(),now())");
        var detail = await Detail(f, id);
        await using var connection = await f.Db.Source.OpenConnectionAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using var writer = connection.CreateCommand(); writer.Transaction = transaction;
        writer.CommandText = $$"""INSERT INTO workflow_run(run_id,workflow_id,device_id,graph_snapshot,status,created_at,updated_at) VALUES ('{{run}}','{{workflow}}','{{f.Db.Device}}','{"assetId":"{{id}}"}','queued',now(),now())""";
        await writer.ExecuteNonQueryAsync(Ct);
        var recycle = f.Client.PostAsJsonAsync($"{Admin}/{id}/recycle", new AssetActionRequest(detail.Asset.Revision, "并发验收"), Ct);
        await Task.Delay(120, Ct); Assert.False(recycle.IsCompleted);
        await transaction.CommitAsync(Ct);
        Assert.Equal(HttpStatusCode.Conflict, (await recycle).StatusCode);
        await f.Db.SqlAsync($"UPDATE workflow_run SET status='cancelled' WHERE run_id='{run}'");
        (await Act(f, id, "recycle")).EnsureSuccessStatusCode();
        var exception = await Assert.ThrowsAsync<PostgresException>(() => f.Db.SqlAsync($"UPDATE workflow_run SET status='queued' WHERE run_id='{run}'"));
        Assert.Equal("asset_not_available", exception.ConstraintName);
    }

    [Fact(Skip = "需要隔离测试库和本机 FFmpeg", SkipWhen = nameof(NoDatabase))]
    public async Task ThumbnailCachesBoundedPreviewWithoutChangingAssetManagementRevision()
    {
        await using var f = await Fixture();
        var upload = await f.UploadAsync<GeneratedAsset>(Admin + "/upload"); var id = Guid.Parse(upload.AssetId);
        await using var scope = f.App.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAssetCenterRepository>();
        var content = await repository.GetContentAsync(id, null, false, Ct);
        var preview = new AssetPreviewService(f.Db.Source, f.App.Services.GetRequiredService<IAssetStore>(), NullLogger<AssetPreviewService>.Instance);
        var revision = (await Detail(f, id)).Asset.Revision;
        await using (var stream = await preview.OpenThumbnailAsync(content!, Ct)) Assert.NotNull(stream);
        await using (var stream = await preview.OpenThumbnailAsync(content!, Ct)) Assert.NotNull(stream);
        Assert.Equal(1, await f.Db.CountAsync("SELECT count(*) FROM asset_preview"));
        await using (var connection = await f.Db.Source.OpenConnectionAsync(Ct))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT storage_path FROM asset_preview";
            var path = (string)(await command.ExecuteScalarAsync(Ct))!;
            // 模拟外部丢盘；正常删除接口应拒绝删除仍被预览引用的内容。
            File.Delete(Path.Combine(f.Db.FileRoot, path));
        }
        await using (var regenerated = await preview.OpenThumbnailAsync(content!, Ct)) Assert.NotNull(regenerated);
        Assert.Equal(1, await f.Db.CountAsync("SELECT count(*) FROM asset_preview"));
        var detail = await Detail(f, id); Assert.Equal(revision, detail.Asset.Revision); Assert.Equal(1, detail.Asset.Width); Assert.Equal(1, detail.Asset.Height); Assert.NotNull(detail.Sha256);
    }

    private sealed class FailingStore : TestAssetStore
    {
        public bool Fail { get; set; } = true;
        public int Saves { get; private set; }
        public IAssetStore Inner { get; set; } = null!;
        public override Task<StoredAsset> SaveAsync(byte[] bytes, string mediaType, string extensionHint, CancellationToken ct) { Saves++; return Inner.SaveAsync(bytes, mediaType, extensionHint, ct); }
        public override Task<Stream?> OpenReadAsync(string storagePath, CancellationToken ct) => Inner.OpenReadAsync(storagePath, ct);
        public override Task DeleteAsync(string storagePath, CancellationToken ct) => Fail ? Task.FromException(new IOException("synthetic failure")) : Inner.DeleteAsync(storagePath, ct);
    }
}
