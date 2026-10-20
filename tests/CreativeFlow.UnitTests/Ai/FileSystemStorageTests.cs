using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using CreativeFlow.Infrastructure.Ai;
using CreativeFlow.Infrastructure.Options;
using CreativeFlow.UnitTests.Support;

namespace CreativeFlow.UnitTests.Ai;

/// <summary>真实磁盘加隔离数据库的存储恢复测试，不调用模型或触碰已有媒体。</summary>
public sealed class FileSystemStorageTests
{
    public static bool NoDatabase => ImageJobDatabase.NoDatabase;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(Skip = "需要隔离测试库", SkipWhen = nameof(NoDatabase))]
    public async Task LargeNonSeekableUploadUsesBoundedReadsAndReturnsSeekableFile()
    {
        await using var db = await ImageJobDatabase.CreateAsync();
        using var input = new GeneratedStream(32L * 1024 * 1024);
        var file = await db.Store.SaveAsync(input, "video/mp4", ".mp4", input.Remaining, Ct);
        Assert.InRange(input.LargestRead, 1, 131072);
        Assert.Equal(32L * 1024 * 1024, file.ByteSize);
        await using var output = await db.Store.OpenReadAsync(file.StoragePath, Ct);
        Assert.IsType<FileStream>(output);
        Assert.True(output.CanSeek);
        output.Seek(-8, SeekOrigin.End);
        var tail = new byte[8]; await output.ReadExactlyAsync(tail, Ct);
        Assert.All(tail, x => Assert.Equal(0x5a, x));
        Assert.Equal(0, await db.CountAsync("SELECT count(*) FROM information_schema.columns WHERE table_schema=current_schema() AND table_name IN ('storage_object','storage_write','image_job_output') AND data_type='bytea'"));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(db.FileRoot, "pending"), "*", SearchOption.AllDirectories));
    }

    [Fact(Skip = "需要隔离测试库", SkipWhen = nameof(NoDatabase))]
    public async Task ConcurrentDeduplicationAndReopenUseOneImmutableObject()
    {
        await using var db = await ImageJobDatabase.CreateAsync();
        var writes = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => db.Store.SaveAsync([1, 2, 3], "image/png", ".png", Ct)));
        Assert.Single(writes.Select(x => x.StoragePath).Distinct());
        Assert.Equal(1, await db.CountAsync("SELECT count(*) FROM storage_object"));
        var options = new AiOptions { AssetRoot = db.FileRoot };
        var restarted = new FileSystemAssetStore(db.Source, new AssetFileSystem(options), options, NullLogger<FileSystemAssetStore>.Instance);
        await using var content = await restarted.OpenReadAsync(writes[0].StoragePath, Ct);
        Assert.Equal(3, content!.Length);
        Assert.Null(await restarted.OpenReadAsync("cloud/legacy", Ct));
        Assert.Null(await restarted.OpenReadAsync("../../etc/passwd", Ct));
    }

    [Fact(Skip = "需要隔离测试库", SkipWhen = nameof(NoDatabase))]
    public async Task FilePublishedBeforeDatabaseFailureRecoversWithoutReadingAnotherSource()
    {
        await using var db = await ImageJobDatabase.CreateAsync();
        await db.SqlAsync("""
            CREATE FUNCTION fail_registration() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'synthetic disk metadata failure'; END $$;
            CREATE TRIGGER fail_registration BEFORE INSERT ON storage_object FOR EACH ROW EXECUTE FUNCTION fail_registration();
            """);
        using var input = new MemoryStream(new byte[] { 8, 9, 10 });
        await Assert.ThrowsAsync<PostgresException>(() => db.Store.SaveAsync(input, "image/png", ".png", 1024, Ct, "recovery-test"));
        Assert.Equal(0, await db.CountAsync("SELECT count(*) FROM storage_object"));
        Assert.Equal(1, await db.CountAsync("SELECT count(*) FROM storage_write WHERE state='writing' AND sha256 IS NOT NULL"));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(db.FileRoot, "objects"), "*", SearchOption.AllDirectories));
        await db.SqlAsync("DROP TRIGGER fail_registration ON storage_object");
        var saved = await db.Store.FindSavedAsync("recovery-test", Ct);
        Assert.NotNull(saved);
        // 关闭且不可读的源代表上游原件已过期，幂等恢复也不应尝试读取它。
        using var unavailable = new MemoryStream(); unavailable.Close();
        Assert.Equal(saved, await db.Store.SaveAsync(unavailable, "image/png", ".png", 1024, Ct, "recovery-test"));
        Assert.Equal(1, await db.CountAsync("SELECT count(*) FROM storage_object WHERE state='ready'"));
    }

    [Fact(Skip = "需要隔离测试库", SkipWhen = nameof(NoDatabase))]
    public async Task SizeLimitAndInterruptedCopiesNeverBecomeReadableAndCanRetryImmediately()
    {
        await using var db = await ImageJobDatabase.CreateAsync();
        using (var tooLarge = new GeneratedStream(4096))
            await Assert.ThrowsAsync<InvalidDataException>(() => db.Store.SaveAsync(tooLarge, "image/png", ".png", 1024, Ct, "limited"));
        Assert.Null(await db.Store.FindSavedAsync("limited", Ct));
        using (var broken = new BrokenStream())
            await Assert.ThrowsAsync<IOException>(() => db.Store.SaveAsync(broken, "image/png", ".png", 1024, Ct, "broken"));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(db.FileRoot, "pending"), "*", SearchOption.AllDirectories));
        using var retry = new MemoryStream(new byte[] { 1, 2 });
        var saved = await db.Store.SaveAsync(retry, "image/png", ".png", 1024, Ct, "broken");
        Assert.Equal(2, saved.ByteSize);
    }

    [Fact(Skip = "需要隔离测试库", SkipWhen = nameof(NoDatabase))]
    public async Task ReclaimedLeaseFencesLateWriterAndCleansItsTemporaryFile()
    {
        await using var db = await ImageJobDatabase.CreateAsync();
        using var slow = new PausedStream();
        var old = db.Store.SaveAsync(slow, "image/png", ".png", 1024, Ct, "lease-test");
        await slow.Started.Task.WaitAsync(Ct);
        await db.SqlAsync("UPDATE storage_write SET lease_expires_at=now()-interval '1 second'");
        using var next = new MemoryStream(new byte[] { 2, 3 });
        var saved = await db.Store.SaveAsync(next, "image/png", ".png", 1024, Ct, "lease-test");
        slow.Resume.TrySetResult();
        await Assert.ThrowsAsync<IOException>(() => old);
        Assert.Equal(saved, await db.Store.FindSavedAsync("lease-test", Ct));
        foreach (var path in Directory.EnumerateFiles(Path.Combine(db.FileRoot, "pending"), "*", SearchOption.AllDirectories))
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-2));
        await db.Store.ReconcileAsync(Ct);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(db.FileRoot, "pending"), "*", SearchOption.AllDirectories));
        Assert.Equal(1, await db.CountAsync("SELECT count(*) FROM storage_object WHERE state='ready'"));
    }

    [Fact(Skip = "需要隔离测试库", SkipWhen = nameof(NoDatabase))]
    public async Task GarbageCollectionProtectsUnsavedImageCheckpointUntilJobIsRemoved()
    {
        await using var db = await ImageJobDatabase.CreateAsync();
        await db.Jobs.SubmitAsync(db.Input(), new(), Ct);
        var job = (await db.Jobs.ClaimAsync(new(), Ct))!;
        await db.Jobs.CheckpointAsync(job, "https://synthetic.invalid", [new("image/png")], Ct);
        var checkpoint = Assert.Single(await db.Jobs.OutputsAsync(job.JobId, Ct));
        using var stream = new MemoryStream(new byte[] { 1, 2, 3 });
        var saved = await db.Store.SaveAsync(stream, "image/png", ".png", 1024, Ct, checkpoint.StorageOperationKey);
        await db.Jobs.FinishAsync(job, "synthetic failure after file write", Ct);
        await db.SqlAsync("UPDATE storage_object SET retain_until=now()-interval '2 hours'; UPDATE storage_write SET lease_expires_at=now()-interval '2 hours'");
        await db.Store.ReconcileAsync(Ct);
        Assert.NotNull(await db.Store.FindSavedAsync(checkpoint.StorageOperationKey, Ct));
        await db.SqlAsync($"DELETE FROM image_job WHERE job_id='{job.JobId}'");
        await db.Store.ReconcileAsync(Ct);
        Assert.Null(await db.Store.OpenReadAsync(saved.StoragePath, Ct));
        Assert.Equal(0, await db.CountAsync("SELECT count(*) FROM storage_object WHERE state<>'deleted'"));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(db.FileRoot, "objects"), "*", SearchOption.AllDirectories));
    }

    [Fact(Skip = "需要隔离测试库", SkipWhen = nameof(NoDatabase))]
    public async Task DatabaseRejectsReferencesToMissingOrDeletedStorageObjects()
    {
        await using var db = await ImageJobDatabase.CreateAsync();
        var exception = await Assert.ThrowsAsync<PostgresException>(() => db.SqlAsync($"INSERT INTO canvas_asset(asset_id,device_id,kind,media_type,storage_path,byte_size) VALUES('{Guid.NewGuid()}','{db.Device}','image','image/png','missing',3)"));
        Assert.Equal("storage_file_not_ready", exception.ConstraintName);
        var saved = await db.Store.SaveAsync([1], "image/png", ".png", Ct);
        await db.SqlAsync("UPDATE storage_object SET retain_until=now()-interval '1 hour'");
        await Task.WhenAll(db.Store.DeleteAsync(saved.StoragePath, Ct), db.Store.DeleteAsync(saved.StoragePath, Ct));
        Assert.Null(await db.Store.OpenReadAsync(saved.StoragePath, Ct));
        var revived = await db.Store.SaveAsync([1], "image/png", ".png", Ct);
        Assert.Equal(saved.StoragePath, revived.StoragePath);
        await using var file = await db.Store.OpenReadAsync(saved.StoragePath, Ct);
        Assert.NotNull(file);
    }

    [Fact]
    public void MissingMountMarkerAndSymbolicLinkAreRejected()
    {
        var directory = Directory.CreateTempSubdirectory("creativeflow-storage-security-").FullName;
        try
        {
            var options = new AiOptions { AssetRoot = directory, RequireStorageMarker = true };
            Assert.Throws<InvalidOperationException>(() => new AssetFileSystem(options));
            File.WriteAllText(Path.Combine(directory, ".creativeflow-storage"), "test");
            var files = new AssetFileSystem(options);
            Assert.Throws<InvalidDataException>(() => files.Resolve("../outside"));
            if (!OperatingSystem.IsWindows())
            {
                Directory.CreateSymbolicLink(Path.Combine(directory, "objects", "aa"), Path.GetTempPath());
                Assert.Throws<IOException>(() => files.Resolve("objects/aa/anything"));
                Directory.Delete(Path.Combine(directory, "objects", "aa"));
            }
            File.Delete(Path.Combine(directory, ".creativeflow-storage"));
            Assert.Throws<IOException>(() => files.Resolve("objects"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private class GeneratedStream(long size) : Stream
    {
        public long Remaining { get; private set; } = size;
        public int LargestRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            LargestRead = Math.Max(LargestRead, buffer.Length);
            var count = (int)Math.Min(Remaining, buffer.Length); buffer[..count].Fill(0x5a); Remaining -= count; return count;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); return ValueTask.FromResult(Read(buffer.Span)); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class BrokenStream() : GeneratedStream(10)
    {
        private bool read;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (read) throw new IOException("synthetic interruption"); read = true;
            return base.ReadAsync(buffer, ct);
        }
    }

    private sealed class PausedStream() : GeneratedStream(1)
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            Started.TrySetResult(); await Resume.Task.WaitAsync(ct); return await base.ReadAsync(buffer, ct);
        }
    }
}
