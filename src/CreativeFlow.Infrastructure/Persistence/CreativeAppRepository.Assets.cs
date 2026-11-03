using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Features.CreativeApps;

namespace CreativeFlow.Infrastructure.Persistence;

public sealed partial class CreativeAppRepository
{
    /// <summary>在素材保留锁内复核引用，避免检查通过后素材被回收，再发布不可用的版本。</summary>
    private static async Task ValidateReleaseAssetsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CreativeAppRelease release, CancellationToken ct)
    {
        var ids = JsonNode.Parse(release.GraphJson)!["nodes"]!.AsArray().OfType<JsonObject>()
            .Where(n => CreativeAppContract.Text(n["type"]) == "image")
            .Select(n => CreativeAppContract.AssetId(n["data"]?["assetUrl"]))
            .Append(CreativeAppContract.AssetId(release.Metadata["coverAssetId"]))
            .Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToArray();
        foreach (var id in ids)
        {
            await using var command = Command(connection, """
                SELECT count(*) FROM canvas_asset a
                WHERE a.asset_id=@Asset AND a.kind='image' AND a.deleted_at IS NULL
                AND a.purged_at IS NULL AND NOT a.purge_pending
                AND a.device_id IN (SELECT device_id FROM creativeflow_accessible_device_ids(
                    (SELECT device_id FROM workflow WHERE workflow_id=@Workflow)))
                """, transaction);
            Add(command, "Asset", id, NpgsqlDbType.Uuid); Add(command, "Workflow", release.WorkflowId, NpgsqlDbType.Uuid);
            if (Convert.ToInt64(await command.ExecuteScalarAsync(ct)) != 1)
                throw new CreativeAppException("asset_unavailable", "封面或固定参考图已不可用，请重新选择后发布。", 409);
        }
    }
}
