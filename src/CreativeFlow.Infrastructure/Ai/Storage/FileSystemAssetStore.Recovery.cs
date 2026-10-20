using Npgsql;
using Microsoft.Extensions.Logging;

namespace CreativeFlow.Infrastructure.Ai;

internal sealed partial class FileSystemAssetStore
{
    private const string WriteColumns = "write_id,lease_id,operation_key,media_type,max_bytes,state,lease_expires_at,storage_path,sha256,byte_size";

    private static StorageWrite Read(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetInt64(4),
        r.GetString(5), r.GetFieldValue<DateTimeOffset>(6), r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? null : r.GetInt64(9));

    private async Task<StorageWrite?> ReadWriteAsync(string operation, CancellationToken ct)
    {
        await using var command = source.CreateCommand($"SELECT {WriteColumns} FROM storage_write WHERE operation_key=@Operation");
        P(command, "Operation", operation);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    /// <summary>稳定操作键只允许一个写入者；过期重试使用新租约路径，旧进程无法覆盖新检查点。</summary>
    private async Task<StorageWrite> ReserveAsync(string operation, string mediaType, long maxBytes, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var id = Guid.CreateVersion7(); var lease = Guid.NewGuid();
        await using var command = new NpgsqlCommand($"""
            INSERT INTO storage_write(write_id,operation_key,lease_id,lease_expires_at,media_type,max_bytes,state)
            VALUES(@Id,@Operation,@Lease,now()+interval '15 minutes',@Type,@Limit,'writing')
            ON CONFLICT(operation_key) DO NOTHING
            """, connection, tx);
        P(command, "Id", id); P(command, "Lease", lease); P(command, "Operation", operation); P(command, "Type", mediaType); P(command, "Limit", maxBytes);
        var inserted = await command.ExecuteNonQueryAsync(ct);
        command.CommandText = $"SELECT {WriteColumns} FROM storage_write WHERE operation_key=@Operation FOR UPDATE";
        StorageWrite current;
        await using (var reader = await command.ExecuteReaderAsync(ct)) { await reader.ReadAsync(ct); current = Read(reader); }
        if (inserted == 0)
        {
            if (current.MediaType != mediaType || current.MaxBytes != maxBytes) throw new InvalidOperationException("同一文件操作不能改变类型或大小限制。");
            if (current.State == "writing" && current.Expires > DateTimeOffset.UtcNow) throw new IOException("相同文件仍在写入；请先查询保存结果。");
            command.CommandText = $"""
                UPDATE storage_write SET lease_id=@Lease,lease_expires_at=now()+interval '15 minutes',
                    state='writing',updated_at=now() WHERE operation_key=@Operation RETURNING {WriteColumns}
                """;
            await using var reader = await command.ExecuteReaderAsync(ct); await reader.ReadAsync(ct); current = Read(reader);
        }
        await tx.CommitAsync(ct);
        return current;
    }

    /// <summary>后台恢复完整文件检查点、清除失效半文件，并仅回收没有业务或待保存输出引用的对象。</summary>
    internal async Task ReconcileAsync(CancellationToken ct)
    {
        var writes = new List<StorageWrite>();
        await using (var command = source.CreateCommand($"SELECT {WriteColumns} FROM storage_write WHERE state='writing' AND lease_expires_at<now() ORDER BY lease_expires_at LIMIT 50"))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) writes.Add(Read(reader));
        foreach (var write in writes)
        {
            try
            {
                if (await FindSavedAsync(write.Operation, ct) is not null) continue;
                await using var abandon = source.CreateCommand("UPDATE storage_write SET state='abandoned',updated_at=now() WHERE write_id=@Id AND lease_id=@Lease AND state='writing' AND lease_expires_at<now()");
                P(abandon, "Id", write.Id); P(abandon, "Lease", write.Lease);
                if (await abandon.ExecuteNonQueryAsync(ct) == 1) files.Delete(AssetFileSystem.PendingKey(write.Id, write.Lease, "part"));
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "File checkpoint {WriteId} needs reconciliation; other files will continue.", write.Id);
                await using var defer = source.CreateCommand("UPDATE storage_write SET lease_expires_at=now()+interval '5 minutes' WHERE write_id=@Id AND lease_id=@Lease AND state='writing'");
                P(defer, "Id", write.Id); P(defer, "Lease", write.Lease); await defer.ExecuteNonQueryAsync(ct);
            }
        }
        var paths = new List<string>();
        await using (var command = source.CreateCommand("""
            SELECT storage_path FROM storage_object WHERE state<>'deleted' AND retain_until<now()
                AND NOT creativeflow_file_is_referenced(storage_path) ORDER BY retain_until LIMIT 100
            """))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) paths.Add(reader.GetString(0));
        foreach (var path in paths)
        {
            try { await DeleteAsync(path, ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "File cleanup failed; persisted deletion will retry.");
                await using var defer = source.CreateCommand("UPDATE storage_object SET retain_until=now()+interval '5 minutes' WHERE storage_path=@Path AND state='deleting'");
                P(defer, "Path", path); await defer.ExecuteNonQueryAsync(ct);
            }
        }
        await PrunePendingAsync(ct);
        // 操作键仍被图片检查点引用时不得回收；匿名写入和已移除任务的临时记录不会无限增长。
        await using var cleanup = source.CreateCommand("""
            DELETE FROM storage_write w WHERE w.state IN ('ready','abandoned') AND w.lease_expires_at<now()
                AND NOT EXISTS(SELECT 1 FROM image_job_output o WHERE o.storage_operation_key=w.operation_key AND NOT o.saved)
                AND NOT EXISTS(SELECT 1 FROM video_job v WHERE w.operation_key='video-job:'||replace(v.job_id::text,'-','') AND v.asset_id IS NULL AND v.status<>'cancelled');
            DELETE FROM asset_storage_cleanup c WHERE NOT EXISTS(
                SELECT 1 FROM storage_object s WHERE s.storage_path=c.storage_path AND s.state<>'deleted')
            """);
        await cleanup.ExecuteNonQueryAsync(ct);
    }

    /// <summary>仅清理本系统生成且过保留期的临时键；逐项查租约，绝不递归扫描或删除任意路径。</summary>
    private async Task PrunePendingAsync(CancellationToken ct)
    {
        foreach (var candidate in files.ExpiredPending(DateTime.UtcNow.AddHours(-1)).Take(200))
        {
            ct.ThrowIfCancellationRequested();
            await using var check = source.CreateCommand("SELECT EXISTS(SELECT 1 FROM storage_write WHERE write_id=@Id AND lease_id=@Lease AND state='writing')");
            P(check, "Id", candidate.Id); P(check, "Lease", candidate.Lease);
            if (await check.ExecuteScalarAsync(ct) is true) continue;
            try { files.Delete(candidate.Key); files.RemoveEmptyPending(candidate.Id); }
            catch (IOException ex) { logger.LogWarning(ex, "Could not prune expired temporary file {WriteId}.", candidate.Id); }
        }
    }
}
