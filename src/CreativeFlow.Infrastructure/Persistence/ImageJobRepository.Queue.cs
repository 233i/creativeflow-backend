using Npgsql;
using NpgsqlTypes;
using CreativeFlow.Application.Abstractions.Persistence;

namespace CreativeFlow.Infrastructure.Persistence;

public sealed partial class ImageJobRepository
{
    /// <summary>持有队列锁恢复过期租约并按原并发配额认领任务；无检查点的未知结果标记失败，不重新生成。</summary>
    public async Task<ImageJob?> ClaimAsync(ImageQueueLimits limits, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await LockQueueAsync(connection, transaction, ct);
        // 没有检查点意味着无法知道上游是否收费成功。绝不把这类任务重新排队生成。
        await using (var recover = new NpgsqlCommand("""
            WITH recovered AS (
                UPDATE image_job SET status = CASE WHEN has_checkpoint THEN 'queued' ELSE 'failed' END,
                    failure_stage = CASE WHEN has_checkpoint THEN NULL ELSE 'generating' END,
                    error = CASE WHEN has_checkpoint THEN NULL ELSE '执行连接中断，上游结果未知；系统未自动重新生成。可重新查询本系统已保存的结果，或联系服务商核实。' END,
                    finished_at = CASE WHEN has_checkpoint THEN NULL ELSE now() END, lease_id = NULL, lease_expires_at = NULL
                WHERE status IN ('generating','saving') AND lease_expires_at < now()
                RETURNING job_id, status, error, finished_at
            ) UPDATE canvas_generation_request r SET status = CASE WHEN x.status = 'queued' THEN 'running' ELSE x.status END,
                error = x.error, finished_at = x.finished_at FROM recovered x WHERE r.request_id = x.job_id
            """, connection, transaction)) await recover.ExecuteNonQueryAsync(ct);
        Guid? id;
        await using (var select = new NpgsqlCommand("""
            SELECT q.job_id FROM image_job q WHERE q.status = 'queued'
                AND (SELECT count(*) FROM image_job WHERE status IN ('generating','saving')) < @Global
                AND (SELECT count(*) FROM image_job u WHERE u.account_id = q.account_id AND u.status IN ('generating','saving')) < @User
                AND (SELECT count(*) FROM image_job p WHERE p.provider_id = q.provider_id AND p.status IN ('generating','saving')) < @Provider
            ORDER BY q.created_at, q.job_id LIMIT 1 FOR UPDATE SKIP LOCKED
            """, connection, transaction))
        {
            P(select, "Global", NpgsqlDbType.Integer, limits.GlobalConcurrency);
            P(select, "User", NpgsqlDbType.Integer, limits.UserConcurrency);
            P(select, "Provider", NpgsqlDbType.Integer, limits.ProviderConcurrency);
            id = await select.ExecuteScalarAsync(ct) is Guid found ? found : null;
        }
        if (!id.HasValue) { await transaction.CommitAsync(ct); return null; }
        var lease = Guid.NewGuid();
        await using (var claim = new NpgsqlCommand("""
            UPDATE image_job SET status = CASE WHEN has_checkpoint THEN 'saving' ELSE 'generating' END,
                started_at = COALESCE(started_at, now()), lease_id = @Lease, lease_expires_at = now() + interval '90 seconds'
            WHERE job_id = @Id
            """, connection, transaction))
        {
            P(claim, "Id", NpgsqlDbType.Uuid, id);
            P(claim, "Lease", NpgsqlDbType.Uuid, lease);
            await claim.ExecuteNonQueryAsync(ct);
        }
        ImageJob? job;
        await using (var query = new NpgsqlCommand(Projection + " WHERE j.job_id = @Id", connection, transaction))
        {
            P(query, "Id", NpgsqlDbType.Uuid, id);
            job = await ReadOneAsync(query, ct);
        }
        await transaction.CommitAsync(ct);
        return job;
    }

    /// <summary>仅为租约匹配且仍在生成或保存的图片任务续租 90 秒，返回是否续租成功。</summary>
    public async Task<bool> HeartbeatAsync(Guid jobId, Guid leaseId, CancellationToken ct)
    {
        await using var command = source.CreateCommand("""
            UPDATE image_job SET lease_expires_at = now() + interval '90 seconds'
            WHERE job_id = @Id AND lease_id = @Lease AND status IN ('generating','saving')
            """);
        P(command, "Id", NpgsqlDbType.Uuid, jobId);
        P(command, "Lease", NpgsqlDbType.Uuid, leaseId);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <summary>在账号范围与队列限制内重新排队有检查点的下载恢复，对已活动或已完成恢复保持幂等。</summary>
    public async Task<bool> RetryDownloadAsync(Guid accountId, Guid jobId, ImageQueueLimits limits, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await LockQueueAsync(connection, transaction, ct);
        // 并发点击对已排队/运行的恢复是幂等操作，不改变其租约。
        await using (var active = new NpgsqlCommand("""
            SELECT EXISTS (SELECT 1 FROM image_job WHERE job_id = @Id AND account_id = @Account
                AND has_checkpoint AND status IN ('queued','saving','succeeded'))
            """, connection, transaction))
        {
            P(active, "Id", NpgsqlDbType.Uuid, jobId);
            P(active, "Account", NpgsqlDbType.Uuid, accountId);
            if ((bool)(await active.ExecuteScalarAsync(ct))!) { await transaction.CommitAsync(ct); return true; }
        }
        if (await QueueFullAsync(connection, transaction, accountId, limits, ct)) return false;
        await using var command = new NpgsqlCommand("""
            WITH retried AS (
                UPDATE image_job SET status = 'queued', error = NULL, failure_stage = NULL, finished_at = NULL,
                    lease_id = NULL, lease_expires_at = NULL
                WHERE job_id = @Id AND account_id = @Account AND status = 'failed' AND has_checkpoint RETURNING job_id
            ) UPDATE canvas_generation_request r SET status = 'running', error = NULL, finished_at = NULL
                FROM retried x WHERE r.request_id = x.job_id
            """, connection, transaction);
        P(command, "Id", NpgsqlDbType.Uuid, jobId);
        P(command, "Account", NpgsqlDbType.Uuid, accountId);
        var changed = await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return changed == 1;
    }
}
