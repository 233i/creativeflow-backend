using Npgsql;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;

namespace CreativeFlow.Infrastructure.Persistence;

public sealed partial class VideoJobRepository
{
    /// <summary>不在事务中传输视频；只关联该任务已确认的文件，并同时完成素材和任务元数据。</summary>
    public async Task CompleteFileAsync(VideoJob job, StoredAsset file, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock_shared(721909190)", connection, tx);
        await command.ExecuteNonQueryAsync(ct);
        command.CommandText = "SELECT status FROM video_job WHERE job_id=@Id AND device_id=@Device FOR UPDATE";
        command.Parameters.AddWithValue("Id", job.JobId);
        command.Parameters.AddWithValue("Device", job.DeviceId.Value);
        if (await command.ExecuteScalarAsync(ct) is not ("pending" or "running")) return;
        command.CommandText = """
            INSERT INTO canvas_asset(asset_id,device_id,kind,media_type,storage_path,byte_size,sha256)
            SELECT @Asset,@Device,'video',@Type,s.storage_path,s.byte_size,s.sha256
            FROM storage_object s JOIN storage_write w ON w.storage_path=s.storage_path
            WHERE s.storage_path=@Path AND s.state='ready' AND s.byte_size=@Size
                AND w.state='ready' AND w.operation_key=@Operation
            """;
        var assetId = Guid.CreateVersion7();
        command.Parameters.AddWithValue("Asset", assetId);
        command.Parameters.AddWithValue("Type", file.MediaType);
        command.Parameters.AddWithValue("Path", file.StoragePath);
        command.Parameters.AddWithValue("Size", file.ByteSize);
        command.Parameters.AddWithValue("Operation", $"video-job:{job.JobId:N}");
        if (await command.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("视频文件检查点未就绪，未完成任务。");
        command.CommandText = "UPDATE video_job SET status='succeeded',progress=1,asset_id=@Asset,error=NULL,updated_at=now() WHERE job_id=@Id";
        await command.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }
}
