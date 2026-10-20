using System.Data;
using Npgsql;
using NpgsqlTypes;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Contracts.Ai;

namespace CreativeFlow.Infrastructure.Persistence;

/// <summary>管理员中心的聚合查询与受保护删除。普通个人图不进入列表；分页不加载图、节点输出或密钥。</summary>
public sealed partial class WorkflowCenterRepository(NpgsqlDataSource dataSource) : IWorkflowCenterRepository
{
    /// <summary>统一两种来源并保留原 ID，最近整图运行与单节点调试分开统计。</summary>
    private const string Projection = """
        WITH items AS (
          SELECT w.workflow_id AS id, 'workflow'::text AS source, w.name,
            COALESCE(d.description, '') AS description, w.schema_version,
            CASE WHEN EXISTS (
              SELECT 1
              FROM jsonb_array_elements(COALESCE(w.graph->'nodes', '[]'::jsonb)) node
              WHERE node->>'type' IN ('start', 'llm', 'knowledgeRetrieve', 'knowledgeAnswer', 'condition', 'merge',
                                      'httpRequest', 'end', 'parameterExtract', 'intentClassify', 'transform')
            ) THEN 'general' ELSE 'media' END AS kind,
            COALESCE(d.status, 'unpublished') AS api_status, d.version AS api_version,
            'none'::text AS template_status, NULL::integer AS template_version,
            CASE WHEN d.deployment_id IS NULL THEN 'unpublished'
                 WHEN w.graph = d.graph_snapshot THEN 'saved' ELSE 'changed' END AS draft_status,
            CASE WHEN jsonb_typeof(w.graph->'nodes') = 'array'
                 THEN jsonb_array_length(w.graph->'nodes') ELSE 0 END AS node_count,
            w.updated_at, w.deleted_at, r.run_id, r.workflow_id AS run_workflow_id,
            r.trigger_type, r.status AS run_status, r.created_at AS run_created_at
          FROM workflow w
          LEFT JOIN LATERAL (
            SELECT deployment_id, description, status, version, graph_snapshot
            FROM workflow_deployment WHERE workflow_id = w.workflow_id
            ORDER BY version DESC LIMIT 1
          ) d ON true
          LEFT JOIN LATERAL (
            SELECT run_id, workflow_id, trigger_type, status, created_at
            FROM workflow_run WHERE workflow_id = w.workflow_id AND input_json->'nodeDebug' IS NULL
            ORDER BY created_at DESC, run_id DESC LIMIT 1
          ) r ON true
          WHERE w.is_api AND @IncludeApis
          UNION ALL
          SELECT t.template_id, 'template', t.name, t.description, t.schema_version,
            CASE WHEN EXISTS (
              SELECT 1
              FROM jsonb_array_elements(COALESCE(t.graph->'nodes', '[]'::jsonb)) node
              WHERE node->>'type' IN ('start', 'llm', 'knowledgeRetrieve', 'knowledgeAnswer', 'condition', 'merge',
                                      'httpRequest', 'end', 'parameterExtract', 'intentClassify', 'transform')
            ) THEN 'general' ELSE 'media' END AS kind,
            'none', NULL::integer, t.status, t.version,
            CASE WHEN t.status = 'published' THEN 'saved' ELSE 'unpublished' END,
            CASE WHEN jsonb_typeof(t.graph->'nodes') = 'array'
                 THEN jsonb_array_length(t.graph->'nodes') ELSE 0 END,
            t.updated_at, t.deleted_at, r.run_id, r.workflow_id, r.trigger_type, r.status, r.created_at
          FROM workflow_template t
          LEFT JOIN LATERAL (
            SELECT r.run_id, r.workflow_id, r.trigger_type, r.status, r.created_at
            FROM workflow_run r JOIN workflow w ON w.workflow_id = r.workflow_id
            WHERE w.debug_template_id = t.template_id AND r.input_json->'nodeDebug' IS NULL
            ORDER BY r.created_at DESC, r.run_id DESC LIMIT 1
          ) r ON true
        ), filtered AS (
          SELECT * FROM items
          WHERE (@Search IS NULL OR strpos(lower(name), lower(@Search)) > 0
                 OR strpos(lower(description), lower(@Search)) > 0)
            AND (@IncludeDeleted OR (CASE WHEN @View = 'deleted' THEN deleted_at IS NOT NULL ELSE deleted_at IS NULL END))
            AND (@Id IS NULL OR id = @Id)
            AND (@Kind IS NULL OR kind = @Kind)
            AND (@Source IS NULL OR source = @Source)
            AND (@ApiStatus IS NULL OR api_status = @ApiStatus)
            AND (@TemplateStatus IS NULL OR template_status = @TemplateStatus)
            AND (@UpdatedFrom IS NULL OR updated_at >= @UpdatedFrom)
            AND (@UpdatedTo IS NULL OR updated_at <= @UpdatedTo)
            AND (@View IS NULL OR @View = 'deleted' OR (@View = 'api' AND api_status = 'published')
                 OR (@View = 'template' AND template_status = 'published')
                 OR (@View = 'changed' AND draft_status IN ('changed', 'unpublished'))
                 OR (@View = 'failed' AND run_status = 'failed'))
        )
        """;

    /// <summary>计数与当前页使用同一只读快照，空页也返回正确总数，所有筛选均参数化。</summary>
    public async Task<WorkflowCenterPage> ListAsync(WorkflowCenterQuery query, bool includeApis, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // 排序仅使用固定白名单；不把查询参数拼成 SQL 标识符。
        var order = query.Sort switch
        {
            "updated_asc" => "updated_at ASC",
            "name_asc" => "name ASC",
            "name_desc" => "name DESC",
            _ => "updated_at DESC",
        };
        command.CommandText = Projection + " SELECT count(*) FROM filtered; " + Projection
            + " SELECT * FROM filtered ORDER BY " + order + ", source, id OFFSET @Offset LIMIT @Limit;";
        Parameter(command, "IncludeApis", NpgsqlDbType.Boolean, includeApis);
        Parameter(command, "IncludeDeleted", NpgsqlDbType.Boolean, query.IncludeDeleted);
        Parameter(command, "Search", NpgsqlDbType.Text, Normalize(query.Search));
        Parameter(command, "Id", NpgsqlDbType.Uuid, Guid.TryParse(query.Id, out var id) ? id : null);
        Parameter(command, "Kind", NpgsqlDbType.Text, Normalize(query.Kind));
        Parameter(command, "Source", NpgsqlDbType.Text, Normalize(query.Source));
        Parameter(command, "View", NpgsqlDbType.Text, Normalize(query.View));
        Parameter(command, "ApiStatus", NpgsqlDbType.Text, Normalize(query.ApiStatus));
        Parameter(command, "TemplateStatus", NpgsqlDbType.Text, Normalize(query.TemplateStatus));
        Parameter(command, "UpdatedFrom", NpgsqlDbType.TimestampTz, query.UpdatedFrom?.ToUniversalTime());
        Parameter(command, "UpdatedTo", NpgsqlDbType.TimestampTz, query.UpdatedTo?.ToUniversalTime());
        Parameter(command, "Offset", NpgsqlDbType.Integer, query.Offset);
        Parameter(command, "Limit", NpgsqlDbType.Integer, query.Limit);
        var items = new List<WorkflowCenterItem>();
        long total;
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            await reader.ReadAsync(ct);
            total = reader.GetInt64(0);
            await reader.NextResultAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                WorkflowCenterRun? run = reader.IsDBNull(reader.GetOrdinal("run_id")) ? null : new(
                    Value<Guid>(reader, "run_id").ToString(), Value<Guid>(reader, "run_workflow_id").ToString(),
                    Value<string>(reader, "trigger_type"), Value<string>(reader, "run_status"),
                    Value<DateTimeOffset>(reader, "run_created_at"));
                items.Add(new(Value<Guid>(reader, "id").ToString(), Value<string>(reader, "source"),
                    Value<string>(reader, "name"), Value<string>(reader, "description"), Value<int>(reader, "schema_version"),
                    Value<string>(reader, "kind"),
                    Value<string>(reader, "api_status"), OptionalInt(reader, "api_version"),
                    Value<string>(reader, "template_status"), OptionalInt(reader, "template_version"),
                    Value<string>(reader, "draft_status"), Value<int>(reader, "node_count"),
                    Value<DateTimeOffset>(reader, "updated_at"), run) { DeletedAt = reader.IsDBNull(reader.GetOrdinal("deleted_at")) ? null : Value<DateTimeOffset>(reader, "deleted_at") });
            }
        }
        await transaction.CommitAsync(ct);
        return new(items.ToArray(), total, query.Offset, query.Limit);
    }

    /// <summary>空白筛选转换为 SQL 空值，非空文本仍按字面量匹配。</summary>
    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>调试与 API 运行同表分页；模板通过真实 debug_template_id 关联，不按名称推断。</summary>
    public async Task<WorkflowCenterRunPage> ListRunsAsync(string source, Guid id, string? status, int offset, int limit, CancellationToken ct)
    {
        // 总数与当前页共享同一事务快照，单节点调试只返回目标标识而不泄漏输入内容。
        const string from = """
            FROM workflow_run r JOIN workflow w ON w.workflow_id = r.workflow_id
            WHERE ((@Source = 'workflow' AND w.is_api AND w.workflow_id = @Id)
                OR (@Source = 'template' AND w.debug_template_id = @Id))
                AND (@Status IS NULL OR r.status = @Status)
            """;
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT count(*) " + from + "; SELECT r.run_id, r.workflow_id, r.trigger_type, r.status, r.created_at, r.input_json->'nodeDebug'->>'nodeId' "
            + from + " ORDER BY r.created_at DESC, r.run_id DESC OFFSET @Offset LIMIT @Limit;";
        Parameter(command, "Source", NpgsqlDbType.Text, source);
        Parameter(command, "Id", NpgsqlDbType.Uuid, id);
        Parameter(command, "Status", NpgsqlDbType.Text, Normalize(status));
        Parameter(command, "Offset", NpgsqlDbType.Integer, offset);
        Parameter(command, "Limit", NpgsqlDbType.Integer, limit);
        var items = new List<WorkflowCenterRun>();
        long total;
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            await reader.ReadAsync(ct);
            total = reader.GetInt64(0);
            await reader.NextResultAsync(ct);
            while (await reader.ReadAsync(ct)) items.Add(new(reader.GetGuid(0).ToString(), reader.GetGuid(1).ToString(),
                reader.GetString(2), reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4)) { DebugNodeId = reader.IsDBNull(5) ? null : reader.GetString(5) });
        }
        await transaction.CommitAsync(ct);
        return new(items.ToArray(), total, offset, limit);
    }
    /// <summary>来源加 ID 校验历史运行；不允许用模板 ID 猜测其他工作流的执行。</summary>
    public async Task<WorkflowCenterRunScope?> FindRunAsync(string source, Guid id, Guid runId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.workflow_id, r.device_id, r.trigger_type
            FROM workflow_run r JOIN workflow w ON r.workflow_id = w.workflow_id
            WHERE r.run_id = @RunId AND ((@Source = 'workflow' AND w.is_api AND w.workflow_id = @Id)
                OR (@Source = 'template' AND w.debug_template_id = @Id))
            """;
        Parameter(command, "RunId", NpgsqlDbType.Uuid, runId);
        Parameter(command, "Source", NpgsqlDbType.Text, source);
        Parameter(command, "Id", NpgsqlDbType.Uuid, id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2)) : null;
    }

    /// <summary>按具名投影读取必填列，避免依赖联合查询中的字段位置。</summary>
    private static T Value<T>(NpgsqlDataReader reader, string field) => reader.GetFieldValue<T>(reader.GetOrdinal(field));
    /// <summary>读取可空发布版本号，不把未发布伪装为零版本。</summary>
    private static int? OptionalInt(NpgsqlDataReader reader, string field) =>
        reader.IsDBNull(reader.GetOrdinal(field)) ? null : Value<int>(reader, field);
    /// <summary>始终使用带 PostgreSQL 类型的参数传值，空筛选绑定为 DBNull。</summary>
    private static void Parameter(NpgsqlCommand command, string name, NpgsqlDbType type, object? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value ?? DBNull.Value });
}
