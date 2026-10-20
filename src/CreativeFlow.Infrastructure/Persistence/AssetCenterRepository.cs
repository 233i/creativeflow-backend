using System.Data;
using Npgsql;
using NpgsqlTypes;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Contracts.Ai;

namespace CreativeFlow.Infrastructure.Persistence;

/// <summary>账户范围内的素材选择与管理员资产管理共用查询和事务保护，字节存储保持可替换。</summary>
public sealed partial class AssetCenterRepository(CreativeFlowConnectionFactory connections, IAssetStore store) : IAssetCenterRepository
{
    public async Task<AssetFacets> FacetsAsync(Guid? scopeDevice, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct); await using var command = connection.CreateCommand();
        P(command, "Scope", NpgsqlDbType.Uuid, scopeDevice);
        command.CommandText = "SELECT g.group_id,g.name,count(*) FROM canvas_asset a JOIN canvas_asset_group g ON g.group_id=a.group_id WHERE a.purged_at IS NULL AND " + Scope + " GROUP BY g.group_id,g.name ORDER BY g.name,g.group_id";
        var groups = new List<AssetFacet>(); await using (var r = await command.ExecuteReaderAsync(ct)) while (await r.ReadAsync(ct)) groups.Add(new(r.GetGuid(0).ToString(), r.GetString(1), r.GetInt64(2)));
        command.CommandText = "SELECT ac.account_id,ac.display_name,count(*) FROM canvas_asset a JOIN creativeflow_account_device ad ON ad.device_id=a.device_id JOIN creativeflow_account ac ON ac.account_id=ad.account_id WHERE a.purged_at IS NULL AND " + Scope + " GROUP BY ac.account_id,ac.display_name ORDER BY ac.display_name,ac.account_id";
        var owners = new List<AssetFacet>(); await using (var r = await command.ExecuteReaderAsync(ct)) while (await r.ReadAsync(ct)) owners.Add(new(r.GetGuid(0).ToString(), r.GetString(1), r.GetInt64(2)));
        command.CommandText = "SELECT DISTINCT tag FROM canvas_asset a CROSS JOIN LATERAL unnest(a.tags) tag WHERE a.purged_at IS NULL AND " + Scope + " ORDER BY tag LIMIT 500";
        var tags = new List<string>(); await using (var r = await command.ExecuteReaderAsync(ct)) while (await r.ReadAsync(ct)) tags.Add(r.GetString(0));
        return new(groups.ToArray(), owners.ToArray(), tags.ToArray());
    }

    private const string Projection = """
        SELECT a.asset_id,COALESCE(NULLIF(a.name,''),CASE WHEN a.kind='video' THEN '视频 ' ELSE '图片 ' END||left(a.asset_id::text,8)) AS name,
            a.kind,a.media_type,a.byte_size,a.origin,
            CASE WHEN a.purged_at IS NOT NULL THEN 'purged' WHEN a.purge_pending THEN 'cleaning'
                 WHEN a.deleted_at IS NOT NULL THEN 'recycled' ELSE 'active' END AS state,
            ad.account_id,COALESCE(ac.display_name,'设备 '||left(a.device_id::text,8)) AS owner,
            a.group_id,g.name AS group_name,a.tags,a.created_at,a.deleted_at,a.purged_at,a.is_public,
            (SELECT count(*)::int FROM asset_reference_context r WHERE r.asset_id=a.asset_id AND r.protection<>'expired') AS reference_count,
            a.width,a.height,a.duration,a.management_version::text AS revision,a.device_id
        FROM canvas_asset a
        LEFT JOIN creativeflow_account_device ad ON ad.device_id=a.device_id
        LEFT JOIN creativeflow_account ac ON ac.account_id=ad.account_id
        LEFT JOIN canvas_asset_group g ON g.group_id=a.group_id
        """;

    private const string Scope = "(@Scope IS NULL OR a.device_id IN (SELECT device_id FROM creativeflow_accessible_device_ids(@Scope)))";

    private static void P(NpgsqlCommand command, string name, NpgsqlDbType type, object? value) =>
        command.Parameters.AddWithValue(name, type, value ?? DBNull.Value);

    private static string? S(NpgsqlDataReader r, string name) => r.IsDBNull(r.GetOrdinal(name)) ? null : r.GetString(r.GetOrdinal(name));
    private static T? V<T>(NpgsqlDataReader r, string name) where T : struct => r.IsDBNull(r.GetOrdinal(name)) ? null : r.GetFieldValue<T>(r.GetOrdinal(name));

    /// <summary>公共目录仅交付公开展示字段；用户资产与后台资产分别使用原鉴权内容入口。</summary>
    private static AssetItem Item(NpgsqlDataReader r, Guid? scope, bool publicOnly = false)
    {
        var id = r.GetGuid(r.GetOrdinal("asset_id")).ToString();
        var prefix = publicOnly ? "/api/v1/canvas/asset-library/public/" : scope is null ? "/api/v1/admin/assets/" : "/api/v1/canvas/asset-library/";
        return new(id, S(r, "name")!, S(r, "kind")!, S(r, "media_type")!, r.GetInt64(r.GetOrdinal("byte_size")),
            publicOnly ? "public" : S(r, "origin")!, S(r, "state")!, publicOnly ? null : V<Guid>(r, "account_id")?.ToString(),
            publicOnly ? "公共素材" : S(r, "owner")!, publicOnly ? null : V<Guid>(r, "group_id")?.ToString(), publicOnly ? null : S(r, "group_name"),
            r.GetFieldValue<string[]>(r.GetOrdinal("tags")), r.GetFieldValue<DateTimeOffset>(r.GetOrdinal("created_at")),
            V<DateTimeOffset>(r, "deleted_at"), V<DateTimeOffset>(r, "purged_at"), r.GetBoolean(r.GetOrdinal("is_public")),
            publicOnly ? 0 : r.GetInt32(r.GetOrdinal("reference_count")), V<int>(r, "width"), V<int>(r, "height"), V<double>(r, "duration"),
            S(r, "revision")!, scope is not null && !publicOnly ? $"/api/v1/canvas/assets/{id}" : prefix + id + "/content",
            scope is not null && !publicOnly ? $"/api/v1/canvas/assets/{id}/thumbnail" : prefix + id + "/thumbnail", !publicOnly);
    }

    /// <summary>所有筛选在数据库内执行；列表不读取二进制、完整运行输入或上游下载地址。</summary>
    public async Task<AssetPage> ListAsync(AssetQuery query, Guid? scopeDevice, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        var where = """
            WHERE (@Public AND a.is_public AND a.deleted_at IS NULL AND NOT a.purge_pending AND a.purged_at IS NULL OR NOT @Public AND
            """ + Scope + ")" + """
              AND (@Search IS NULL OR a.name ILIKE @Search OR (NOT @Public AND a.prompt ILIKE @Search)
                   OR a.asset_id::text ILIKE @Search OR (NOT @Public AND ac.display_name ILIKE @Search))
              AND (@Kind IS NULL OR a.kind=@Kind) AND (@Source IS NULL OR a.origin=@Source)
              AND (@Account IS NULL OR ad.account_id=@Account) AND (@Group IS NULL OR a.group_id=@Group)
              AND (NOT @Ungrouped OR a.group_id IS NULL) AND (@Tag IS NULL OR @Tag=ANY(a.tags))
              AND (@From IS NULL OR a.created_at>=@From) AND (@To IS NULL OR a.created_at<=@To)
              AND (@MinBytes IS NULL OR a.byte_size>=@MinBytes)
              AND (@State='all' OR @State='active' AND a.deleted_at IS NULL AND a.purged_at IS NULL
                OR @State='recycled' AND a.deleted_at IS NOT NULL AND a.purged_at IS NULL
                OR @State='purged' AND a.purged_at IS NOT NULL)
              AND (@Usage IS NULL OR @Usage='referenced' AND EXISTS (SELECT 1 FROM asset_reference_context r WHERE r.asset_id=a.asset_id AND r.protection<>'expired')
                OR @Usage='unreferenced' AND NOT EXISTS (SELECT 1 FROM asset_reference_context r WHERE r.asset_id=a.asset_id AND r.protection<>'expired')
                OR @Usage='purgeable' AND a.deleted_at <= now() - (SELECT retention_days*interval '1 day' FROM asset_storage_policy)
                  AND NOT a.is_public AND a.purged_at IS NULL AND NOT EXISTS (SELECT 1 FROM asset_reference_context r WHERE r.asset_id=a.asset_id AND r.protection<>'expired'))
            """;
        var sort = query.Sort switch { "created_asc" => "a.created_at ASC,a.asset_id ASC", "size_desc" => "a.byte_size DESC,a.asset_id DESC", "name_asc" => "a.name ASC,a.asset_id DESC", _ => "a.created_at DESC,a.asset_id DESC" };
        P(command, "Scope", NpgsqlDbType.Uuid, scopeDevice); P(command, "Public", NpgsqlDbType.Boolean, query.Public);
        // 转义通配符，用户输入是搜索文字，不是 SQL LIKE 模式。
        P(command, "Search", NpgsqlDbType.Text, string.IsNullOrWhiteSpace(query.Search) ? null : "%" + query.Search.Trim().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%");
        P(command, "Kind", NpgsqlDbType.Text, Empty(query.Kind)); P(command, "Source", NpgsqlDbType.Text, Empty(query.Source));
        P(command, "Account", NpgsqlDbType.Uuid, query.AccountId); P(command, "Group", NpgsqlDbType.Uuid, query.GroupId);
        P(command, "Ungrouped", NpgsqlDbType.Boolean, query.Ungrouped); P(command, "Tag", NpgsqlDbType.Text, Empty(query.Tag));
        P(command, "From", NpgsqlDbType.TimestampTz, query.From?.ToUniversalTime()); P(command, "To", NpgsqlDbType.TimestampTz, query.To?.ToUniversalTime());
        P(command, "MinBytes", NpgsqlDbType.Bigint, query.MinBytes); P(command, "State", NpgsqlDbType.Text, query.State);
        P(command, "Usage", NpgsqlDbType.Text, Empty(query.Usage)); P(command, "Offset", NpgsqlDbType.Integer, query.Offset); P(command, "Limit", NpgsqlDbType.Integer, query.Limit);
        command.CommandText = $"SELECT count(*) FROM canvas_asset a LEFT JOIN creativeflow_account_device ad ON ad.device_id=a.device_id LEFT JOIN creativeflow_account ac ON ac.account_id=ad.account_id {where}";
        var total = (long)(await command.ExecuteScalarAsync(ct))!;
        command.CommandText = $"{Projection} {where} ORDER BY {sort} OFFSET @Offset LIMIT @Limit";
        var items = new List<AssetItem>();
        await using (var reader = await command.ExecuteReaderAsync(ct)) while (await reader.ReadAsync(ct)) items.Add(Item(reader, scopeDevice, query.Public && scopeDevice is not null));
        await transaction.CommitAsync(ct);
        return new(items.ToArray(), total, query.Offset, query.Limit);
    }

    private static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static async Task<AssetItem?> ReadItemAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid id, Guid? scope, CancellationToken ct)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = Projection + " WHERE a.asset_id=@Id AND " + Scope;
        P(command, "Id", NpgsqlDbType.Uuid, id); P(command, "Scope", NpgsqlDbType.Uuid, scope);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Item(reader, scope) : null;
    }

    /// <summary>只有非清理中的字节能被读取；回收站原件仍须通过原账户或管理员授权。</summary>
    public async Task<AssetContent?> GetContentAsync(Guid id, Guid? scopeDevice, bool publicOnly, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct); await using var command = connection.CreateCommand();
        command.CommandText = "SELECT a.asset_id,a.device_id,COALESCE(NULLIF(a.name,''),a.asset_id::text) AS name,a.media_type,a.storage_path,a.byte_size,CASE WHEN a.deleted_at IS NULL THEN 'active' ELSE 'recycled' END AS state FROM canvas_asset a WHERE a.asset_id=@Id AND a.purged_at IS NULL AND NOT a.purge_pending AND ((@Public AND a.is_public AND a.deleted_at IS NULL) OR (NOT @Public AND " + Scope + "))";
        P(command, "Id", NpgsqlDbType.Uuid, id); P(command, "Scope", NpgsqlDbType.Uuid, scopeDevice); P(command, "Public", NpgsqlDbType.Boolean, publicOnly);
        await using var r = await command.ExecuteReaderAsync(ct); if (!await r.ReadAsync(ct)) return null;
        return new(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt64(5), r.GetString(6));
    }

    /// <summary>详情与引用在同一快照中读取；用户端不返回管理范围之外的使用位置名称。</summary>
    public async Task<AssetDetail?> GetAsync(Guid id, Guid? scopeDevice, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var item = await ReadItemAsync(connection, transaction, id, scopeDevice, ct); if (item is null) return null;
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT prompt,sha256 FROM canvas_asset WHERE asset_id=@Id"; P(command, "Id", NpgsqlDbType.Uuid, id);
        string? prompt, hash;
        await using (var reader = await command.ExecuteReaderAsync(ct)) { await reader.ReadAsync(ct); prompt = S(reader, "prompt"); hash = S(reader, "sha256"); }
        var refs = await ReadReferencesAsync(connection, transaction, id, scopeDevice, ct);
        var origins = await ReadOriginsAsync(connection, transaction, id, ct);
        var audit = new List<AssetAudit>();
        command.CommandText = "SELECT action,actor_id::text,reason,created_at FROM asset_management_audit WHERE asset_id=@Id ORDER BY id DESC LIMIT 100";
        await using (var r = await command.ExecuteReaderAsync(ct)) while (await r.ReadAsync(ct)) audit.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetFieldValue<DateTimeOffset>(3)));
        await transaction.CommitAsync(ct);
        return new(item, prompt, hash, refs, origins, audit.ToArray(), true);
    }
}
