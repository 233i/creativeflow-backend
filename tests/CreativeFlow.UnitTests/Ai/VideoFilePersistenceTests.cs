using Npgsql;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Domain.Identity;
using CreativeFlow.Infrastructure.Persistence;
using CreativeFlow.UnitTests.Support;

namespace CreativeFlow.UnitTests.Ai;

public sealed class VideoFilePersistenceTests
{
    public static bool NoDatabase => ImageJobDatabase.NoDatabase;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(Skip = "需要隔离测试库", SkipWhen = nameof(NoDatabase))]
    public async Task CompletionFailureRetainsFileAndRetryAtomicallyCreatesOneAsset()
    {
        await using var db = await ImageJobDatabase.CreateAsync();
        var jobs = new VideoJobRepository(new CreativeFlowConnectionFactory(db.Source, new AccountContext { AccountId = new AccountId(db.Account) }));
        var id = Guid.NewGuid(); var before = DateTimeOffset.UtcNow.AddHours(-2);
        var job = new VideoJob(id, new(db.Device), null, "synthetic-model", VideoJobStatus.Running, "synthetic-task", 1, null, null, "{}", before, before);
        await jobs.InsertAsync(job, Ct);
        using var input = new MemoryStream(new byte[] { 1, 2, 3, 4 });
        var file = await db.Store.SaveAsync(input, "video/mp4", ".mp4", 512L * 1024 * 1024, Ct, $"video-job:{id:N}");
        Assert.Equal(0, await jobs.FailStaleAsync(DateTimeOffset.UtcNow.AddMinutes(-30), Ct));
        await db.SqlAsync("""
            CREATE FUNCTION fail_video_asset() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'synthetic asset failure'; END $$;
            CREATE TRIGGER fail_video_asset BEFORE INSERT ON canvas_asset FOR EACH ROW EXECUTE FUNCTION fail_video_asset();
            """);
        await Assert.ThrowsAsync<PostgresException>(() => jobs.CompleteFileAsync(job, file, Ct));
        Assert.Equal(0, await db.CountAsync("SELECT count(*) FROM canvas_asset"));
        Assert.Equal(VideoJobStatus.Running, (await jobs.FindByIdAsync(id, Ct))!.Status);
        Assert.NotNull(await db.Store.FindSavedAsync($"video-job:{id:N}", Ct));
        await db.SqlAsync("DROP TRIGGER fail_video_asset ON canvas_asset");
        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => jobs.CompleteFileAsync(job, file, Ct)));
        Assert.Equal(1, await db.CountAsync("SELECT count(*) FROM canvas_asset"));
        var finished = (await jobs.FindByIdAsync(id, Ct))!;
        Assert.Equal(VideoJobStatus.Succeeded, finished.Status); Assert.NotNull(finished.AssetId);
        Assert.Equal(1, await db.CountAsync("SELECT count(*) FROM storage_object"));
    }

    [Fact(Skip = "需要隔离测试库", SkipWhen = nameof(NoDatabase))]
    public async Task CompletionCannotAttachAnotherTasksFileOrReviveCancelledTask()
    {
        await using var db = await ImageJobDatabase.CreateAsync();
        var jobs = new VideoJobRepository(new CreativeFlowConnectionFactory(db.Source, new AccountContext()));
        var id = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        var job = new VideoJob(id, new(db.Device), null, "synthetic", VideoJobStatus.Running, "task", 1, null, null, "{}", now, now);
        await jobs.InsertAsync(job, Ct);
        var foreign = await db.Store.SaveAsync([1], "video/mp4", ".mp4", Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => jobs.CompleteFileAsync(job, foreign, Ct));
        await jobs.UpdateAsync(job with { Status = VideoJobStatus.Cancelled }, Ct);
        using var input = new MemoryStream(new byte[] { 2 });
        var file = await db.Store.SaveAsync(input, "video/mp4", ".mp4", 1024, Ct, $"video-job:{id:N}");
        await jobs.CompleteFileAsync(job, file, Ct);
        Assert.Equal(0, await db.CountAsync("SELECT count(*) FROM canvas_asset"));
        Assert.Equal(VideoJobStatus.Cancelled, (await jobs.FindByIdAsync(id, Ct))!.Status);
    }
}
