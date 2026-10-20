using Npgsql;
using NpgsqlTypes;
using CreativeFlow.Application.Abstractions.Persistence;

namespace CreativeFlow.Infrastructure.Persistence;

/// <summary>队列短事务使用数据库锁，跨进程共享配额；长耗时生成永远不占数据库事务。</summary>
public sealed partial class ImageJobRepository(NpgsqlDataSource source) : IImageJobRepository
{
    /// <summary>任务快照的固定投影，顺序与读取器一致。</summary>
    private const string Projection = """
        SELECT j.job_id, j.account_id, j.device_id, j.provider_id, j.canvas_id, j.node_id,
               j.request::text, j.status, j.failure_stage, j.error, j.created_at,
               j.started_at, j.finished_at, j.lease_id, j.has_checkpoint, j.request_hash FROM image_job j
        """;

    /// <summary>添加显式 PostgreSQL 类型参数并统一映射空值。</summary>
    private static void P(NpgsqlCommand command, string name, NpgsqlDbType type, object? value) =>
        command.Parameters.AddWithValue(name, type, value ?? DBNull.Value);

    /// <summary>读取可空值类型列。</summary>
    private static T? Nullable<T>(NpgsqlDataReader reader, int index) where T : struct =>
        reader.IsDBNull(index) ? null : reader.GetFieldValue<T>(index);

    /// <summary>读取可空文本列。</summary>
    private static string? Text(NpgsqlDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : reader.GetString(index);

    /// <summary>将固定列投影还原为内部任务快照。</summary>
    private static ImageJob Read(NpgsqlDataReader reader) => new(reader.GetGuid(0), reader.GetGuid(1),
        reader.GetGuid(2), reader.GetGuid(3), Nullable<Guid>(reader, 4), Text(reader, 5), reader.GetString(6),
        reader.GetString(7), Text(reader, 8), Text(reader, 9), reader.GetFieldValue<DateTimeOffset>(10),
        Nullable<DateTimeOffset>(reader, 11), Nullable<DateTimeOffset>(reader, 12), Nullable<Guid>(reader, 13), reader.GetBoolean(14), reader.GetString(15));

    /// <summary>读取单一任务，找不到时返回空。</summary>
    private static async Task<ImageJob?> ReadOneAsync(NpgsqlCommand command, CancellationToken ct)
    {
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    /// <summary>提交后复用现有连接读取结果，避免额外占用连接池。</summary>
    private static async Task<ImageJob?> ReadAcceptedAsync(NpgsqlConnection connection, Guid jobId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(Projection + " WHERE j.job_id = @Id", connection);
        P(command, "Id", NpgsqlDbType.Uuid, jobId);
        return await ReadOneAsync(command, ct);
    }

    /// <summary>以短事务锁串行化跨进程的队列配额决策。</summary>
    private static async Task LockQueueAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(73401822002)", connection, transaction);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>合并计算全局及账号的待完成任务上限。</summary>
    private static async Task<bool> QueueFullAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid accountId, ImageQueueLimits limits, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            SELECT count(*) >= @Global OR count(*) FILTER (WHERE account_id = @Account) >= @User
            FROM image_job WHERE status IN ('queued','generating','saving')
            """, connection, transaction);
        P(command, "Global", NpgsqlDbType.Integer, limits.MaxPendingGlobal);
        P(command, "User", NpgsqlDbType.Integer, limits.MaxPendingPerUser);
        P(command, "Account", NpgsqlDbType.Uuid, accountId);
        return (bool)(await command.ExecuteScalarAsync(ct))!;
    }

    /// <summary>按账号和提交幂等键查找首次受理的图片任务，重试继续返回原任务。</summary>
    public async Task<ImageJob?> FindSubmissionAsync(Guid accountId, Guid key, CancellationToken ct)
    {
        await using var command = source.CreateCommand(Projection + """
             JOIN image_job_submission s ON s.job_id = j.job_id
             WHERE s.account_id = @Account AND s.submission_key = @Key
            """);
        P(command, "Account", NpgsqlDbType.Uuid, accountId);
        P(command, "Key", NpgsqlDbType.Uuid, key);
        return await ReadOneAsync(command, ct);
    }

    /// <summary>在账号归属范围内按标识查询图片任务，不允许读取其他账号任务。</summary>
    public async Task<ImageJob?> FindAsync(Guid accountId, Guid jobId, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(Projection + " WHERE j.account_id = @Account AND j.job_id = @Id", connection);
        P(command, "Account", NpgsqlDbType.Uuid, accountId);
        P(command, "Id", NpgsqlDbType.Uuid, jobId);
        return await ReadOneAsync(command, ct);
    }

    /// <summary>按账号、画布和可选游标倒序分页读取图片任务。</summary>
    public async Task<IReadOnlyList<ImageJob>> ListAsync(Guid accountId, Guid canvasId, Guid? before, int limit, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(Projection + """
             WHERE j.account_id = @Account AND j.canvas_id = @Canvas AND (@Before IS NULL OR j.job_id < @Before)
             ORDER BY j.job_id DESC LIMIT @Limit
            """, connection);
        P(command, "Account", NpgsqlDbType.Uuid, accountId);
        P(command, "Canvas", NpgsqlDbType.Uuid, canvasId);
        P(command, "Before", NpgsqlDbType.Uuid, before);
        P(command, "Limit", NpgsqlDbType.Integer, limit);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<ImageJob>();
        while (await reader.ReadAsync(ct)) result.Add(Read(reader));
        return result;
    }

    /// <summary>在队列锁与事务内受理图片任务，先处理幂等及活动任务合并，再检查容量并记录提交键。</summary>
    public async Task<ImageJobSubmission> SubmitAsync(ImageJobSubmissionInput input, ImageQueueLimits limits, CancellationToken ct)
    {
        // 幂等键优先于容量检查；合并活动任务仍记录新键，使所有网络重试稳定返回原任务。
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await LockQueueAsync(connection, transaction, ct);
        Guid? existing;
        await using (var command = new NpgsqlCommand("""
            SELECT job_id, request_hash FROM image_job_submission WHERE account_id = @Account AND submission_key = @Key
            """, connection, transaction))
        {
            P(command, "Account", NpgsqlDbType.Uuid, input.AccountId);
            P(command, "Key", NpgsqlDbType.Uuid, input.Key);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                if (reader.GetString(1) != input.RequestHash) return new(null, Conflict: true);
                existing = reader.GetGuid(0);
            }
            else existing = null;
        }
        if (existing.HasValue)
        {
            await transaction.CommitAsync(ct);
            return new(await ReadAcceptedAsync(connection, existing.Value, ct));
        }
        Guid? duplicate = null;
        if (!input.ForceNew)
        {
            await using var command = new NpgsqlCommand("""
                SELECT job_id FROM image_job WHERE account_id = @Account AND request_hash = @Hash
                AND status IN ('queued','generating','saving') ORDER BY created_at LIMIT 1
                """, connection, transaction);
            P(command, "Account", NpgsqlDbType.Uuid, input.AccountId);
            P(command, "Hash", NpgsqlDbType.Text, input.RequestHash);
            duplicate = await command.ExecuteScalarAsync(ct) is Guid id ? id : null;
        }
        if (!duplicate.HasValue && await QueueFullAsync(connection, transaction, input.AccountId, limits, ct))
            return new(null, QueueFull: true);
        var jobId = duplicate ?? Guid.CreateVersion7();
        if (!duplicate.HasValue)
        {
            await using var command = new NpgsqlCommand("""
                INSERT INTO image_job(job_id,account_id,device_id,provider_id,canvas_id,node_id,request,request_hash,status)
                VALUES (@Id,@Account,@Device,@Provider,@Canvas,@Node,@Request,@Hash,'queued');
                INSERT INTO canvas_generation_request(request_id,device_id,account_id,canvas_id,canvas_name,
                    node_id,kind,provider_id,model_key,input,status,created_at)
                SELECT @Id,@Device,@Account,c.canvas_id,c.name,@Node,'image',@Provider,@Request->>'modelKey',@Request,'running',now()
                FROM (SELECT 1) seed LEFT JOIN canvas c ON c.canvas_id = @Canvas
                    AND EXISTS (SELECT 1 FROM creativeflow_account_device ad WHERE ad.device_id = c.device_id AND ad.account_id = @Account);
                """, connection, transaction);
            P(command, "Id", NpgsqlDbType.Uuid, jobId);
            P(command, "Account", NpgsqlDbType.Uuid, input.AccountId);
            P(command, "Device", NpgsqlDbType.Uuid, input.DeviceId);
            P(command, "Provider", NpgsqlDbType.Uuid, input.ProviderId);
            P(command, "Canvas", NpgsqlDbType.Uuid, input.CanvasId);
            P(command, "Node", NpgsqlDbType.Text, input.NodeId);
            P(command, "Request", NpgsqlDbType.Jsonb, input.RequestJson);
            P(command, "Hash", NpgsqlDbType.Text, input.RequestHash);
            await command.ExecuteNonQueryAsync(ct);
        }
        await using (var command = new NpgsqlCommand("""
            INSERT INTO image_job_submission(account_id,submission_key,request_hash,job_id) VALUES (@Account,@Key,@Hash,@Id)
            """, connection, transaction))
        {
            P(command, "Account", NpgsqlDbType.Uuid, input.AccountId);
            P(command, "Key", NpgsqlDbType.Uuid, input.Key);
            P(command, "Hash", NpgsqlDbType.Text, input.RequestHash);
            P(command, "Id", NpgsqlDbType.Uuid, jobId);
            await command.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return new(await ReadAcceptedAsync(connection, jobId, ct));
    }
}
