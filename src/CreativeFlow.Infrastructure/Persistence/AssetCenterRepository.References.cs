using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;
using CreativeFlow.Application.Contracts.Ai;

namespace CreativeFlow.Infrastructure.Persistence;

public sealed partial class AssetCenterRepository
{
    /// <summary>不截断引用保护集；详情 UI 可以折叠，但服务端必须检查全部引用。</summary>
    private static async Task<AssetReference[]> ReadReferencesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid id, Guid? scope, CancellationToken ct)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT source_kind,source_id,source_slot,reference_name,protection,workflow_id,run_id,node_id FROM asset_reference_context WHERE asset_id=@Id AND protection<>'expired' ORDER BY protection,source_kind,source_id,source_slot";
        P(command, "Id", NpgsqlDbType.Uuid, id);
        var result = new List<AssetReference>();
        await using var r = await command.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) result.Add(new(r.GetString(0), r.GetGuid(1).ToString(), r.GetString(2),
            scope is null ? r.GetString(3) : "已有资源引用", r.GetString(4), scope is null ? V<Guid>(r, "workflow_id")?.ToString() : null,
            scope is null ? V<Guid>(r, "run_id")?.ToString() : null, scope is null ? S(r, "node_id") : null));
        return result.ToArray();
    }

    /// <summary>只从可证实的输出关系还原来源；输入引用不冒充生成来源，上游下载 URL 不交付客户端。</summary>
    private static async Task<AssetOrigin[]> ReadOriginsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, CancellationToken ct)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            SELECT 'image' AS kind,j.job_id AS id,NULL::uuid AS workflow_id,j.node_id,j.request->>'modelKey' AS model,p.name AS provider,j.request::text AS parameters,NULL::uuid AS source_asset
            FROM image_job j JOIN image_job_output o ON o.job_id=j.job_id LEFT JOIN ai_provider p ON p.provider_id=j.provider_id WHERE o.asset_id=@Id
            UNION ALL
            SELECT 'video',j.job_id,NULL::uuid,NULL::text,j.model_key,p.name,j.request::text,NULL::uuid
            FROM video_job j LEFT JOIN ai_provider p ON p.provider_id=j.provider_id WHERE j.asset_id=@Id
            UNION ALL
            SELECT 'canvas',r.request_id,NULL::uuid,r.node_id,r.model_key,p.name,r.input::text,NULL::uuid
            FROM canvas_generation_request r LEFT JOIN ai_provider p ON p.provider_id=r.provider_id
            WHERE EXISTS(SELECT 1 FROM asset_reference ar WHERE ar.asset_id=@Id AND ar.source_kind='canvas_generation_request' AND ar.source_id=r.request_id)
              AND @Id IN (SELECT creativeflow_asset_ids(r.output))
            UNION ALL
            SELECT 'workflow',n.run_id,w.workflow_id,n.node_id,NULL::text,NULL::text,NULL::text,NULL::uuid
            FROM workflow_node_run n JOIN workflow_run w ON w.run_id=n.run_id
            WHERE EXISTS(SELECT 1 FROM asset_reference ar WHERE ar.asset_id=@Id AND ar.source_kind='workflow_node_run' AND ar.source_id=n.run_id AND ar.source_slot=n.node_id)
              AND @Id IN (SELECT creativeflow_asset_ids(n.output))
            UNION ALL
            SELECT 'derived',a.asset_id,NULL::uuid,NULL::text,NULL::text,NULL::text,NULL::text,COALESCE(a.source_asset_id,a.copied_from_asset_id)
            FROM canvas_asset a WHERE a.asset_id=@Id AND COALESCE(a.source_asset_id,a.copied_from_asset_id) IS NOT NULL
            """;
        P(command, "Id", NpgsqlDbType.Uuid, id); var origins = new List<AssetOrigin>();
        await using var r = await command.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            // 白名单保留生成设置；绝不把任意任务 JSON（可能含授权头）原样暴露。
            var safe = new JsonObject();
            if (S(r, "parameters") is { } raw && JsonNode.Parse(raw) is JsonObject input)
                foreach (var key in new[] { "modelKey", "prompt", "size", "quality", "format", "count", "seed", "duration", "seconds", "resolution", "aspectRatio", "watermark", "generateAudio", "inputMode" })
                    if (input[key] is JsonValue value) safe[key] = value.DeepClone();
            origins.Add(new(r.GetString(0), r.GetGuid(1).ToString(), V<Guid>(r, "workflow_id")?.ToString(), S(r, "node_id"), S(r, "model"), S(r, "provider"), safe.Count > 0 ? safe.ToJsonString() : null, V<Guid>(r, "source_asset")?.ToString()));
        }
        return origins.ToArray();
    }

    private static async Task<AssetStoragePolicy> ReadPolicyAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken ct)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT retention_days,warning_bytes,account_warning_bytes FROM asset_storage_policy WHERE singleton";
        await using var r = await command.ExecuteReaderAsync(ct); await r.ReadAsync(ct);
        return new(r.GetInt32(0), r.GetInt64(1), r.GetInt64(2));
    }

    private static AssetDeletionPreview DeletionPreview(AssetItem item, AssetReference[] refs, AssetStoragePolicy policy)
    {
        var eligible = item.DeletedAt?.AddDays(policy.RetentionDays);
        var active = refs.Any(r => r.Protection == "active"); var protectedRef = refs.Any(r => r.Protection == "protected");
        var ready = item.State is "recycled" or "cleaning" && eligible <= DateTimeOffset.UtcNow && !item.IsPublic && !active && !protectedRef;
        var blockers = new List<string>();
        if (item.IsPublic) blockers.Add("请先下架公共素材。");
        if (active) blockers.Add("排队或运行中的任务仍在使用此资产。");
        if (protectedRef) blockers.Add("画布、工作流草稿、发布版本、模板或有效外部上传仍引用此资产。");
        if (refs.Any(r => r.Protection == "history")) blockers.Add("存在历史引用；默认保留原件，清理历史成品需要单独确认。");
        if (item.State == "active") blockers.Add("请先移入回收站。");
        if (eligible > DateTimeOffset.UtcNow) blockers.Add("回收站保留期限尚未届满。");
        return new(item, refs, item.State == "active" && !item.IsPublic && !active, ready && refs.Length == 0, ready, eligible, blockers.ToArray(), policy.RetentionDays);
    }

    public async Task<AssetDeletionPreview?> PreviewAsync(Guid id, Guid? scopeDevice, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var item = await ReadItemAsync(connection, transaction, id, scopeDevice, ct); if (item is null) return null;
        var result = DeletionPreview(item, await ReadReferencesAsync(connection, transaction, id, scopeDevice, ct), await ReadPolicyAsync(connection, transaction, ct));
        await transaction.CommitAsync(ct); return result;
    }
}
