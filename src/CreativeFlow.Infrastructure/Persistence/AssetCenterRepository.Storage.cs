using Npgsql;
using NpgsqlTypes;
using CreativeFlow.Application.Contracts.Ai;

namespace CreativeFlow.Infrastructure.Persistence;

public sealed partial class AssetCenterRepository
{
    /// <summary>容量以字节存储与逻辑引用分别统计；不把数据库可复用页当作磁盘已经释放。</summary>
    public async Task<AssetStorageReport> StorageAsync(CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct); await using var tx = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var policy = await ReadPolicyAsync(connection, tx, ct); await using var command = connection.CreateCommand(); command.Transaction = tx;
        command.CommandText = """
            SELECT COALESCE(sum(byte_size) FILTER(WHERE deleted_at IS NULL),0)::bigint AS active,
              COALESCE(sum(byte_size) FILTER(WHERE deleted_at IS NOT NULL AND purged_at IS NULL),0)::bigint AS recycle,
              COALESCE(sum(byte_size) FILTER(WHERE purged_at IS NOT NULL),0)::bigint AS purged,
              (SELECT COALESCE(sum(byte_size),0)::bigint FROM storage_object WHERE state<>'deleted') AS stored,
              (SELECT COALESCE(sum(byte_size),0)::bigint FROM asset_preview) AS previews,
              COALESCE(sum(byte_size) FILTER(WHERE deleted_at<=now()-@Days*interval '1 day' AND purged_at IS NULL AND NOT is_public
                AND NOT EXISTS(SELECT 1 FROM asset_reference_context r WHERE r.asset_id=a.asset_id AND r.protection<>'expired')),0)::bigint AS purgeable
            FROM canvas_asset a
            """;
        P(command, "Days", NpgsqlDbType.Integer, policy.RetentionDays);
        long active, recycle, purged, stored, previews, purgeable;
        await using (var r = await command.ExecuteReaderAsync(ct)) { await r.ReadAsync(ct); active = r.GetInt64(0); recycle = r.GetInt64(1); purged = r.GetInt64(2); stored = r.GetInt64(3); previews = r.GetInt64(4); purgeable = r.GetInt64(5); }
        var kinds = await BucketsAsync(connection, tx, "a.kind", false, ct);
        var owners = await BucketsAsync(connection, tx, "COALESCE(ac.display_name||' · '||left(ac.account_id::text,8),'设备 '||left(a.device_id::text,8))", true, ct);
        var sources = await BucketsAsync(connection, tx, "a.origin", false, ct);
        await tx.CommitAsync(ct);
        return new(active, recycle, stored, previews, purgeable, purged, kinds, owners, sources, policy,
            policy.WarningBytes > 0 && stored >= policy.WarningBytes || policy.AccountWarningBytes > 0 && owners.Any(x => x.Bytes >= policy.AccountWarningBytes));
    }

    private static async Task<AssetStorageBucket[]> BucketsAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string key, bool owners, CancellationToken ct)
    {
        await using var command = connection.CreateCommand(); command.Transaction = tx;
        command.CommandText = $"SELECT {key},count(*),sum(a.byte_size)::bigint FROM canvas_asset a " + (owners ? "LEFT JOIN creativeflow_account_device ad ON ad.device_id=a.device_id LEFT JOIN creativeflow_account ac ON ac.account_id=ad.account_id " : "") + $"WHERE a.purged_at IS NULL GROUP BY {key} ORDER BY sum(a.byte_size) DESC";
        var items = new List<AssetStorageBucket>(); await using var r = await command.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) items.Add(new(r.GetString(0), r.GetInt64(1), r.GetInt64(2))); return items.ToArray();
    }

    /// <summary>设置告警与回收站期限只改变策略，不在保存设置时触发删除。</summary>
    public async Task SetPolicyAsync(AssetStoragePolicy policy, Guid adminId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct);
        await using var command = connection.CreateCommand(); command.Transaction = tx;
        command.CommandText = "UPDATE asset_storage_policy SET retention_days=@Days,warning_bytes=@Warning,account_warning_bytes=@Account WHERE singleton";
        P(command, "Days", NpgsqlDbType.Integer, policy.RetentionDays); P(command, "Warning", NpgsqlDbType.Bigint, policy.WarningBytes); P(command, "Account", NpgsqlDbType.Bigint, policy.AccountWarningBytes);
        await command.ExecuteNonQueryAsync(ct); await AuditAsync(connection, tx, null, "policy", adminId, $"回收保留 {policy.RetentionDays} 天；容量告警 {policy.WarningBytes} 字节；不自动清理。", ct); await tx.CommitAsync(ct);
    }
}
