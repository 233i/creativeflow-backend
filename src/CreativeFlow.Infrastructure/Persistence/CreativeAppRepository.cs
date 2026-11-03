using System.Text.Json.Nodes;
using Dapper;
using Npgsql;
using NpgsqlTypes;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Features.CreativeApps;
using CreativeFlow.Application.Features.Workflow;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;

namespace CreativeFlow.Infrastructure.Persistence;

/// <summary>公共应用发布与任务准入使用独立事务，继续由既有工作流 Worker 执行。</summary>
public sealed partial class CreativeAppRepository(CreativeFlowConnectionFactory connections) : ICreativeAppRepository
{
    /// <summary>发布记录的查询列顺序，与读取映射保持一致。</summary>
    private const string ReleaseColumns = "r.release_id,r.workflow_id,r.version,r.metadata::text,r.graph_snapshot::text,r.input_schema::text,r.output_schema::text,r.max_concurrency,r.user_concurrency,r.daily_limit,r.created_at";
    /// <summary>从固定查询列还原内部发布快照。</summary>
    private static CreativeAppRelease ReadRelease(NpgsqlDataReader row) => new(row.GetGuid(0), row.GetGuid(1), row.GetInt32(2),
        JsonNode.Parse(row.GetString(3))!.AsObject(), row.GetString(4), row.GetString(5), row.GetString(6),
        row.GetInt32(7), row.GetInt32(8), row.GetInt32(9), row.GetFieldValue<DateTimeOffset>(10));
    /// <summary>按显式 PostgreSQL 类型绑定参数，空值转换为数据库空值。</summary>
    private static void Add(NpgsqlCommand command, string name, object? value, NpgsqlDbType type) => command.Parameters.AddWithValue(name, type, value ?? DBNull.Value);
    /// <summary>创建使用指定连接和事务的命令。</summary>
    private static NpgsqlCommand Command(NpgsqlConnection connection, string sql, NpgsqlTransaction? transaction = null) => new(sql, connection, transaction);

    /// <summary>只读取上架版本的可用封面，调用者不因此获得发布者其他素材的权限。</summary>
    public async Task<CanvasAsset?> FindCoverAsync(Guid workflowId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var command = Command(connection, """
            SELECT c.asset_id,c.device_id,c.kind,c.media_type,c.storage_path,c.byte_size,c.created_at
            FROM creative_app a JOIN creative_app_release r ON r.release_id=a.active_release_id
            JOIN workflow w ON w.workflow_id=a.workflow_id AND w.deleted_at IS NULL
            JOIN canvas_asset c ON c.asset_id=(r.metadata->>'coverAssetId')::uuid
            WHERE a.workflow_id=@Id AND c.kind='image' AND c.deleted_at IS NULL AND c.purged_at IS NULL AND NOT c.purge_pending
            """);
        Add(command, "Id", workflowId, NpgsqlDbType.Uuid);
        await using var rows = await command.ExecuteReaderAsync(ct);
        return await rows.ReadAsync(ct) ? new CanvasAsset(rows.GetGuid(0), new DeviceId(rows.GetGuid(1)), rows.GetString(2), rows.GetString(3), rows.GetString(4), rows.GetInt64(5), null, rows.GetFieldValue<DateTimeOffset>(6)) : null;
    }

    /// <summary>分页读取当前上架应用，返回已脱敏的表单与展示信息。</summary>
    public async Task<JsonArray> ListAsync(string? search, string? kind, int offset, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var command = Command(connection, $"""
            SELECT {ReleaseColumns} FROM creative_app a JOIN creative_app_release r ON r.release_id=a.active_release_id
            JOIN workflow w ON w.workflow_id=a.workflow_id AND w.deleted_at IS NULL
            WHERE (@Search IS NULL OR r.metadata->>'name' ILIKE '%'||@Search||'%' OR r.metadata->>'description' ILIKE '%'||@Search||'%')
            AND (@Kind IS NULL OR r.metadata->>'category'=@Kind) ORDER BY a.updated_at DESC,a.workflow_id LIMIT 50 OFFSET @Offset
            """);
        Add(command, "Search", search, NpgsqlDbType.Text); Add(command, "Kind", kind, NpgsqlDbType.Text); Add(command, "Offset", offset, NpgsqlDbType.Integer);
        await using var rows = await command.ExecuteReaderAsync(ct);
        var result = new JsonArray(); while (await rows.ReadAsync(ct)) result.Add((JsonNode)CreativeAppContract.Describe(ReadRelease(rows)));
        return result;
    }

    /// <summary>读取指定发布版本；未提供版本时只返回当前上架版本。</summary>
    public async Task<CreativeAppRelease?> FindAsync(Guid workflowId, Guid? releaseId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var command = Command(connection, $"""
            SELECT {ReleaseColumns} FROM creative_app a JOIN creative_app_release r ON r.workflow_id=a.workflow_id
            JOIN workflow w ON w.workflow_id=a.workflow_id AND w.deleted_at IS NULL
            WHERE a.workflow_id=@Id AND r.release_id=COALESCE(@Release,a.active_release_id)
            """);
        Add(command, "Id", workflowId, NpgsqlDbType.Uuid); Add(command, "Release", releaseId, NpgsqlDbType.Uuid);
        await using var rows = await command.ExecuteReaderAsync(ct);
        return await rows.ReadAsync(ct) ? ReadRelease(rows) : null;
    }

    /// <summary>读取管理员可管理的发布历史及当前上架版本。</summary>
    public async Task<JsonObject> ManagementAsync(Guid workflowId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var command = Command(connection, $"SELECT {ReleaseColumns},a.active_release_id FROM creative_app a JOIN creative_app_release r ON r.workflow_id=a.workflow_id WHERE a.workflow_id=@Id ORDER BY r.version DESC");
        Add(command, "Id", workflowId, NpgsqlDbType.Uuid);
        await using var rows = await command.ExecuteReaderAsync(ct);
        var versions = new JsonArray(); string? active = null;
        while (await rows.ReadAsync(ct))
        {
            var release = ReadRelease(rows); active = rows.IsDBNull(11) ? null : rows.GetGuid(11).ToString();
            var item = CreativeAppContract.Describe(release);
            item["maxConcurrency"] = release.MaxConcurrency;
            item["coverAssetId"] = release.Metadata["coverAssetId"]?.DeepClone();
            item["shareFixedAssets"] = release.Metadata["shareFixedAssets"]?.DeepClone();
            versions.Add((JsonNode)item);
        }
        return new JsonObject { ["activeReleaseId"] = active, ["versions"] = versions };
    }

    /// <summary>保存不可变版本并切换上架指针，同时记录管理员发布审计。</summary>
    public async Task<CreativeAppRelease> PublishAsync(CreativeAppRelease release, Guid actor, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        // Acquire the existing asset lock before workflow/app locks, matching asset retention operations.
        await using var command = Command(connection, "SELECT pg_advisory_xact_lock_shared(721909190); SELECT creativeflow_lock_live_workflow(@Id); INSERT INTO creative_app(workflow_id) VALUES(@Id) ON CONFLICT DO NOTHING; SELECT workflow_id FROM creative_app WHERE workflow_id=@Id FOR UPDATE", transaction);
        Add(command, "Id", release.WorkflowId, NpgsqlDbType.Uuid);
        await command.ExecuteNonQueryAsync(ct);
        await ValidateReleaseAssetsAsync(connection, transaction, release, ct);
        command.CommandText = "SELECT COALESCE(MAX(version),0)+1 FROM creative_app_release WHERE workflow_id=@Id";
        var version = Convert.ToInt32(await command.ExecuteScalarAsync(ct));
        command.CommandText = """
            INSERT INTO creative_app_release(release_id,workflow_id,version,metadata,graph_snapshot,input_schema,output_schema,max_concurrency,user_concurrency,daily_limit,created_by)
            VALUES(@Release,@Id,@Version,@Metadata,@Graph,@Inputs,@Outputs,@Concurrency,@UserConcurrency,@Daily,@Actor);
            UPDATE creative_app SET active_release_id=@Release,updated_at=now() WHERE workflow_id=@Id;
            INSERT INTO creative_app_audit(event_id,workflow_id,release_id,actor_id,action) VALUES(@Event,@Id,@Release,@Actor,'publish');
            """;
        Add(command, "Release", release.Id, NpgsqlDbType.Uuid); Add(command, "Version", version, NpgsqlDbType.Integer);
        Add(command, "Metadata", release.Metadata.ToJsonString(), NpgsqlDbType.Jsonb); Add(command, "Graph", release.GraphJson, NpgsqlDbType.Jsonb);
        Add(command, "Inputs", release.InputSchemaJson, NpgsqlDbType.Jsonb); Add(command, "Outputs", release.OutputSchemaJson, NpgsqlDbType.Jsonb);
        Add(command, "Concurrency", release.MaxConcurrency, NpgsqlDbType.Integer); Add(command, "UserConcurrency", release.UserConcurrency, NpgsqlDbType.Integer);
        Add(command, "Daily", release.DailyLimit, NpgsqlDbType.Integer); Add(command, "Actor", actor, NpgsqlDbType.Uuid); Add(command, "Event", Guid.CreateVersion7(), NpgsqlDbType.Uuid);
        await command.ExecuteNonQueryAsync(ct); await transaction.CommitAsync(ct);
        return release with { Version = version };
    }

    /// <summary>切换有效发布版本或下架，不修改已受理任务的执行快照。</summary>
    public async Task<bool> ActivateAsync(Guid workflowId, Guid? releaseId, Guid actor, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var command = Command(connection, "SELECT pg_advisory_xact_lock_shared(721909190); SELECT creativeflow_lock_live_workflow(@Id); SELECT workflow_id FROM creative_app WHERE workflow_id=@Id FOR UPDATE", transaction);
        Add(command, "Id", workflowId, NpgsqlDbType.Uuid); await command.ExecuteNonQueryAsync(ct);
        Add(command, "Release", releaseId, NpgsqlDbType.Uuid);
        if (releaseId is not null)
        {
            command.CommandText = $"SELECT {ReleaseColumns} FROM creative_app_release r WHERE r.workflow_id=@Id AND r.release_id=@Release";
            CreativeAppRelease release;
            await using (var rows = await command.ExecuteReaderAsync(ct))
            {
                if (!await rows.ReadAsync(ct)) return false;
                release = ReadRelease(rows);
            }
            await ValidateReleaseAssetsAsync(connection, transaction, release, ct);
        }
        command.CommandText = """
            UPDATE creative_app SET active_release_id=@Release,updated_at=now() WHERE workflow_id=@Id
            AND (@Release IS NULL OR EXISTS(SELECT 1 FROM creative_app_release WHERE workflow_id=@Id AND release_id=@Release))
            """;
        if (await command.ExecuteNonQueryAsync(ct) == 0) return false;
        command.CommandText = "INSERT INTO creative_app_audit(event_id,workflow_id,release_id,actor_id,action) VALUES(@Event,@Id,@Release,@Actor,@Action)";
        Add(command, "Event", Guid.CreateVersion7(), NpgsqlDbType.Uuid); Add(command, "Actor", actor, NpgsqlDbType.Uuid);
        Add(command, "Action", releaseId is null ? "unpublish" : "activate", NpgsqlDbType.Text);
        await command.ExecuteNonQueryAsync(ct); await transaction.CommitAsync(ct); return true;
    }

    /// <summary>按客户、应用和请求键找回已受理任务，不产生新的模型调用。</summary>
    public async Task<Guid?> FindRequestAsync(AccountId account, Guid workflowId, string key, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var command = Command(connection, "SELECT run_id FROM creative_app_run WHERE account_id=@Account AND workflow_id=@Id AND idempotency_key=@Key");
        Add(command, "Account", account.Value, NpgsqlDbType.Uuid); Add(command, "Id", workflowId, NpgsqlDbType.Uuid); Add(command, "Key", key, NpgsqlDbType.Text);
        return await command.ExecuteScalarAsync(ct) is Guid id ? id : null;
    }

    /// <summary>只在指定客户账户内查找运行及其固定发布版本。</summary>
    public async Task<CreativeAppRun?> FindRunAsync(AccountId account, Guid runId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var run = await connection.QueryFirstOrDefaultAsync<WorkflowRunRow>("""
            SELECT r.run_id,r.workflow_id,r.device_id,r.graph_snapshot::text AS graph_json,r.status,r.error,r.created_at,r.updated_at,r.started_at,r.finished_at,r.trigger_type,r.input_json::text AS input_json
            FROM workflow_run r JOIN creative_app_run c ON c.run_id=r.run_id WHERE r.run_id=@RunId AND c.account_id=@AccountId
            """, new CreativeRunLookup { RunId = runId, AccountId = account.Value });
        if (run is null) return null;
        await using var command = Command(connection, $"SELECT {ReleaseColumns} FROM creative_app_release r JOIN creative_app_run c ON c.release_id=r.release_id WHERE c.run_id=@Run AND c.account_id=@Account");
        Add(command, "Run", runId, NpgsqlDbType.Uuid); Add(command, "Account", account.Value, NpgsqlDbType.Uuid);
        await using var rows = await command.ExecuteReaderAsync(ct);
        return await rows.ReadAsync(ct) ? new(run.ToDomain(), ReadRelease(rows)) : null;
    }

    /// <summary>分页读取指定账户的生成历史，可选限定应用。</summary>
    public async Task<JsonArray> ListRunsAsync(AccountId account, Guid? workflowId, int offset, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var command = Command(connection, """
            SELECT r.run_id,c.workflow_id,v.metadata->>'name',v.version,r.status,r.created_at,r.updated_at,v.release_id
            FROM creative_app_run c JOIN workflow_run r ON r.run_id=c.run_id JOIN creative_app_release v ON v.release_id=c.release_id
            WHERE c.account_id=@Account AND (@Id IS NULL OR c.workflow_id=@Id) ORDER BY c.created_at DESC,c.run_id DESC LIMIT 50 OFFSET @Offset
            """);
        Add(command, "Account", account.Value, NpgsqlDbType.Uuid); Add(command, "Id", workflowId, NpgsqlDbType.Uuid); Add(command, "Offset", offset, NpgsqlDbType.Integer);
        await using var rows = await command.ExecuteReaderAsync(ct); var result = new JsonArray();
        while (await rows.ReadAsync(ct)) result.Add((JsonNode)new JsonObject { ["id"] = rows.GetGuid(0).ToString(), ["appId"] = rows.GetGuid(1).ToString(), ["name"] = rows.GetString(2), ["version"] = rows.GetInt32(3), ["status"] = rows.GetString(4), ["createdAt"] = rows.GetFieldValue<DateTimeOffset>(5).ToString("O"), ["updatedAt"] = rows.GetFieldValue<DateTimeOffset>(6).ToString("O"), ["releaseId"] = rows.GetGuid(7).ToString() });
        return result;
    }

    /// <summary>仅取消客户自己的活动任务，终态任务保持原结果。</summary>
    public async Task<bool> CancelAsync(AccountId account, Guid runId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var command = Command(connection, "SELECT r.run_id FROM workflow_run r JOIN creative_app_run c ON c.run_id=r.run_id WHERE r.run_id=@Run AND c.account_id=@Account FOR UPDATE OF r", transaction);
        Add(command, "Run", runId, NpgsqlDbType.Uuid); Add(command, "Account", account.Value, NpgsqlDbType.Uuid);
        if (await command.ExecuteScalarAsync(ct) is null) return false;
        command.CommandText = """
            UPDATE workflow_run SET status='cancelled',updated_at=now(),finished_at=now() WHERE run_id=@Run AND status IN ('queued','running');
            UPDATE workflow_node_run SET status='cancelled',finished_at=now() WHERE run_id=@Run AND status IN ('queued','running','retrying')
            AND EXISTS(SELECT 1 FROM workflow_run WHERE run_id=@Run AND status='cancelled');
            """;
        await command.ExecuteNonQueryAsync(ct); await transaction.CommitAsync(ct); return true;
    }
}
internal sealed class CreativeRunLookup
{
    /// <summary>待查询的持久任务标识。</summary>
    public Guid RunId { get; init; }
    /// <summary>允许读取该任务的客户账户。</summary>
    public Guid AccountId { get; init; }
}
