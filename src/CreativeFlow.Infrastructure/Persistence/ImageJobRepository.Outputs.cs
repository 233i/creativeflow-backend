using Npgsql;
using NpgsqlTypes;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;

namespace CreativeFlow.Infrastructure.Persistence;

public sealed partial class ImageJobRepository
{
    /// <summary>锁定任务并校验当前租约，禁止过期执行者写入。</summary>
    private static async Task GuardLeaseAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        ImageJob job, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            SELECT job_id FROM image_job WHERE job_id = @Id AND lease_id = @Lease
                AND status IN ('generating','saving') FOR UPDATE
            """, connection, transaction);
        P(command, "Id", NpgsqlDbType.Uuid, job.JobId);
        P(command, "Lease", NpgsqlDbType.Uuid, job.LeaseId);
        if (await command.ExecuteScalarAsync(ct) is not Guid)
            throw new InvalidOperationException("图片任务执行租约已失效。");
    }

    /// <summary>在持有有效租约的事务内保存上游图片输出与阶段检查点，重复保存同一输出时保留原素材标识。</summary>
    public async Task CheckpointAsync(ImageJob job, string providerBaseUrl, IReadOnlyList<ImageJobOutputSource> outputs, CancellationToken ct)
    {
        // 全部输出及阶段标记必须一起提交；重复保存同一响应时保留原素材 ID。
        if (outputs.Count == 0) throw new InvalidOperationException("上游未返回可保存的图片。");
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await GuardLeaseAsync(connection, transaction, job, ct);
        for (var index = 0; index < Math.Min(4, outputs.Count); index++)
        {
            var output = outputs[index];
            await using var insert = new NpgsqlCommand("""
                INSERT INTO image_job_output(job_id,output_index,asset_id,media_type,download_url,
                    provider_base_url,source_url,source_url_expires_at,storage_operation_key)
                VALUES (@Id,@Index,@Asset,@Type,@Url,@Base,@Source,@Expiry,@Operation)
                ON CONFLICT (job_id,output_index) DO NOTHING
                """, connection, transaction);
            P(insert, "Id", NpgsqlDbType.Uuid, job.JobId);
            P(insert, "Index", NpgsqlDbType.Integer, index);
            P(insert, "Asset", NpgsqlDbType.Uuid, Guid.CreateVersion7());
            P(insert, "Type", NpgsqlDbType.Text, output.MediaType);
            P(insert, "Operation", NpgsqlDbType.Text, $"image-job:{job.JobId:N}:{index}");
            P(insert, "Url", NpgsqlDbType.Text, output.DownloadUrl);
            P(insert, "Base", NpgsqlDbType.Text, providerBaseUrl);
            P(insert, "Source", NpgsqlDbType.Text, output.SourceUrl);
            P(insert, "Expiry", NpgsqlDbType.TimestampTz, output.SourceUrlExpiresAt);
            await insert.ExecuteNonQueryAsync(ct);
        }
        await using (var update = new NpgsqlCommand("""
            UPDATE image_job SET has_checkpoint = true, status = 'saving', lease_expires_at = now() + interval '90 seconds'
            WHERE job_id = @Id
            """, connection, transaction))
        {
            P(update, "Id", NpgsqlDbType.Uuid, job.JobId);
            await update.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }

    /// <summary>按输出序号读取轻量文件引用和下载恢复信息，不加载任何图片字节。</summary>
    public async Task<IReadOnlyList<ImageJobOutput>> OutputsAsync(Guid jobId, CancellationToken ct)
    {
        await using var command = source.CreateCommand("""
            SELECT output_index,asset_id,media_type,storage_path,download_url,
                provider_base_url,source_url,source_url_expires_at,saved,storage_operation_key
            FROM image_job_output WHERE job_id = @Id ORDER BY output_index
            """);
        P(command, "Id", NpgsqlDbType.Uuid, jobId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var outputs = new List<ImageJobOutput>();
        while (await reader.ReadAsync(ct)) outputs.Add(new(reader.GetInt32(0), reader.GetGuid(1), reader.GetString(2),
            Text(reader, 3), Text(reader, 4), Text(reader, 5), Text(reader, 6),
            Nullable<DateTimeOffset>(reader, 7), reader.GetBoolean(8), reader.GetString(9)));
        return outputs;
    }

    /// <summary>在有效租约下原子关联已确认文件、素材元数据和完成标记，重复登记不产生新素材。</summary>
    public async Task SaveOutputAsync(ImageJob job, ImageJobOutput output, StoredAsset file, CancellationToken ct)
    {
        if (file.ByteSize <= 0) throw new InvalidOperationException("上游图片文件为空。");
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await GuardLeaseAsync(connection, transaction, job, ct);
        // 文件已经持久化；这里仅提交元数据。事务失败后按固定操作键找回同一文件。
        await using var command = new NpgsqlCommand("""
            INSERT INTO canvas_asset(asset_id,device_id,kind,media_type,storage_path,byte_size,prompt,source_url,source_url_expires_at,sha256)
                SELECT @Asset,j.device_id,'image',@Type,@Path,@Size,j.request->>'prompt',@Source,@Expiry,s.sha256
                FROM image_job j JOIN storage_object s ON s.storage_path=@Path AND s.state='ready' AND s.byte_size=@Size
                WHERE j.job_id = @Id AND EXISTS(SELECT 1 FROM storage_write w
                    WHERE w.operation_key=@Operation AND w.storage_path=s.storage_path AND w.state='ready')
                ON CONFLICT (asset_id) DO NOTHING;
            UPDATE image_job_output SET saved = true, storage_path=@Path, media_type = @Type
                WHERE job_id = @Id AND output_index = @Index
                  AND EXISTS(SELECT 1 FROM canvas_asset WHERE asset_id=@Asset AND storage_path=@Path);
            """, connection, transaction);
        P(command, "Path", NpgsqlDbType.Text, file.StoragePath);
        P(command, "Type", NpgsqlDbType.Text, file.MediaType);
        P(command, "Asset", NpgsqlDbType.Uuid, output.AssetId);
        P(command, "Size", NpgsqlDbType.Bigint, file.ByteSize);
        P(command, "Source", NpgsqlDbType.Text, output.SourceUrl);
        P(command, "Expiry", NpgsqlDbType.TimestampTz, output.SourceUrlExpiresAt);
        P(command, "Id", NpgsqlDbType.Uuid, job.JobId);
        P(command, "Index", NpgsqlDbType.Integer, output.Index);
        P(command, "Operation", NpgsqlDbType.Text, output.StorageOperationKey);
        await command.ExecuteNonQueryAsync(ct);
        command.CommandText = "SELECT saved AND storage_path=@Path FROM image_job_output WHERE job_id=@Id AND output_index=@Index";
        if (await command.ExecuteScalarAsync(ct) is not true) throw new InvalidOperationException("已落盘文件与图片输出不一致，未完成保存。");
        await transaction.CommitAsync(ct);
    }

    /// <summary>在租约匹配时写入图片任务终态并同步请求记录；成功必须有检查点且全部输出已保存。</summary>
    public async Task FinishAsync(ImageJob job, string? error, CancellationToken ct)
    {
        await using var command = source.CreateCommand("""
            WITH finished AS (
                UPDATE image_job SET status = CASE WHEN @Error IS NULL THEN 'succeeded' ELSE 'failed' END,
                    failure_stage = CASE WHEN @Error IS NULL THEN NULL WHEN has_checkpoint THEN 'saving' ELSE 'generating' END,
                    error = @Error, finished_at = now(), lease_id = NULL, lease_expires_at = NULL
                WHERE job_id = @Id AND lease_id = @Lease
                    AND (@Error IS NOT NULL OR (has_checkpoint AND NOT EXISTS (
                        SELECT 1 FROM image_job_output o WHERE o.job_id = @Id AND NOT saved)))
                RETURNING job_id,status,error,finished_at
            ) UPDATE canvas_generation_request r SET status = f.status, error = f.error, finished_at = f.finished_at,
                output = (SELECT jsonb_build_object('assets', COALESCE(jsonb_agg(jsonb_build_object(
                    'url','/api/v1/canvas/assets/' || o.asset_id,'mediaType',o.media_type,'assetId',o.asset_id)
                    ORDER BY o.output_index), '[]'::jsonb)) FROM image_job_output o WHERE o.job_id = f.job_id AND o.saved)
                FROM finished f WHERE r.request_id = f.job_id
            """);
        P(command, "Id", NpgsqlDbType.Uuid, job.JobId);
        P(command, "Lease", NpgsqlDbType.Uuid, job.LeaseId);
        P(command, "Error", NpgsqlDbType.Text, error is { Length: > 2000 } ? error[..2000] : error);
        await command.ExecuteNonQueryAsync(ct);
    }
}
