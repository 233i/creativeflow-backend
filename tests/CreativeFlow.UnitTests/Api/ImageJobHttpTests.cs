using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Application.Features.Generation;
using CreativeFlow.Application.Features.Identity;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Infrastructure.Ai;
using CreativeFlow.Infrastructure.Persistence;
using CreativeFlow.UnitTests.Support;

namespace CreativeFlow.UnitTests.Api;

public sealed class ImageJobHttpTests
{
    /// <summary>没有显式测试库时跳过数据库集成场景。</summary>
    public static bool NoDatabase => ImageJobDatabase.NoDatabase;
    /// <summary>当前测试的取消令牌。</summary>
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    /// <summary>本组 HTTP 契约测试的路由前缀。</summary>
    private const string Root = "/api/v1/ai/image-jobs";

    /// <summary>验证禁用配置后仍可幂等找回原任务，停用账号不会调用模型。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task DisablingConfigurationDoesNotBreakAcceptedIdempotencyAndDisabledUsersDoNotGenerate()
    {
        await using var db = await ImageJobDatabase.CreateAsync();
        await using var fixture = await ImageGenerationHttpFixture.CreateAsync(database: db);
        var input = new SubmitImageJobRequest(Guid.NewGuid(), fixture.Request());
        var first = await fixture.Client.PostAsJsonAsync(Root, input, Ct);
        var accepted = (await first.Content.ReadFromJsonAsync<ImageJobResponse>(Ct))!;
        fixture.Providers.Provider = fixture.Providers.Provider! with { Enabled = false };
        var retry = await fixture.Client.PostAsJsonAsync(Root, input, Ct);
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        Assert.Equal(accepted.JobId, (await retry.Content.ReadFromJsonAsync<ImageJobResponse>(Ct))!.JobId);
        await db.SqlAsync($"UPDATE creativeflow_account SET is_enabled = false WHERE account_id = '{db.Account}'");
        await Runner(db, fixture, new DownloadStub()).RunAsync((await db.Jobs.ClaimAsync(new(), Ct))!, Ct);
        Assert.Equal(0, fixture.Images.Calls);
        Assert.Equal("failed", (await db.Jobs.FindAsync(db.Account, Guid.Parse(accepted.JobId), Ct))!.Status);
    }

    /// <summary>验证提交立即返回、客户端断开不影响后台执行且历史只写一次。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task SubmitReturns202WithoutCallingModelAndSurvivesClientDisconnect()
    {
        await using var db = await ImageJobDatabase.CreateAsync();
        await using var fixture = await ImageGenerationHttpFixture.CreateAsync(database: db);
        var input = new SubmitImageJobRequest(Guid.NewGuid(), fixture.Request() with { CanvasId = db.Canvas.ToString(), NodeId = "node-1" });
        using var clientLifetime = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        using var response = await fixture.Client.PostAsJsonAsync(Root, input, clientLifetime.Token);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(0, fixture.Images.Calls);
        var accepted = (await response.Content.ReadFromJsonAsync<ImageJobResponse>(Ct))!;
        Assert.Equal("queued", accepted.Status);
        Assert.EndsWith(accepted.JobId, response.Headers.Location!.ToString());
        await clientLifetime.CancelAsync();
        var job = (await db.Jobs.ClaimAsync(new(), Ct))!;
        await Runner(db, fixture, new DownloadStub()).RunAsync(job, Ct);
        var complete = (await fixture.Client.GetFromJsonAsync<ImageJobResponse>($"{Root}/{accepted.JobId}", Ct))!;
        Assert.Equal("succeeded", complete.Status);
        Assert.Single(complete.Assets);
        Assert.Equal(1, fixture.Images.Calls);
        Assert.True(fixture.Images.Request!.DeferDownloads);
        var repeated = await fixture.Client.PostAsJsonAsync(Root, input, Ct);
        Assert.Equal(accepted.JobId, (await repeated.Content.ReadFromJsonAsync<ImageJobResponse>(Ct))!.JobId);
        Assert.Equal(1, await db.CountAsync("SELECT count(*) FROM canvas_generation_request"));
        var byKey = await fixture.Client.GetFromJsonAsync<ImageJobResponse>($"{Root}/by-submission/{input.Key}", Ct);
        Assert.Equal(accepted.JobId, byKey!.JobId);
    }

    /// <summary>验证查询和下载恢复不重复生成，明确重新生成才新建任务。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task QueryAndDownloadRecoveryNeverGenerateAgainButExplicitRegenerationDoes()
    {
        await using var db = await ImageJobDatabase.CreateAsync();
        await using var fixture = await ImageGenerationHttpFixture.CreateAsync(database: db);
        fixture.Images.Result = ImageGenerationResult.Success([new([], "image/png", DownloadUrl: "https://cdn.invalid/result?secret=hidden")]);
        var input = new SubmitImageJobRequest(Guid.NewGuid(), fixture.Request());
        var response = await fixture.Client.PostAsJsonAsync(Root, input, Ct);
        var accepted = (await response.Content.ReadFromJsonAsync<ImageJobResponse>(Ct))!;
        var downloader = new DownloadStub { Fail = true };
        var runner = Runner(db, fixture, downloader);
        await runner.RunAsync((await db.Jobs.ClaimAsync(new(), Ct))!, Ct);
        var failed = (await fixture.Client.GetFromJsonAsync<ImageJobResponse>($"{Root}/{accepted.JobId}", Ct))!;
        Assert.Equal("failed", failed.Status);
        Assert.Equal("saving", failed.FailureStage);
        Assert.True(failed.CanRetryDownload);
        Assert.Equal(1, fixture.Images.Calls);
        var raw = await fixture.Client.GetStringAsync($"{Root}/{accepted.JobId}", Ct);
        Assert.DoesNotContain("secret=hidden", raw);
        var retry = await fixture.Client.PostAsJsonAsync($"{Root}/{accepted.JobId}/retry-download", new { }, Ct);
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        downloader.Fail = false;
        await runner.RunAsync((await db.Jobs.ClaimAsync(new(), Ct))!, Ct);
        Assert.Equal(1, fixture.Images.Calls);
        Assert.Equal(2, downloader.Calls);
        var regenerated = await fixture.Client.PostAsJsonAsync($"{Root}/{accepted.JobId}/regenerate", new RegenerateImageJobRequest(Guid.NewGuid()), Ct);
        var next = (await regenerated.Content.ReadFromJsonAsync<ImageJobResponse>(Ct))!;
        Assert.NotEqual(accepted.JobId, next.JobId);
        Assert.Equal(input.Input.Prompt, next.Input.Prompt);
        await runner.RunAsync((await db.Jobs.ClaimAsync(new(), Ct))!, Ct);
        Assert.Equal(2, fixture.Images.Calls);
        Assert.Equal(2, await db.CountAsync("SELECT count(*) FROM canvas_asset"));
    }

    /// <summary>原生尺寸和优化模式贯穿入队、查询、执行与显式重新生成，不读取编辑后的画布。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task SeedreamOptionsPersistThroughQueueAndExplicitRegeneration()
    {
        await using var db = await ImageJobDatabase.CreateAsync();
        await using var fixture = await ImageGenerationHttpFixture.CreateAsync(database: db);
        var provider = fixture.Providers.Provider!;
        const string model = "doubao-seedream-5.0-pro";
        fixture.Providers.Provider = provider with { Models = [provider.Models[0] with { Type = ProviderType.SeedreamImages, ModelKey = model }] };
        var input = fixture.Request() with { ModelKey = model, Size = "2112x1188", PromptOptimizeMode = "fast" };
        using var response = await fixture.Client.PostAsJsonAsync(Root, new SubmitImageJobRequest(Guid.NewGuid(), input), Ct);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = (await response.Content.ReadFromJsonAsync<ImageJobResponse>(Ct))!;
        Assert.Equal("fast", accepted.Input.PromptOptimizeMode);
        var runner = Runner(db, fixture, new DownloadStub());
        await runner.RunAsync((await db.Jobs.ClaimAsync(new(), Ct))!, Ct);
        var done = (await fixture.Client.GetFromJsonAsync<ImageJobResponse>($"{Root}/{accepted.JobId}", Ct))!;
        Assert.Equal("succeeded", done.Status); Assert.Equal(input.Size, done.Input.Size);
        using var repeated = await fixture.Client.PostAsJsonAsync($"{Root}/{accepted.JobId}/regenerate", new RegenerateImageJobRequest(Guid.NewGuid()), Ct);
        var next = (await repeated.Content.ReadFromJsonAsync<ImageJobResponse>(Ct))!;
        Assert.Equal(input.Size, next.Input.Size); Assert.Equal("fast", next.Input.PromptOptimizeMode);
        await runner.RunAsync((await db.Jobs.ClaimAsync(new(), Ct))!, Ct);
        Assert.Equal(2, fixture.Images.Calls); Assert.Equal("fast", fixture.Images.Request!.PromptOptimizeMode);
    }

    /// <summary>验证未知产物不能下载恢复，其他账号及匿名请求不能访问。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task UnknownGenerationResultCannotUseDownloadRecoveryAndForeignAccountCannotQuery()
    {
        await using var db = await ImageJobDatabase.CreateAsync();
        await using var fixture = await ImageGenerationHttpFixture.CreateAsync(database: db);
        fixture.Images.Result = ImageGenerationResult.Failure(null, "upstream timeout");
        var input = new SubmitImageJobRequest(Guid.NewGuid(), fixture.Request());
        var response = await fixture.Client.PostAsJsonAsync(Root, input, Ct);
        var accepted = (await response.Content.ReadFromJsonAsync<ImageJobResponse>(Ct))!;
        await Runner(db, fixture, new DownloadStub()).RunAsync((await db.Jobs.ClaimAsync(new(), Ct))!, Ct);
        var retry = await fixture.Client.PostAsJsonAsync($"{Root}/{accepted.JobId}/retry-download", new { }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);
        Assert.Equal(1, fixture.Images.Calls);
        Assert.Null(await db.Jobs.ClaimAsync(new(), Ct));
        fixture.AccountId = new(db.OtherAccount);
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.Client.GetAsync($"{Root}/{accepted.JobId}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.Client.GetAsync($"{Root}/by-submission/{input.Key}", Ct)).StatusCode);
        await using var anonymous = await ImageGenerationHttpFixture.CreateAsync(false, db);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.Client.PostAsJsonAsync(Root, input, Ct)).StatusCode);
    }

    /// <summary>组合真实持久化执行器和可观察的模型、下载替身。</summary>
    private static ImageJobRunner Runner(ImageJobDatabase db, ImageGenerationHttpFixture fixture, DownloadStub downloader)
    {
        var context = new AccountContext();
        downloader.Store = db.Store;
        return new(fixture.Services.GetRequiredService<GenerateImagesUseCase>(), db.Jobs,
            new BackgroundAccountScope(new AccountRepository(db.Source), context), context, fixture.Providers, fixture.Protector, downloader,
            new AccountRepository(db.Source), db.Store);
    }

    private sealed class DownloadStub : IImageOutputDownloader
    {
        public IAssetStore Store { get; set; } = null!;
        /// <summary>是否模拟下载失败。</summary>
        public bool Fail { get; set; }
        /// <summary>记录下载调用次数，区分下载与再次生成。</summary>
        public int Calls { get; private set; }
        /// <summary>模拟已有产物下载，并验证外部来源不会收到密钥。</summary>
        public async Task<StoredAsset> DownloadAsync(string url, string providerBaseUrl, string apiKey, string expectedMediaType, CancellationToken ct, string? operationKey = null)
        {
            Calls++;
            Assert.Empty(apiKey);
            if (Fail) throw new InvalidOperationException("mock download failure");
            using var stream = new MemoryStream([1, 2, 3]);
            return await Store.SaveAsync(stream, "image/png", ".png", 25L * 1024 * 1024, ct, operationKey);
        }
    }

    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task InlineImagesSurviveAssetTransactionFailureWithoutAnotherModelCall()
    {
        await using var db = await ImageJobDatabase.CreateAsync();
        await using var fixture = await ImageGenerationHttpFixture.CreateAsync(database: db);
        fixture.Images.Result = ImageGenerationResult.Success([new([1, 2], "image/png"), new([3, 4], "image/png")]);
        var response = await fixture.Client.PostAsJsonAsync(Root, new SubmitImageJobRequest(Guid.NewGuid(), fixture.Request()), Ct);
        var accepted = (await response.Content.ReadFromJsonAsync<ImageJobResponse>(Ct))!;
        var job = (await db.Jobs.ClaimAsync(new(), Ct))!;
        await db.SqlAsync("""
            CREATE FUNCTION fail_image_asset() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'synthetic metadata outage'; END $$;
            CREATE TRIGGER fail_image_asset BEFORE INSERT ON canvas_asset FOR EACH ROW EXECUTE FUNCTION fail_image_asset();
            """);
        var downloader = new DownloadStub(); var runner = Runner(db, fixture, downloader);
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => runner.RunAsync(job, Ct));
        // Worker 的失败收口：不删除文件检查点，也不重发模型请求。
        await db.Jobs.FinishAsync(job, "synthetic metadata outage", Ct);
        Assert.Equal(2, await db.CountAsync("SELECT count(*) FROM storage_object WHERE state='ready'"));
        Assert.Equal(0, await db.CountAsync("SELECT count(*) FROM canvas_asset"));
        await db.SqlAsync("DROP TRIGGER fail_image_asset ON canvas_asset");
        await fixture.Client.PostAsJsonAsync($"{Root}/{accepted.JobId}/retry-download", new { }, Ct);
        await runner.RunAsync((await db.Jobs.ClaimAsync(new(), Ct))!, Ct);
        Assert.Equal(1, fixture.Images.Calls); Assert.Equal(0, downloader.Calls);
        Assert.Equal(2, await db.CountAsync("SELECT count(*) FROM canvas_asset"));
        Assert.Equal("succeeded", (await db.Jobs.FindAsync(db.Account, job.JobId, Ct))!.Status);
    }
}
