using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.UnitTests.Support;

namespace CreativeFlow.UnitTests.Ai;

public sealed class ImageJobDatabaseTests
{
    /// <summary>未显式提供隔离测试库时跳过数据库集成场景。</summary>
    public static bool NoDatabase => ImageJobDatabase.NoDatabase;
    /// <summary>当前测试的取消令牌。</summary>
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    /// <summary>跨任务共享的默认配额。</summary>
    private static readonly ImageQueueLimits Limits = new();

    /// <summary>验证并发同键、异键合并及完成后的幂等重放。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task ConcurrentSubmissionsAndDifferentKeysDeduplicateAtomically()
    {
        await using var db = await ImageJobDatabase.CreateAsync();
        var input = db.Input();
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => db.Jobs.SubmitAsync(input, Limits, Ct)));
        var id = results[0].Job!.JobId;
        Assert.All(results, result => Assert.Equal(id, result.Job!.JobId));
        var alias = input with { Key = Guid.NewGuid() };
        Assert.Equal(id, (await db.Jobs.SubmitAsync(alias, Limits, Ct)).Job!.JobId);
        Assert.Equal(id, (await db.Jobs.FindSubmissionAsync(db.Account, alias.Key, Ct))!.JobId);
        Assert.True((await db.Jobs.SubmitAsync(input with { RequestHash = "different" }, Limits, Ct)).Conflict);
        Assert.Equal(1, await db.CountAsync("SELECT count(*) FROM image_job"));
        Assert.Equal(1, await db.CountAsync("SELECT count(*) FROM canvas_generation_request"));
        var job = (await db.Jobs.ClaimAsync(Limits, Ct))!;
        await db.Jobs.FinishAsync(job, "test failure", Ct);
        // 完成后的网络重试也必须回到原任务，不能创建新付费调用。
        Assert.Equal(id, (await db.Jobs.SubmitAsync(alias, Limits, Ct)).Job!.JobId);
        Assert.NotEqual(id, (await db.Jobs.SubmitAsync(input with { Key = Guid.NewGuid(), ForceNew = true }, Limits, Ct)).Job!.JobId);
    }

    /// <summary>验证排队上限及跨账号访问隔离。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task QueueLimitsAndOwnershipCannotBeBypassed()
    {
        await using var db = await ImageJobDatabase.CreateAsync();
        var limits = Limits with { MaxPendingPerUser = 1, MaxPendingGlobal = 2 };
        var input = db.Input();
        var id = (await db.Jobs.SubmitAsync(input, limits, Ct)).Job!.JobId;
        Assert.True((await db.Jobs.SubmitAsync(db.Input("another"), limits, Ct)).QueueFull);
        Assert.Equal(id, (await db.Jobs.SubmitAsync(input, limits, Ct)).Job!.JobId);
        Assert.Null(await db.Jobs.FindAsync(db.OtherAccount, id, Ct));
        Assert.Null(await db.Jobs.FindSubmissionAsync(db.OtherAccount, input.Key, Ct));
        Assert.Empty(await db.Jobs.ListAsync(db.OtherAccount, db.Canvas, null, 50, Ct));
        Assert.False(await db.Jobs.RetryDownloadAsync(db.OtherAccount, id, Limits, Ct));
        Assert.False(await db.Jobs.RetryDownloadAsync(db.Account, id, Limits, Ct));
    }

    /// <summary>验证并发认领不突破全局、账号或服务商配额。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task ClaimsEnforceGlobalUserAndProviderLimitsAcrossConcurrentWorkers()
    {
        await using var db = await ImageJobDatabase.CreateAsync();
        var provider2 = Guid.NewGuid();
        for (var index = 0; index < 12; index++)
        {
            var input = db.Input($"request-{index}") with {
                AccountId = index % 2 == 0 ? db.Account : db.OtherAccount,
                DeviceId = index % 2 == 0 ? db.Device : db.OtherDevice,
                ProviderId = index % 3 == 0 ? provider2 : db.Provider };
            await db.Jobs.SubmitAsync(input, Limits, Ct);
        }
        var claimed = (await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => db.Jobs.ClaimAsync(Limits, Ct))))
            .Where(job => job is not null).Select(job => job!).ToArray();
        Assert.Equal(4, claimed.Length);
        Assert.All(claimed.GroupBy(job => job.AccountId), group => Assert.True(group.Count() <= 2));
        Assert.All(claimed.GroupBy(job => job.ProviderId), group => Assert.True(group.Count() <= 2));
        Assert.Equal(4, claimed.Select(job => job.JobId).Distinct().Count());
        await db.Jobs.FinishAsync(claimed[0], "test", Ct);
        Assert.NotNull(await db.Jobs.ClaimAsync(Limits, Ct));
    }

    /// <summary>验证部分输出成功后重试保存不会复制素材。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task CheckpointDownloadRetrySavesExactlyOnceAndPreservesPartialResults()
    {
        await using var db = await ImageJobDatabase.CreateAsync();
        var id = (await db.Jobs.SubmitAsync(db.Input(), Limits, Ct)).Job!.JobId;
        var job = (await db.Jobs.ClaimAsync(Limits, Ct))!;
        await db.Jobs.CheckpointAsync(job, "https://provider.invalid/v1", [new("image/png"), new("image/png", DownloadUrl: "https://cdn.invalid/two.png?secret=private")], Ct);
        var outputs = await db.Jobs.OutputsAsync(id, Ct);
        var first = await SaveFile(db, outputs[0], [1, 2]);
        await db.Jobs.SaveOutputAsync(job, outputs[0], first, Ct);
        await db.Jobs.FinishAsync(job, "download failed", Ct);
        Assert.True((await db.Jobs.FindAsync(db.Account, id, Ct))!.HasCheckpoint);
        Assert.True(await db.Jobs.RetryDownloadAsync(db.Account, id, Limits, Ct));
        Assert.True(await db.Jobs.RetryDownloadAsync(db.Account, id, Limits, Ct));
        var retry = (await db.Jobs.ClaimAsync(Limits, Ct))!;
        Assert.Equal("saving", retry.Status);
        outputs = await db.Jobs.OutputsAsync(id, Ct);
        Assert.True(outputs[0].Saved);
        Assert.Equal(first.StoragePath, outputs[0].StoragePath);
        var second = await SaveFile(db, outputs[1], [3, 4]);
        await db.Jobs.SaveOutputAsync(retry, outputs[1], second, Ct);
        await db.Jobs.SaveOutputAsync(retry, outputs[1], second, Ct);
        await db.Jobs.FinishAsync(retry, null, Ct);
        Assert.Equal("succeeded", (await db.Jobs.FindAsync(db.Account, id, Ct))!.Status);
        Assert.Equal(2, await db.CountAsync("SELECT count(*) FROM canvas_asset"));
        Assert.Equal(2, await db.CountAsync("SELECT count(*) FROM storage_object"));
        Assert.Equal(1, await db.CountAsync("SELECT count(*) FROM canvas_generation_request WHERE status = 'succeeded'"));
    }

    /// <summary>验证过期生成不重发、检查点可恢复且旧租约不能写入。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task ExpiredGenerationIsNotRegeneratedButCheckpointCanResumeWithFencing()
    {
        await using var db = await ImageJobDatabase.CreateAsync();
        var unknown = (await db.Jobs.SubmitAsync(db.Input("unknown"), Limits, Ct)).Job!;
        var saving = (await db.Jobs.SubmitAsync(db.Input("saving"), Limits, Ct)).Job!;
        var first = (await db.Jobs.ClaimAsync(Limits, Ct))!;
        var second = (await db.Jobs.ClaimAsync(Limits, Ct))!;
        await db.Jobs.CheckpointAsync(second, "https://provider.invalid", [new("image/png")], Ct);
        await db.SqlAsync("UPDATE image_job SET lease_expires_at = now() - interval '1 second'");
        var recovered = (await db.Jobs.ClaimAsync(Limits, Ct))!;
        Assert.Equal(saving.JobId, recovered.JobId);
        Assert.Equal("saving", recovered.Status);
        Assert.NotEqual(second.LeaseId, recovered.LeaseId);
        Assert.Null(await db.Jobs.ClaimAsync(Limits, Ct));
        Assert.Equal("failed", (await db.Jobs.FindAsync(db.Account, unknown.JobId, Ct))!.Status);
        Assert.False(await db.Jobs.HeartbeatAsync(first.JobId, first.LeaseId!.Value, Ct));
        var output = Assert.Single(await db.Jobs.OutputsAsync(recovered.JobId, Ct));
        var file = await SaveFile(db, output, [1]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Jobs.SaveOutputAsync(second, output, file, Ct));
        await db.Jobs.SaveOutputAsync(recovered, output, file, Ct);
        await db.Jobs.FinishAsync(recovered, null, Ct);
    }

    private static async Task<StoredAsset> SaveFile(ImageJobDatabase db, ImageJobOutput output, byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        return await db.Store.SaveAsync(stream, "image/png", ".png", 25L * 1024 * 1024, Ct, output.StorageOperationKey);
    }
}
