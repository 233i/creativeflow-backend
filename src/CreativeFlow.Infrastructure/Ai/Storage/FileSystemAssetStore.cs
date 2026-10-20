using Npgsql;
using NpgsqlTypes;
using Microsoft.Extensions.Logging;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Infrastructure.Options;

namespace CreativeFlow.Infrastructure.Ai;

/// <summary>唯一媒体存储：磁盘保存正文，PostgreSQL 仅保存写入租约、内容摘要和引用状态。</summary>
internal sealed partial class FileSystemAssetStore(NpgsqlDataSource source, AssetFileSystem files, AiOptions options,
    ILogger<FileSystemAssetStore> logger) : IAssetStore
{
    /// <summary>协议已返回内联字节时不复制整份内容，仍走同一流式、可恢复的保存流程。</summary>
    public async Task<StoredAsset> SaveAsync(byte[] bytes, string mediaType, string extensionHint, CancellationToken ct)
    {
        await using var stream = new MemoryStream(bytes, writable: false);
        return await SaveAsync(stream, mediaType, extensionHint, options.MaxAssetBytes, ct);
    }

    /// <summary>先预留元数据，再有界落盘，最后登记不可变内容；存储失败绝不重发模型请求。</summary>
    public async Task<StoredAsset> SaveAsync(Stream stream, string mediaType, string extensionHint, long maxBytes,
        CancellationToken ct, string? operationKey = null)
    {
        if (maxBytes <= 0 || string.IsNullOrWhiteSpace(mediaType) || mediaType.Length > 200)
            throw new ArgumentException("文件类型或大小限制无效。");
        var operation = operationKey ?? $"upload:{Guid.NewGuid():N}";
        if (operation.Length is < 1 or > 300) throw new ArgumentException("文件操作标识无效。");
        if (await FindSavedAsync(operation, ct) is { } saved)
        {
            if (saved.MediaType != mediaType || saved.ByteSize > Math.Min(maxBytes, options.MaxAssetBytes))
                throw new InvalidOperationException("同一文件操作不能改变文件类型或越过大小限制。");
            return saved;
        }
        var write = await ReserveAsync(operation, mediaType, Math.Min(maxBytes, options.MaxAssetBytes), ct);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.AssetWriteTimeoutSeconds));
        (string Hash, long Size) value;
        try { value = await files.WriteAsync(write, stream, deadline.Token); }
        catch
        {
            // 已知未产生完整检查点，允许只重试上传/下载；不把失败租约阻塞十五分钟。
            using var releaseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await using var release = source.CreateCommand("UPDATE storage_write SET lease_expires_at=now(),updated_at=now() WHERE write_id=@Id AND lease_id=@Lease AND state='writing'");
                P(release, "Id", write.Id); P(release, "Lease", write.Lease);
                await release.ExecuteNonQueryAsync(releaseTimeout.Token);
            }
            catch (Exception ex) { logger.LogWarning(ex, "Could not release interrupted file write {WriteId}; expiry will recover it.", write.Id); }
            throw;
        }
        return await FinalizeAsync(write, value.Hash, value.Size, deadline.Token);
    }

    /// <summary>完整临时文件或已发布文件均可恢复；半文件只允许原租约继续写入。</summary>
    public async Task<StoredAsset?> FindSavedAsync(string operationKey, CancellationToken ct)
    {
        var write = await ReadWriteAsync(operationKey, ct);
        if (write is null || write.State == "abandoned") return null;
        if (write.State == "ready")
        {
            return write.Path is { } path && files.Exists(path) && await ReadyAsync(path, ct)
                ? new(path, write.MediaType, write.Size!.Value) : null;
        }
        var complete = AssetFileSystem.PendingKey(write.Id, write.Lease, "complete");
        if (files.Exists(complete))
        {
            var value = await files.InspectAsync(complete, write.MaxBytes, ct);
            return await FinalizeAsync(write, value.Hash, value.Size, ct);
        }
        // Finalize 已持久化摘要，但文件 rename 后的业务提交失败：按原摘要核对后补登记。
        if (write.Hash is { } hash && write.Size is { } size && files.Exists(AssetFileSystem.ObjectKey(hash)))
            return await FinalizeAsync(write, hash, size, ct);
        return null;
    }

    /// <summary>只返回可定位的文件流，不将整个媒体读入托管内存；没有任何数据库正文回退。</summary>
    public async Task<Stream?> OpenReadAsync(string storagePath, CancellationToken ct)
    {
        if (!AssetFileSystem.IsObjectKey(storagePath) || !await ReadyAsync(storagePath, ct)) return null;
        return files.Open(storagePath);
    }

    /// <summary>在引用域内登记删除意图，磁盘删除失败保持 deleting，后续清理只重试删除。</summary>
    public async Task DeleteAsync(string storagePath, CancellationToken ct)
    {
        if (!AssetFileSystem.IsObjectKey(storagePath)) return;
        await using (var connection = await source.OpenConnectionAsync(ct))
        await using (var tx = await connection.BeginTransactionAsync(ct))
        {
            await using var command = new NpgsqlCommand("""
                SELECT pg_advisory_xact_lock(721909190);
                INSERT INTO asset_storage_cleanup(storage_path) VALUES(@Path) ON CONFLICT DO NOTHING;
                UPDATE storage_object SET state='deleting',updated_at=now()
                WHERE storage_path=@Path AND state<>'deleted' AND retain_until<now()
                  AND NOT creativeflow_file_is_referenced(storage_path)
                RETURNING storage_path
                """, connection, tx);
            P(command, "Path", storagePath);
            // 第一条 SELECT void 不作为 ExecuteScalar 的返回值。
            var allowed = false;
            await using (var reader = await command.ExecuteReaderAsync(ct))
                do { while (await reader.ReadAsync(ct)) if (reader.FieldCount == 1 && reader.GetDataTypeName(0) == "text") allowed = true; }
                while (await reader.NextResultAsync(ct));
            await tx.CommitAsync(ct);
            if (!allowed) return;
        }
        // 第二阶段再次串行化；防止两个清理器中的迟到者删除刚被重新上传的同摘要文件。
        await using var finishConnection = await source.OpenConnectionAsync(ct);
        await using var finishTx = await finishConnection.BeginTransactionAsync(ct);
        await using var finish = new NpgsqlCommand("SELECT pg_advisory_xact_lock(721909190)", finishConnection, finishTx);
        await finish.ExecuteNonQueryAsync(ct);
        finish.CommandText = "SELECT state FROM storage_object WHERE storage_path=@Path FOR UPDATE";
        P(finish, "Path", storagePath);
        if (await finish.ExecuteScalarAsync(ct) is not "deleting") return;
        files.Delete(storagePath);
        finish.CommandText = """
            UPDATE storage_object SET state='deleted',updated_at=now() WHERE storage_path=@Path AND state='deleting';
            DELETE FROM asset_storage_cleanup WHERE storage_path=@Path
            """;
        await finish.ExecuteNonQueryAsync(ct);
        await finishTx.CommitAsync(ct);
    }

    /// <summary>先保存恢复摘要，再短事务发布文件。文件移动与数据库提交之间中断仍能按操作键恢复。</summary>
    private async Task<StoredAsset> FinalizeAsync(StorageWrite write, string hash, long size, CancellationToken ct)
    {
        if (write.Hash is { } expected && expected != hash) throw new InvalidDataException("恢复文件与已确认摘要不一致。");
        await using (var checkpoint = source.CreateCommand("""
            UPDATE storage_write SET sha256=@Hash,byte_size=@Size,updated_at=now()
            WHERE write_id=@Id AND lease_id=@Lease AND state='writing'
            """))
        {
            P(checkpoint, "Hash", hash); P(checkpoint, "Size", size); P(checkpoint, "Id", write.Id); P(checkpoint, "Lease", write.Lease);
            await checkpoint.ExecuteNonQueryAsync(ct);
        }
        var path = AssetFileSystem.ObjectKey(hash);
        var complete = AssetFileSystem.PendingKey(write.Id, write.Lease, "complete");
        // 校验可能耗时的现有大文件在事务外完成；发布路径不可变，只能由本适配器按摘要创建。
        if (files.Exists(path))
        {
            try
            {
                var existing = await files.InspectAsync(path, write.MaxBytes, ct);
                if (existing.Hash != hash || existing.Size != size) throw new InvalidDataException("存储对象校验失败，禁止覆盖。");
            }
            catch (FileNotFoundException) { /* 与无引用对象清理竞争时，下面用完整检查点重新发布。 */ }
        }
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT pg_advisory_xact_lock_shared(721909190);
            SELECT pg_advisory_xact_lock(hashtextextended(@Path,721909194));
            """, connection, tx);
        P(command, "Path", path); P(command, "Id", write.Id); P(command, "Lease", write.Lease);
        await command.ExecuteNonQueryAsync(ct);
        command.CommandText = "SELECT state FROM storage_write WHERE write_id=@Id AND lease_id=@Lease FOR UPDATE";
        var state = await command.ExecuteScalarAsync(ct) as string;
        if (state is not ("writing" or "ready")) throw new IOException("文件写入租约已失效。");
        command.CommandText = "SELECT state FROM storage_object WHERE storage_path=@Path FOR UPDATE";
        if (await command.ExecuteScalarAsync(ct) is "deleting") throw new IOException("相同文件仍在清理，请稍后重试保存。");
        if (files.Exists(path))
        {
            using var existing = files.Open(path)!;
            if (existing.Length != size) throw new InvalidDataException("存储对象大小发生变化，禁止覆盖。");
        }
        else if (!files.Exists(complete)) throw new FileNotFoundException("完整文件检查点已丢失，不能重新生成替代。");
        if (files.Exists(complete)) files.Publish(complete, path);
        command.CommandText = """
            INSERT INTO storage_object(storage_path,sha256,media_type,byte_size,state,retain_until)
            VALUES(@Path,@Hash,@Type,@Size,'ready',now()+interval '1 hour')
            ON CONFLICT(storage_path) DO UPDATE SET state='ready',retain_until=now()+interval '1 hour',updated_at=now();
            UPDATE storage_write SET state='ready',storage_path=@Path,sha256=@Hash,byte_size=@Size,
                lease_expires_at=now()+interval '1 hour',updated_at=now() WHERE write_id=@Id AND lease_id=@Lease
            """;
        P(command, "Hash", hash); P(command, "Type", write.MediaType); P(command, "Size", size);
        await command.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        files.RemoveEmptyPending(write.Id);
        return new(path, write.MediaType, size);
    }

    /// <summary>内容可读性来自元数据终态，deleting 期间禁止建立新的媒体响应。</summary>
    private async Task<bool> ReadyAsync(string path, CancellationToken ct)
    {
        await using var command = source.CreateCommand("SELECT EXISTS(SELECT 1 FROM storage_object WHERE storage_path=@Path AND state='ready')");
        P(command, "Path", path); return (bool)(await command.ExecuteScalarAsync(ct))!;
    }

    private static void P(NpgsqlCommand command, string name, string value) => command.Parameters.AddWithValue(name, NpgsqlDbType.Text, value);
    private static void P(NpgsqlCommand command, string name, Guid value) => command.Parameters.AddWithValue(name, NpgsqlDbType.Uuid, value);
    private static void P(NpgsqlCommand command, string name, long value) => command.Parameters.AddWithValue(name, NpgsqlDbType.Bigint, value);
}
