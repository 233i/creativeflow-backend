using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Features.CreativeApps;
using CreativeFlow.Application.Features.Workflow;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;

namespace CreativeFlow.Infrastructure.Persistence;

public sealed partial class CreativeAppRepository
{
    /// <summary>幂等、下架检查、额度和客户素材副本均在提交事务内完成，失败时不会留下半个任务。</summary>
    public async Task<CreativeAppAdmission> EnqueueAsync(CreativeAppRelease release, AccountId account, DeviceId device,
        string key, string hash, string graphJson, JsonObject inputs, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var command = Command(connection, """
            SELECT pg_advisory_xact_lock_shared(721909190);
            SELECT creativeflow_lock_live_workflow(@Id);
            SELECT pg_advisory_xact_lock(hashtextextended('creative-admission:'||CAST(@Account AS text),0));
            SELECT workflow_id FROM creative_app WHERE workflow_id=@Id FOR UPDATE;
            """, transaction);
        Add(command, "Account", account.Value, NpgsqlDbType.Uuid); Add(command, "Id", release.WorkflowId, NpgsqlDbType.Uuid);
        await command.ExecuteNonQueryAsync(ct);
        command.CommandText = "SELECT run_id,request_hash FROM creative_app_run WHERE workflow_id=@Id AND account_id=@Account AND idempotency_key=@Key";
        Add(command, "Key", key, NpgsqlDbType.Text);
        await using (var rows = await command.ExecuteReaderAsync(ct))
            if (await rows.ReadAsync(ct)) return new(rows.GetString(1) == hash ? "replayed" : "idempotency_conflict", new WorkflowRunId(rows.GetGuid(0)));
        command.CommandText = "SELECT active_release_id FROM creative_app WHERE workflow_id=@Id";
        if (await command.ExecuteScalarAsync(ct) is not Guid active || active != release.Id) return new("version_changed", null);
        command.CommandText = """
            SELECT
              (SELECT count(*) FROM creative_app_run c JOIN workflow_run r ON r.run_id=c.run_id WHERE c.workflow_id=@Id AND r.status='queued'),
              (SELECT count(*) FROM creative_app_run c JOIN workflow_run r ON r.run_id=c.run_id WHERE c.account_id=@Account AND r.status IN ('queued','running')),
              (SELECT count(*) FROM creative_app_run WHERE account_id=@Account AND workflow_id=@Id AND created_at>=date_trunc('day',now() AT TIME ZONE 'UTC') AT TIME ZONE 'UTC')
            """;
        await using (var rows = await command.ExecuteReaderAsync(ct))
        {
            await rows.ReadAsync(ct);
            if (rows.GetInt64(0) >= 100 || rows.GetInt64(1) >= 20) return new("queue_full", null);
            if (rows.GetInt64(2) >= release.DailyLimit) return new("daily_limit", null);
        }
        // A published fixed reference is an explicit grant for this application only. Create a
        // customer-owned metadata alias of its immutable file, never a grant to the publisher account.
        var graph = JsonNode.Parse(graphJson)!.AsObject();
        var publishedNodes = JsonNode.Parse(release.GraphJson)!["nodes"]!.AsArray().OfType<JsonObject>()
            .ToDictionary(n => CreativeAppContract.Text(n["id"]), StringComparer.Ordinal);
        foreach (var node in graph["nodes"]!.AsArray().OfType<JsonObject>().Where(n => CreativeAppContract.Text(n["type"]) == "image"))
        {
            var original = publishedNodes[CreativeAppContract.Text(node["id"])]["data"]?["assetUrl"];
            if (CreativeAppContract.AssetId(original) is not { } source || !JsonNode.DeepEquals(original, node["data"]?["assetUrl"])) continue;
            if (!CreativeAppContract.Flag(release.Metadata["shareFixedAssets"])) throw new CreativeAppException("fixed_asset_denied", "此应用没有授权共享固定参考图。");
            var alias = Guid.CreateVersion7();
            await using var copy = Command(connection, """
                INSERT INTO canvas_asset(asset_id,device_id,kind,media_type,storage_path,byte_size,prompt,created_at,name,origin,sha256)
                SELECT @Alias,@Device,kind,media_type,storage_path,byte_size,NULL,now(),'公共创作参考图','reused',sha256 FROM canvas_asset
                WHERE asset_id=@Source AND kind='image' AND deleted_at IS NULL AND purged_at IS NULL AND NOT purge_pending
                """, transaction);
            Add(copy, "Alias", alias, NpgsqlDbType.Uuid); Add(copy, "Device", device.Value, NpgsqlDbType.Uuid); Add(copy, "Source", source, NpgsqlDbType.Uuid);
            if (await copy.ExecuteNonQueryAsync(ct) != 1) throw new CreativeAppException("asset_unavailable", "公共创作的固定参考图已不可用。", 409);
            node["data"]!["assetUrl"] = $"/api/v1/canvas/assets/{alias}";
        }
        foreach (var node in graph["nodes"]!.AsArray().OfType<JsonObject>().Where(n => CreativeAppContract.Text(n["type"]) == "image"))
        {
            if (CreativeAppContract.AssetId(node["data"]?["assetUrl"]) is not { } image) continue;
            await using var check = Command(connection, "SELECT count(*) FROM canvas_asset WHERE asset_id=@Asset AND device_id IN (SELECT device_id FROM creativeflow_accessible_device_ids(@Device)) AND kind='image' AND deleted_at IS NULL AND purged_at IS NULL AND NOT purge_pending", transaction);
            Add(check, "Asset", image, NpgsqlDbType.Uuid); Add(check, "Device", device.Value, NpgsqlDbType.Uuid);
            if (Convert.ToInt64(await check.ExecuteScalarAsync(ct)) != 1) throw new CreativeAppException("asset_denied", "参考图在提交期间已不可用，请重新选择。", 403);
        }
        var validation = WorkflowGraphValidator.Validate(graph);
        if (!validation.IsValid || validation.Plan is null) throw new CreativeAppException("invalid_inputs", string.Join("；", validation.Errors));
        var id = WorkflowRunId.New();
        command.CommandText = """
            INSERT INTO workflow_run(run_id,workflow_id,device_id,graph_snapshot,status,created_at,updated_at,trigger_type,input_json)
            VALUES(@Run,@Id,@Device,@Graph,'queued',now(),now(),'public_creative',@Inputs);
            INSERT INTO creative_app_run(run_id,release_id,account_id,workflow_id,idempotency_key,request_hash)
            VALUES(@Run,@Release,@Account,@Id,@Key,@Hash);
            """;
        Add(command, "Run", id.Value, NpgsqlDbType.Uuid); Add(command, "Device", device.Value, NpgsqlDbType.Uuid); Add(command, "Release", release.Id, NpgsqlDbType.Uuid);
        Add(command, "Hash", hash, NpgsqlDbType.Text); Add(command, "Graph", graph.ToJsonString(), NpgsqlDbType.Jsonb); Add(command, "Inputs", inputs.ToJsonString(), NpgsqlDbType.Jsonb);
        await command.ExecuteNonQueryAsync(ct);
        foreach (var node in validation.Plan.Nodes)
        {
            await using var insert = Command(connection, "INSERT INTO workflow_node_run(run_id,node_id,node_type,status,is_reused) VALUES(@Run,@Node,@Type,'queued',false)", transaction);
            Add(insert, "Run", id.Value, NpgsqlDbType.Uuid); Add(insert, "Node", node.Id, NpgsqlDbType.Text); Add(insert, "Type", node.Type, NpgsqlDbType.Text);
            await insert.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct); return new("accepted", id);
    }
}
