using System.Text.Json.Nodes;
using NpgsqlTypes;
using CreativeFlow.Domain.Identity;

namespace CreativeFlow.Infrastructure.Persistence;

public sealed partial class CreativeAppRepository
{
    /// <summary>直接分页读取声明输出及仍可访问的文件，避免客户端逐个获取历史任务或泄露内部节点结果。</summary>
    public async Task<JsonObject> ListOutputsAsync(AccountId account, Guid? workflowId, string? search, string? kind,
        DateTimeOffset? since, int offset, CancellationToken ct)
    {
        const int pageSize = 24;
        await using var connection = await connections.OpenAsync(ct);
        await using var command = Command(connection, """
            SELECT c.run_id,c.workflow_id,v.metadata->>'name',v.version,r.status,
                   d.value->>'key',d.value->>'label',a.asset_id,a.kind,a.media_type,a.byte_size,a.created_at,u.ordinality
            FROM creative_app_run c
            JOIN workflow_run r ON r.run_id=c.run_id
            JOIN creative_app_release v ON v.release_id=c.release_id
            CROSS JOIN LATERAL jsonb_array_elements(v.output_schema) WITH ORDINALITY AS d(value,ordinality)
            JOIN workflow_node_run n ON n.run_id=c.run_id AND n.node_id=d.value->>'nodeId' AND n.status='succeeded'
            CROSS JOIN LATERAL jsonb_array_elements_text(
                CASE WHEN jsonb_typeof(n.output->CASE WHEN d.value->>'type'='video' THEN 'videos' ELSE 'images' END)='array'
                  THEN n.output->CASE WHEN d.value->>'type'='video' THEN 'videos' ELSE 'images' END ELSE '[]'::jsonb END
            ) WITH ORDINALITY AS u(url,ordinality)
            JOIN canvas_asset a ON a.asset_id=CASE
                WHEN u.url ~ '^/api/v1/canvas/assets/[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$'
                THEN substring(u.url from 23)::uuid END AND a.kind=d.value->>'type'
            WHERE c.account_id=@Account AND (@Workflow IS NULL OR c.workflow_id=@Workflow)
              AND a.device_id IN (SELECT device_id FROM creativeflow_accessible_device_ids(r.device_id))
              AND a.deleted_at IS NULL AND a.purged_at IS NULL AND NOT a.purge_pending
              AND (@Kind IS NULL OR a.kind=@Kind) AND (@Since IS NULL OR a.created_at>=@Since)
              AND (@Search='' OR strpos(lower(v.metadata->>'name'),@Search)>0
                   OR strpos(lower(COALESCE(d.value->>'label','')),@Search)>0 OR strpos(c.run_id::text,@Search)>0)
            ORDER BY c.created_at DESC,c.run_id DESC,d.ordinality,u.ordinality
            LIMIT 25 OFFSET @Offset
            """);
        Add(command, "Account", account.Value, NpgsqlDbType.Uuid);
        Add(command, "Workflow", workflowId, NpgsqlDbType.Uuid);
        Add(command, "Search", search?.ToLowerInvariant() ?? "", NpgsqlDbType.Text);
        Add(command, "Kind", kind, NpgsqlDbType.Text);
        Add(command, "Since", since?.ToUniversalTime(), NpgsqlDbType.TimestampTz);
        Add(command, "Offset", offset, NpgsqlDbType.Integer);
        await using var rows = await command.ExecuteReaderAsync(ct);
        var items = new JsonArray(); var hasMore = false;
        while (await rows.ReadAsync(ct))
        {
            if (items.Count == pageSize) { hasMore = true; break; }
            var assetId = rows.GetGuid(7).ToString();
            items.Add((JsonNode)new JsonObject
            {
                ["id"] = $"{rows.GetGuid(0)}:{rows.GetString(5)}:{rows.GetInt64(12)}",
                ["runId"] = rows.GetGuid(0).ToString(), ["appId"] = rows.GetGuid(1).ToString(),
                ["name"] = rows.GetString(2), ["version"] = rows.GetInt32(3), ["runStatus"] = rows.GetString(4),
                ["key"] = rows.GetString(5), ["label"] = rows.IsDBNull(6) ? rows.GetString(5) : rows.GetString(6),
                ["assetId"] = assetId, ["url"] = $"/api/v1/canvas/assets/{assetId}",
                ["kind"] = rows.GetString(8), ["mediaType"] = rows.GetString(9), ["byteSize"] = rows.GetInt64(10),
                ["createdAt"] = rows.GetFieldValue<DateTimeOffset>(11).ToString("O")
            });
        }
        return new JsonObject { ["items"] = items, ["hasMore"] = hasMore, ["offset"] = offset, ["limit"] = pageSize };
    }
}
