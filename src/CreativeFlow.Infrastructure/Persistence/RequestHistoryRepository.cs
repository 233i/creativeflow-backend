using Npgsql;
using NpgsqlTypes;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Contracts.Admin;
using CreativeFlow.Domain.Identity;

namespace CreativeFlow.Infrastructure.Persistence;

/// <summary>独立管理员请求查询；所有筛选值使用参数，业务记录写入再次验证画布归属。</summary>
public sealed class RequestHistoryRepository(NpgsqlDataSource dataSource) : IRequestHistoryRepository
{
    /// <summary>画布列表与详情共用的投影，输入和输出只在详情查询读取。</summary>
    private const string CanvasProjection = """
        SELECT r.request_id AS id, 'canvas' AS source, r.kind, r.status, r.account_id,
               a.display_name, a.email, r.canvas_id AS project_id,
               COALESCE(c.name, r.canvas_name, '未关联画布') AS project_name,
               r.node_id, r.model_key, p.name AS provider_name, r.created_at, r.finished_at,
               r.error, r.historical, r.device_id, r.input::text AS input_json, r.output::text AS output_json
        FROM admin_canvas_request_history r
        LEFT JOIN creativeflow_account a ON a.account_id = r.account_id
        LEFT JOIN canvas c ON c.canvas_id = r.canvas_id
        LEFT JOIN ai_provider p ON p.provider_id = r.provider_id
        """;

    /// <summary>工作流按每次运行展示，用户通过执行设备关联，不按当前管理员身份推断。</summary>
    private const string WorkflowProjection = """
        SELECT r.run_id AS id, 'workflow' AS source, 'workflow' AS kind, r.status, ad.account_id,
               a.display_name, a.email, r.workflow_id AS project_id, w.name AS project_name,
               NULL::text AS node_id, NULL::text AS model_key, NULL::text AS provider_name,
               r.created_at, r.finished_at, r.error, false AS historical,
               r.device_id, r.graph_snapshot::text AS input_json, NULL::text AS output_json
        FROM workflow_run r
        JOIN workflow w ON w.workflow_id = r.workflow_id
        LEFT JOIN creativeflow_account_device ad ON ad.device_id = r.device_id
        LEFT JOIN creativeflow_account a ON a.account_id = ad.account_id
        """;

    /// <summary>两个来源使用相同的用户、状态筛选语义。</summary>
    private const string Filter = """
        WHERE (@AccountId IS NULL OR h.account_id = @AccountId)
          AND (@Search IS NULL OR h.display_name ILIKE '%' || @Search || '%'
               OR h.email ILIKE '%' || @Search || '%' OR h.account_id::text = @Search)
          AND (@Status IS NULL OR h.status = @Status)
        """;

    /// <summary>按数据库类型传递可空值，避免 SQL 对空参数类型进行猜测。</summary>
    private static void Parameter(NpgsqlCommand command, string name, NpgsqlDbType type, object? value) =>
        command.Parameters.AddWithValue(name, type, value ?? DBNull.Value);

    /// <summary>构造固定的来源投影，拒绝将调用方字符串拼接为表名。</summary>
    private static string Projection(string source) => source switch
    {
        "canvas" => CanvasProjection,
        "workflow" => WorkflowProjection,
        _ => throw new ArgumentException("Unsupported request history source.", nameof(source)),
    };

    /// <summary>保存生成请求的起始记录，仅关联属于当前设备或账号的画布上下文。</summary>
    public async Task StartAsync(GenerationHistoryStart request, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        // 客户端提供的画布标识只是上下文线索，不能让请求被归到其他账号画布。
        command.CommandText = """
            INSERT INTO canvas_generation_request
                (request_id, device_id, account_id, canvas_id, canvas_name, node_id, kind,
                 provider_id, model_key, input, status, created_at)
            SELECT @Id, @DeviceId, @AccountId, c.canvas_id, c.name, @NodeId, @Kind,
                   @ProviderId, @ModelKey, @Input, 'running', @CreatedAt
            FROM (SELECT 1) seed
            LEFT JOIN canvas c ON c.canvas_id = @CanvasId
                AND (c.device_id = @DeviceId OR EXISTS (
                    SELECT 1 FROM creativeflow_account_device ad
                    WHERE ad.device_id = c.device_id AND ad.account_id = @AccountId))
            """;
        Parameter(command, "Id", NpgsqlDbType.Uuid, request.Id);
        Parameter(command, "DeviceId", NpgsqlDbType.Uuid, request.DeviceId);
        Parameter(command, "AccountId", NpgsqlDbType.Uuid, request.AccountId);
        Parameter(command, "CanvasId", NpgsqlDbType.Uuid, request.CanvasId);
        Parameter(command, "NodeId", NpgsqlDbType.Text, request.NodeId);
        Parameter(command, "Kind", NpgsqlDbType.Text, request.Kind);
        Parameter(command, "ProviderId", NpgsqlDbType.Uuid, request.ProviderId);
        Parameter(command, "ModelKey", NpgsqlDbType.Text, request.ModelKey);
        Parameter(command, "Input", NpgsqlDbType.Jsonb, request.InputJson);
        Parameter(command, "CreatedAt", NpgsqlDbType.TimestampTz, request.CreatedAt);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>按请求标识记录终态、输出、错误和关联视频任务，并写入完成时间。</summary>
    public async Task FinishAsync(Guid id, string status, string? output, string? error, Guid? videoJobId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE canvas_generation_request
            SET status = @Status, output = @Output, error = @Error, video_job_id = @VideoJobId,
                finished_at = CASE WHEN @Status = 'running' THEN NULL ELSE now() END
            WHERE request_id = @Id
            """;
        Parameter(command, "Id", NpgsqlDbType.Uuid, id);
        Parameter(command, "Status", NpgsqlDbType.Text, status);
        Parameter(command, "Output", NpgsqlDbType.Jsonb, output);
        Parameter(command, "Error", NpgsqlDbType.Text, error);
        Parameter(command, "VideoJobId", NpgsqlDbType.Uuid, videoJobId);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>向分页和计数查询绑定同一组筛选条件。</summary>
    private static void Filters(NpgsqlCommand command, RequestHistoryQuery query)
    {
        Parameter(command, "AccountId", NpgsqlDbType.Uuid, query.AccountId);
        Parameter(command, "Search", NpgsqlDbType.Text, query.Search);
        Parameter(command, "Status", NpgsqlDbType.Text, query.Status);
    }

    /// <summary>按来源及筛选条件分页读取后台请求摘要和总数，列表不加载大型输入输出 JSON。</summary>
    public async Task<AdminRequestPage> ListAsync(RequestHistoryQuery query, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        // 只在详情读取大型输入、输出 JSON；列表分页保持与产物体积无关。
        var from = $"FROM ({Projection(query.Source)}) h {Filter}";
        await using var count = connection.CreateCommand();
        count.CommandText = $"SELECT COUNT(*) {from}";
        Filters(count, query);
        var total = (long)(await count.ExecuteScalarAsync(ct))!;
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, source, kind, status, account_id, display_name, email, project_id, project_name,
                   node_id, model_key, provider_name, created_at, finished_at, error, historical
            {from} ORDER BY created_at DESC, id DESC OFFSET @Offset LIMIT @Limit
            """;
        Filters(command, query);
        Parameter(command, "Offset", NpgsqlDbType.Integer, query.Offset);
        Parameter(command, "Limit", NpgsqlDbType.Integer, query.Limit);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var items = new List<AdminRequestSummary>();
        while (await reader.ReadAsync(ct)) items.Add(Summary(reader));
        return new(items.ToArray(), total, query.Offset, query.Limit);
    }

    /// <summary>读取可空列，字段名来自固定 SQL 投影。</summary>
    private static T? Optional<T>(NpgsqlDataReader reader, string name) where T : struct =>
        reader.IsDBNull(reader.GetOrdinal(name)) ? null : reader.GetFieldValue<T>(reader.GetOrdinal(name));

    /// <summary>读取可空字符串列。</summary>
    private static string? String(NpgsqlDataReader reader, string name) =>
        reader.IsDBNull(reader.GetOrdinal(name)) ? null : reader.GetString(reader.GetOrdinal(name));

    /// <summary>把固定查询投影转为列表契约，不携带内部存储路径。</summary>
    private static AdminRequestSummary Summary(NpgsqlDataReader reader) => new(
        reader.GetGuid(reader.GetOrdinal("id")).ToString(), String(reader, "source")!, String(reader, "kind")!,
        String(reader, "status")!, Optional<Guid>(reader, "account_id")?.ToString(), String(reader, "display_name"),
        String(reader, "email"), Optional<Guid>(reader, "project_id")?.ToString(), String(reader, "project_name")!,
        String(reader, "node_id"), String(reader, "model_key"), String(reader, "provider_name"),
        reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at")), Optional<DateTimeOffset>(reader, "finished_at"),
        String(reader, "error"), reader.GetBoolean(reader.GetOrdinal("historical")));

    /// <summary>按来源与标识读取完整请求详情，工作流详情额外加载节点执行记录。</summary>
    public async Task<RequestHistoryDocument?> FindAsync(string source, Guid id, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM ({Projection(source)}) h WHERE id = @Id";
        Parameter(command, "Id", NpgsqlDbType.Uuid, id);
        RequestHistoryDocument result;
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) return null;
            result = new(Summary(reader), reader.GetGuid(reader.GetOrdinal("device_id")),
                String(reader, "input_json") ?? "{}", String(reader, "output_json"), []);
        }
        if (source == "canvas") return result;
        // 先释放运行读取器，再读取节点，避免在一个连接上并行操作多个结果集。
        command.CommandText = """
            SELECT node_id, node_type, status, is_reused, output::text AS output_json, error, started_at, finished_at
            FROM workflow_node_run WHERE run_id = @Id ORDER BY started_at NULLS LAST, node_id
            """;
        await using var nodesReader = await command.ExecuteReaderAsync(ct);
        var nodes = new List<RequestHistoryNode>();
        while (await nodesReader.ReadAsync(ct))
            nodes.Add(new(String(nodesReader, "node_id")!, String(nodesReader, "node_type")!, String(nodesReader, "status")!,
                nodesReader.GetBoolean(nodesReader.GetOrdinal("is_reused")), String(nodesReader, "output_json"),
                String(nodesReader, "error"), Optional<DateTimeOffset>(nodesReader, "started_at"), Optional<DateTimeOffset>(nodesReader, "finished_at")));
        return result with { Nodes = nodes };
    }

    /// <summary>按原运行设备或账号范围查找产物素材，防止旧图中伪造的其他账号素材被作为产物读取。</summary>
    public async Task<IReadOnlyList<CanvasAsset>> FindArtifactsAsync(Guid deviceId, Guid? accountId, Guid[] ids, CancellationToken ct)
    {
        if (ids.Length == 0) return [];
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        // 即使旧运行图里被写入了别人的素材 UUID，也不把它变成管理员详情的产物。
        command.CommandText = """
            SELECT asset_id, device_id, kind, media_type, storage_path, byte_size, prompt, created_at
            FROM canvas_asset asset WHERE asset_id = ANY(@Ids)
              AND (device_id = @DeviceId OR EXISTS (
                  SELECT 1 FROM creativeflow_account_device ad
                  WHERE ad.device_id = asset.device_id AND ad.account_id = @AccountId))
            """;
        Parameter(command, "Ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid, ids);
        Parameter(command, "DeviceId", NpgsqlDbType.Uuid, deviceId);
        Parameter(command, "AccountId", NpgsqlDbType.Uuid, accountId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var assets = new List<CanvasAsset>();
        while (await reader.ReadAsync(ct))
            assets.Add(new(reader.GetGuid(0), new DeviceId(reader.GetGuid(1)), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetInt64(5), String(reader, "prompt"), reader.GetFieldValue<DateTimeOffset>(7)));
        return assets;
    }
}
