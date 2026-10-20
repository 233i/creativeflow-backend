using Npgsql;
using NpgsqlTypes;
using CreativeFlow.Application.Contracts.Ai;

namespace CreativeFlow.Infrastructure.Persistence;

public sealed partial class AssetCenterRepository
{
    /// <summary>管理员上传使用专用资源设备，不要求同时登录普通用户，也不借用其他用户的素材归属。</summary>
    public async Task<Guid> GetAdminDeviceAsync(Guid adminId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct);
        await using var command = connection.CreateCommand(); command.Transaction = tx;
        P(command, "Admin", NpgsqlDbType.Uuid, adminId);
        // ExecuteScalar 避免 SELECT void 的第一个结果；锁和读取分开执行。
        command.CommandText = "SELECT pg_advisory_xact_lock(hashtextextended(@Admin::text,721909191))"; await command.ExecuteNonQueryAsync(ct);
        command.CommandText = "SELECT device_id FROM asset_admin_owner WHERE admin_id=@Admin";
        if (await command.ExecuteScalarAsync(ct) is Guid existing) { await tx.CommitAsync(ct); return existing; }
        var id = Guid.CreateVersion7(); P(command, "Device", NpgsqlDbType.Uuid, id);
        command.CommandText = """
            INSERT INTO device(device_id,fp_exact,fp_coarse,ip_prefix,signals,sig_version,user_agent)
            VALUES(@Device,decode(replace(@Device::text,'-',''),'hex'),decode(replace(@Device::text,'-',''),'hex'),'127.0.0.1','{"source":"asset_center"}',1,'asset-center');
            INSERT INTO asset_admin_owner(admin_id,device_id) VALUES(@Admin,@Device)
            """;
        await command.ExecuteNonQueryAsync(ct); await tx.CommitAsync(ct); return id;
    }

    /// <summary>所有破坏性操作先锁引用域再锁资产；图与队列的 BEFORE STATEMENT 触发器遵循相同顺序。</summary>
    private static async Task LockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, CancellationToken ct)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT pg_advisory_xact_lock(721909190); SELECT asset_id FROM canvas_asset WHERE asset_id=@Id FOR UPDATE";
        P(command, "Id", NpgsqlDbType.Uuid, id); await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task AuditAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid? id, string action, Guid actor, string reason, CancellationToken ct)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO asset_management_audit(asset_id,action,actor_id,reason) VALUES(@Id,@Action,@Actor,@Reason)";
        P(command, "Id", NpgsqlDbType.Uuid, id); P(command, "Action", NpgsqlDbType.Text, action); P(command, "Actor", NpgsqlDbType.Uuid, actor); P(command, "Reason", NpgsqlDbType.Text, reason);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static AssetActionResult Result(Guid id, string status, string message, long bytes = 0) => new(id.ToString(), status, message, bytes);

    public async Task<AssetActionResult> UpdateAsync(Guid id, UpdateAssetRequest request, Guid actor, Guid? scopeDevice, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct);
        await LockAsync(connection, tx, id, ct);
        var item = await ReadItemAsync(connection, tx, id, scopeDevice, ct); if (item is null) return Result(id, "not_found", "资产不存在或无权管理。");
        if (item.State != "active") return Result(id, "blocked", "请先恢复回收站中的资产。");
        if (item.Revision != request.Revision) return Result(id, "changed", "资产已变化，请重新查询。");
        await using var command = connection.CreateCommand(); command.Transaction = tx;
        command.CommandText = "UPDATE canvas_asset SET name=@Name,tags=@Tags,management_version=management_version+1,updated_at=now() WHERE asset_id=@Id";
        P(command, "Id", NpgsqlDbType.Uuid, id); P(command, "Name", NpgsqlDbType.Text, request.Name.Trim()); P(command, "Tags", NpgsqlDbType.Array | NpgsqlDbType.Text, request.Tags.Select(x => x.Trim()).Distinct(StringComparer.Ordinal).ToArray());
        await command.ExecuteNonQueryAsync(ct); await AuditAsync(connection, tx, id, "update", actor, request.Reason, ct); await tx.CommitAsync(ct);
        return Result(id, "updated", "资产信息已保存。");
    }

    /// <summary>回收只隐藏选择入口；永久清理先持久化清理意图，失败/中断后可安全重试，不调用生成模型。</summary>
    public async Task<AssetActionResult> ActAsync(Guid id, string action, AssetActionRequest request, Guid actor, Guid? scopeDevice, CancellationToken ct)
    {
        string? path = null; string? previewPath = null; long bytes = 0; bool shared = false;
        await using (var connection = await connections.OpenAsync(ct))
        await using (var tx = await connection.BeginTransactionAsync(ct))
        {
            await LockAsync(connection, tx, id, ct);
            var item = await ReadItemAsync(connection, tx, id, scopeDevice, ct); if (item is null) return Result(id, "not_found", "资产不存在或无权管理。");
            if (action == "recycle" && item.State == "recycled" || action == "restore" && item.State == "active" || action == "purge" && item.State == "purged")
                return Result(id, "unchanged", "操作已完成，无需重复提交。");
            if (item.Revision != request.Revision) return Result(id, "changed", "资产已被修改，请重新查询影响。");
            var refs = await ReadReferencesAsync(connection, tx, id, scopeDevice, ct);
            var preview = DeletionPreview(item, refs, await ReadPolicyAsync(connection, tx, ct));
            var allowed = action switch
            {
                "recycle" => preview.CanRecycle,
                "restore" => item.State == "recycled",
                "publish" => scopeDevice is null && item.State == "active" && !item.IsPublic && request.ConfirmName == item.Name,
                "unpublish" => scopeDevice is null && item.IsPublic,
                "purge" => request.ConfirmName == item.Name && (request.AllowHistory && scopeDevice is null ? preview.CanPurgeHistory : preview.CanPurge),
                _ => false,
            };
            if (!allowed) return Result(id, "blocked", action == "purge" ? "清理条件不满足：请核对名称、保留期限及全部引用。" : "当前状态或引用不允许此操作。");
            await using var command = connection.CreateCommand(); command.Transaction = tx;
            command.CommandText = action switch
            {
                "recycle" => "UPDATE canvas_asset SET deleted_at=now(),deleted_by=@Actor,management_version=management_version+1,updated_at=now() WHERE asset_id=@Id",
                "restore" => "UPDATE canvas_asset SET deleted_at=NULL,deleted_by=NULL,cleanup_error=NULL,management_version=management_version+1,updated_at=now() WHERE asset_id=@Id",
                "publish" => "UPDATE canvas_asset SET is_public=true,management_version=management_version+1,updated_at=now() WHERE asset_id=@Id",
                "unpublish" => "UPDATE canvas_asset SET is_public=false,management_version=management_version+1,updated_at=now() WHERE asset_id=@Id",
                _ => "UPDATE canvas_asset SET purge_pending=true,cleanup_error=NULL,management_version=management_version+1,updated_at=now() WHERE asset_id=@Id",
            };
            P(command, "Id", NpgsqlDbType.Uuid, id); P(command, "Actor", NpgsqlDbType.Uuid, actor); await command.ExecuteNonQueryAsync(ct);
            await AuditAsync(connection, tx, id, action == "purge" ? (request.AllowHistory ? "purge-history-requested" : "purge-requested") : action, actor, request.Reason, ct);
            if (action == "purge")
            {
                command.CommandText = "SELECT storage_path,(SELECT storage_path FROM asset_preview WHERE asset_id=@Id) AS preview FROM canvas_asset WHERE asset_id=@Id";
                await using (var r = await command.ExecuteReaderAsync(ct)) { await r.ReadAsync(ct); path = r.GetString(0); previewPath = S(r, "preview"); }
                // 派生缩略图不是独立逻辑资产；先删其元数据，存储适配器再安全回收字节。
                command.CommandText = "DELETE FROM asset_preview WHERE asset_id=@Id"; await command.ExecuteNonQueryAsync(ct);
                command.CommandText = """
                    INSERT INTO asset_storage_cleanup(storage_path) SELECT storage_path FROM canvas_asset WHERE asset_id=@Id ON CONFLICT DO NOTHING;
                    SELECT EXISTS(SELECT 1 FROM canvas_asset WHERE storage_path=(SELECT storage_path FROM canvas_asset WHERE asset_id=@Id)
                        AND asset_id<>@Id AND purged_at IS NULL AND NOT purge_pending)
                    """;
                shared = (bool)(await command.ExecuteScalarAsync(ct))!;
                if (previewPath is not null)
                {
                    command.CommandText = "INSERT INTO asset_storage_cleanup(storage_path) VALUES(@Path) ON CONFLICT DO NOTHING";
                    P(command, "Path", NpgsqlDbType.Text, previewPath); await command.ExecuteNonQueryAsync(ct);
                }
                bytes = item.ByteSize;
            }
            await tx.CommitAsync(ct);
        }
        if (path is null) return Result(id, action switch { "publish" => "published", "unpublish" => "unpublished", _ => action + "d" }, "操作已完成。");
        try
        {
            // 存储适配器也检查共享键；同一原件的其他逻辑资产不能被连带删除。
            if (!shared) await store.DeleteAsync(path, ct); if (previewPath is not null) await store.DeleteAsync(previewPath, ct);
            await using var connection = await connections.OpenAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct);
            await LockAsync(connection, tx, id, ct);
            await using var command = connection.CreateCommand(); command.Transaction = tx;
            command.CommandText = "UPDATE canvas_asset SET purged_at=now(),purge_pending=false,cleanup_error=NULL,management_version=management_version+1,updated_at=now() WHERE asset_id=@Id AND purged_at IS NULL AND purge_pending";
            P(command, "Id", NpgsqlDbType.Uuid, id); var changed = await command.ExecuteNonQueryAsync(ct);
            if (changed > 0) await AuditAsync(connection, tx, id, "purged", actor, "原件已释放；共享或写入中的字节按存储保护规则保留。", ct);
            await tx.CommitAsync(ct); return Result(id, "purged", "已清理逻辑资产，保留审计；共享内容不会重复删除。", bytes);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            // 不把原始存储异常（可能含路径/连接信息）返回给客户端。
            await using var connection = await connections.OpenAsync(CancellationToken.None); await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE canvas_asset SET cleanup_error='存储清理失败，可重新查询后重试。' WHERE asset_id=@Id AND purged_at IS NULL";
            P(command, "Id", NpgsqlDbType.Uuid, id); await command.ExecuteNonQueryAsync(CancellationToken.None);
            return Result(id, "cleanup_failed", "存储清理未完成，请重新查询后重试；不会重新生成。");
        }
    }

    /// <summary>批量操作逐条提交并返回结果；默认不允许批量放弃历史成品，客户端必须确认每项版本。</summary>
    public async Task<AssetActionResult[]> BatchAsync(AssetBatchRequest request, Guid actor, Guid? scopeDevice, CancellationToken ct)
    {
        var results = new List<AssetActionResult>();
        foreach (var text in request.Ids.Distinct(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested(); var id = Guid.Parse(text);
            if (request.Action is "recycle" or "restore" or "purge")
            {
                var preview = await PreviewAsync(id, scopeDevice, ct);
                if (preview is null) { results.Add(Result(id, "not_found", "资产不存在或无权管理。")); continue; }
                var revision = request.Revisions?.GetValueOrDefault(text);
                if (revision != preview.Asset.Revision) { results.Add(Result(id, "changed", "资产已变化，请重新选择确认。")); continue; }
                results.Add(await ActAsync(id, request.Action, new(revision, request.Reason, preview.Asset.Name), actor, scopeDevice, ct));
                continue;
            }
            await using var connection = await connections.OpenAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct);
            await LockAsync(connection, tx, id, ct); var item = await ReadItemAsync(connection, tx, id, scopeDevice, ct);
            if (item is null) { results.Add(Result(id, "not_found", "资产不存在或无权管理。")); continue; }
            if (item.State != "active") { results.Add(Result(id, "blocked", "只能整理有效资产。")); continue; }
            await using var command = connection.CreateCommand(); command.Transaction = tx; P(command, "Id", NpgsqlDbType.Uuid, id);
            if (request.Action == "tags")
            {
                var tags = item.Tags.Concat(request.Tags ?? []).Distinct(StringComparer.Ordinal).ToArray();
                if (tags.Length > 20) { results.Add(Result(id, "blocked", "每个资产最多 20 个标签。")); continue; }
                command.CommandText = "UPDATE canvas_asset SET tags=@Tags,management_version=management_version+1,updated_at=now() WHERE asset_id=@Id"; P(command, "Tags", NpgsqlDbType.Array | NpgsqlDbType.Text, tags);
            }
            else if (request.Action == "group")
            {
                P(command, "Name", NpgsqlDbType.Text, Empty(request.GroupName));
                command.CommandText = """
                    INSERT INTO canvas_asset_group(group_id,device_id,name,created_at,updated_at)
                    SELECT gen_random_uuid(),device_id,@Name,now(),now() FROM canvas_asset WHERE asset_id=@Id AND @Name IS NOT NULL
                    ON CONFLICT (device_id,lower(name)) DO NOTHING;
                    UPDATE canvas_asset a SET group_id=(SELECT g.group_id FROM canvas_asset_group g
                      WHERE g.device_id=a.device_id AND lower(g.name)=lower(@Name)),management_version=management_version+1,updated_at=now() WHERE a.asset_id=@Id;
                    """;
            }
            else { results.Add(Result(id, "blocked", "不支持此批量操作。")); continue; }
            await command.ExecuteNonQueryAsync(ct); await AuditAsync(connection, tx, id, request.Action, actor, request.Reason, ct); await tx.CommitAsync(ct);
            results.Add(Result(id, "updated", "已整理。"));
        }
        return results.ToArray();
    }

    /// <summary>公开资产只复制名称/标签和字节引用，不复制原用户提示词、参数或原账号访问权。</summary>
    public async Task<AssetItem?> ReuseAsync(Guid id, string revision, Guid deviceId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct);
        await LockAsync(connection, tx, id, ct); var copy = Guid.CreateVersion7();
        await using var command = connection.CreateCommand(); command.Transaction = tx;
        command.CommandText = """
            INSERT INTO canvas_asset(asset_id,device_id,kind,media_type,storage_path,byte_size,created_at,name,tags,origin,
                copied_from_asset_id,sha256,width,height,duration)
            SELECT @Copy,@Device,kind,media_type,storage_path,byte_size,now(),name,tags,'reused',asset_id,sha256,width,height,duration
            FROM canvas_asset WHERE asset_id=@Id AND is_public AND deleted_at IS NULL AND NOT purge_pending AND purged_at IS NULL AND management_version::text=@Revision
            """;
        P(command, "Id", NpgsqlDbType.Uuid, id); P(command, "Copy", NpgsqlDbType.Uuid, copy); P(command, "Device", NpgsqlDbType.Uuid, deviceId); P(command, "Revision", NpgsqlDbType.Text, revision);
        if (await command.ExecuteNonQueryAsync(ct) == 0) return null;
        await AuditAsync(connection, tx, copy, "reuse", deviceId, "从公共目录复用；未复制原用户私有元数据。", ct);
        var item = await ReadItemAsync(connection, tx, copy, deviceId, ct); await tx.CommitAsync(ct); return item;
    }
}
