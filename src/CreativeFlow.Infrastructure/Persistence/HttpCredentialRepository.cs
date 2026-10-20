using System.Text.Json.Nodes;
using Dapper;
using Npgsql;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Infrastructure.Persistence;

/// <summary>HTTP 凭据以固定版本追加，状态/授权即时读取；密文仅从内部版本端口返回。</summary>
public sealed class HttpCredentialRepository(NpgsqlDataSource source) : IHttpCredentialRepository
{
    /// <summary>旧查询所在源禁止匿名访问，避免路径别名绕过；实际凭据仍只许可固定方法、路径和契约。</summary>
    public async Task<bool> IsRestrictedEndpointAsync(string endpoint, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        return await connection.ExecuteScalarAsync<bool>("""
            SELECT EXISTS(SELECT 1 FROM workflow_http_credential_revision r
              WHERE r.definition_json ? 'requestDigest' AND r.definition_json->>'origin'=@Origin)
            """, new { Origin = new Uri(endpoint).GetLeftPart(UriPartial.Authority) });
    }
    /// <summary>列出安全元数据，不把连接定义或鉴权头泄露给节点目录。</summary>
    public async Task<JsonArray> ListAsync(CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<OperationRow>("SELECT id, name, description, enabled, current_revision, created_at FROM workflow_http_credential ORDER BY created_at DESC LIMIT 100");
        return new JsonArray(rows.Select(row => (JsonNode)new JsonObject { ["id"] = row.Id.ToString(), ["name"] = row.Name, ["description"] = row.Description,
            ["enabled"] = row.Enabled, ["currentRevision"] = row.CurrentRevision?.ToString(), ["createdAt"] = Utc(row.CreatedAt) }).ToArray());
    }

    /// <summary>管理员读取各版本配置与是否已保存凭据，不提供凭据恢复接口。</summary>
    public async Task<JsonObject?> DetailsAsync(Guid id, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<OperationRow>("SELECT id, name, description, enabled, current_revision, created_at FROM workflow_http_credential WHERE id = @Id", new { Id = id });
        if (row is null) return null;
        var revisions = await connection.QueryAsync<RevisionSummary>("SELECT id, definition_json::text AS definition, digest, secret_hint, created_at FROM workflow_http_credential_revision WHERE credential_id = @Id ORDER BY created_at DESC LIMIT 100", new { Id = id });
        return new JsonObject { ["id"] = row.Id.ToString(), ["name"] = row.Name, ["description"] = row.Description, ["enabled"] = row.Enabled, ["currentRevision"] = row.CurrentRevision?.ToString(),
            ["revisions"] = new JsonArray(revisions.Select(revision => (JsonNode)new JsonObject { ["id"] = revision.Id.ToString(), ["definition"] = JsonNode.Parse(revision.Definition),
                ["digest"] = revision.Digest, ["hasSecret"] = revision.SecretHint is not null, ["secretHint"] = revision.SecretHint, ["createdAt"] = Utc(revision.CreatedAt) }).ToArray()) };
    }

    /// <summary>按凭据和版本双条件定位，不能跨凭据使用已知版本 ID。</summary>
    public async Task<HttpCredentialRevision?> FindRevisionAsync(Guid operation, Guid revision, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<RevisionRow>("""
            SELECT id, credential_id, definition_json::text AS definition, digest, secret_cipher, secret_nonce, secret_tag, secret_hint
            FROM workflow_http_credential_revision WHERE credential_id = @Operation AND id = @Revision
            """, new { Operation = operation, Revision = revision });
        return row is null ? null : new(row.CredentialId, row.Id, row.Definition, row.Digest,
            row.SecretCipher is null ? null : new ApiKeySecret(row.SecretCipher, row.SecretNonce!, row.SecretTag!, row.SecretHint!));
    }

    /// <summary>锁定凭据并追加配置版本，再原子切换编辑器默认版本；运行/发布仍引用旧版本。</summary>
    public async Task<Guid> SaveAsync(Guid operation, string name, string description, string definition, string digest, ApiKeySecret? secret, Guid admin, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var id = Guid.CreateVersion7();
        // 统一锁只序列化后台配置写入；不占用工作流执行队列或远端 HTTP 的并发槽。
        await connection.ExecuteAsync("SELECT pg_advisory_xact_lock(652935)", transaction: transaction);
        if (!await connection.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM creativeflow_admin WHERE admin_id = @Admin)", new { Admin = admin }, transaction)) throw Denied();
        var exists = await connection.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM workflow_http_credential WHERE id = @Id)", new { Id = operation }, transaction);
        if (!exists && await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM workflow_http_credential", transaction: transaction) >= 100) throw new WorkflowHttpException("HTTP_CONFIG_QUOTA", "最多配置 100 个外部凭据。");
        if (await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM workflow_http_credential_revision WHERE credential_id = @Id", new { Id = operation }, transaction) >= 100) throw new WorkflowHttpException("HTTP_CONFIG_QUOTA", "单凭据最多保留 100 个配置版本；当前不会自动删除被引用版本。");
        await connection.ExecuteAsync("""
            INSERT INTO workflow_http_credential(id, name, description) VALUES (@Operation, @Name, @Description)
            ON CONFLICT(id) DO UPDATE SET name = EXCLUDED.name, description = EXCLUDED.description, updated_at = clock_timestamp();
            INSERT INTO workflow_http_credential_revision(id, credential_id, definition_json, digest, secret_cipher, secret_nonce, secret_tag, secret_hint, created_by)
            VALUES (@Id, @Operation, @Definition::jsonb, @Digest, @Cipher, @Nonce, @Tag, @Hint, @Admin);
            UPDATE workflow_http_credential SET current_revision = @Id WHERE id = @Operation;
            """, new { Id = id, Operation = operation, Name = name, Description = description, Definition = definition, Digest = digest,
                Cipher = secret?.Cipher, Nonce = secret?.Nonce, Tag = secret?.Tag, Hint = secret?.Hint, Admin = admin }, transaction);
        await transaction.CommitAsync(ct);
        return id;
    }

    /// <summary>修改可用性而不销毁版本，恢复后既有显式授权也重新生效。</summary>
    public async Task<bool> SetEnabledAsync(Guid operation, bool enabled, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        return await connection.ExecuteAsync("UPDATE workflow_http_credential SET enabled = @Enabled, updated_at = clock_timestamp() WHERE id = @Operation", new { Operation = operation, Enabled = enabled }) == 1;
    }

    /// <summary>不同主体走独立 SQL 条件；Key 拥有工作流调用权不隐含 HTTP 凭据许可。</summary>
    public async Task<bool> CanUseAsync(Guid operation, KnowledgeSubject subject, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        return await connection.ExecuteScalarAsync<bool>("""
            SELECT EXISTS (SELECT 1 FROM workflow_http_credential o WHERE o.id = @Operation AND o.enabled AND (
              (@Kind = 'admin' AND EXISTS (SELECT 1 FROM creativeflow_admin WHERE admin_id = @Id)) OR
              (EXISTS (SELECT 1 FROM workflow_http_credential_grant g WHERE g.credential_id = o.id AND g.subject_kind = @Kind AND g.subject_id = @Id AND g.enabled) AND (
                (@Kind = 'account' AND EXISTS (SELECT 1 FROM creativeflow_account a JOIN creativeflow_account_device d ON d.account_id = a.account_id
                  WHERE a.account_id = @Id AND a.is_enabled AND d.device_id = @Device)) OR
                (@Kind = 'apiKey' AND EXISTS (SELECT 1 FROM workflow_api_key k JOIN workflow w ON w.workflow_id = k.workflow_id AND w.device_id = @Device
                  WHERE k.api_key_id = @Id AND k.enabled AND k.revoked_at IS NULL
                  AND (k.expires_at IS NULL OR k.expires_at > clock_timestamp())))))))
            """, new { Operation = operation, subject.Kind, subject.Id, subject.Device });
    }

    /// <summary>展示资源许可及最近审计，主体名称只用于识别，不返回任何认证字段。</summary>
    public async Task<JsonObject> GrantsAsync(Guid operation, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<GrantRow>("""
            SELECT g.subject_kind, g.subject_id, g.enabled, COALESCE(a.display_name,k.name,'已删除主体') AS name
            FROM workflow_http_credential_grant g LEFT JOIN creativeflow_account a ON g.subject_kind = 'account' AND a.account_id = g.subject_id
            LEFT JOIN workflow_api_key k ON g.subject_kind = 'apiKey' AND k.api_key_id = g.subject_id
            WHERE g.credential_id = @Operation ORDER BY g.updated_at DESC LIMIT 100
            """, new { Operation = operation });
        var audit = await connection.QueryAsync<AuditRow>("SELECT subject_kind, subject_id, enabled, reason, created_at FROM workflow_http_credential_grant_audit WHERE credential_id = @Operation ORDER BY created_at DESC LIMIT 100", new { Operation = operation });
        return new JsonObject { ["grants"] = new JsonArray(rows.Select(row => (JsonNode)new JsonObject { ["kind"] = row.SubjectKind, ["id"] = row.SubjectId.ToString(), ["enabled"] = row.Enabled, ["name"] = row.Name }).ToArray()),
            ["audit"] = new JsonArray(audit.Select(row => (JsonNode)new JsonObject { ["kind"] = row.SubjectKind, ["id"] = row.SubjectId.ToString(), ["enabled"] = row.Enabled, ["reason"] = row.Reason, ["createdAt"] = Utc(row.CreatedAt) }).ToArray()) };
    }

    /// <summary>检查真实主体存在并锁定凭据，幂等调整状态与原因审计在同一事务提交。</summary>
    public async Task SetGrantAsync(Guid operation, string kind, Guid subject, Guid admin, bool enabled, string reason, CancellationToken ct)
    {
        if (kind is not ("account" or "apiKey") || string.IsNullOrWhiteSpace(reason) || reason.Length > 300) throw new WorkflowHttpException("HTTP_GRANT_INVALID", "请指定账号/API Key 并填写 1–300 字符的调整原因。");
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var args = new { Operation = operation, Kind = kind, Subject = subject, Admin = admin, Enabled = enabled, Reason = reason.Trim(), Id = Guid.CreateVersion7() };
        if (!await connection.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM workflow_http_credential WHERE id = @Operation FOR UPDATE)", args, transaction)) throw Denied();
        if (!await connection.ExecuteScalarAsync<bool>("""
            SELECT EXISTS (SELECT 1 FROM creativeflow_admin WHERE admin_id = @Admin) AND (
              (@Kind = 'account' AND EXISTS(SELECT 1 FROM creativeflow_account WHERE account_id = @Subject)) OR
              (@Kind = 'apiKey' AND EXISTS(SELECT 1 FROM workflow_api_key WHERE api_key_id = @Subject)))
            """, args, transaction)) throw Denied();
        var current = await connection.QuerySingleOrDefaultAsync<bool?>("SELECT enabled FROM workflow_http_credential_grant WHERE credential_id = @Operation AND subject_kind = @Kind AND subject_id = @Subject", args, transaction);
        if (current == enabled) return;
        if (current is null && await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM workflow_http_credential_grant WHERE credential_id = @Operation", args, transaction) >= 100) throw new WorkflowHttpException("HTTP_GRANT_QUOTA", "此凭据已有 100 条授权记录。");
        await connection.ExecuteAsync("""
            INSERT INTO workflow_http_credential_grant(credential_id,subject_kind,subject_id,enabled) VALUES (@Operation,@Kind,@Subject,@Enabled)
            ON CONFLICT(credential_id,subject_kind,subject_id) DO UPDATE SET enabled = EXCLUDED.enabled, updated_at = clock_timestamp();
            INSERT INTO workflow_http_credential_grant_audit(id,credential_id,subject_kind,subject_id,admin_id,enabled,reason) VALUES (@Id,@Operation,@Kind,@Subject,@Admin,@Enabled,@Reason);
            """, args, transaction);
        await transaction.CommitAsync(ct);
    }

    /// <summary>输出明确 UTC 时间，不随开发机器时区改变含义。</summary>
    private static string Utc(DateTime date) => DateTime.SpecifyKind(date, DateTimeKind.Utc).ToString("O");
    /// <summary>权限错误不区别资源或主体是否存在。</summary>
    private static WorkflowHttpException Denied() => new("HTTP_RESOURCE_DENIED", "外部凭据或授权主体不可访问。");

    /// <summary>安全列表的静态 SQL 映射。</summary>
    /// <param name="Id">凭据标识。</param><param name="Name">显示名称。</param><param name="Description">用途说明。</param>
    /// <param name="Enabled">启用状态。</param><param name="CurrentRevision">默认编辑版本。</param><param name="CreatedAt">创建时间。</param>
    internal sealed record OperationRow(Guid Id, string Name, string Description, bool Enabled, Guid? CurrentRevision, DateTime CreatedAt);
    /// <summary>管理员版本元数据，不含密文。</summary>
    /// <param name="Id">版本标识。</param><param name="Definition">配置 JSON。</param><param name="Digest">配置摘要。</param>
    /// <param name="SecretHint">遮盖后的凭据提示。</param><param name="CreatedAt">创建时间。</param>
    internal sealed record RevisionSummary(Guid Id, string Definition, string Digest, string? SecretHint, DateTime CreatedAt);
    /// <summary>仅在服务器中使用的版本密文行。</summary>
    /// <param name="Id">版本标识。</param><param name="CredentialId">所属凭据。</param><param name="Definition">配置 JSON。</param><param name="Digest">配置摘要。</param>
    /// <param name="SecretCipher">密文。</param><param name="SecretNonce">加密 nonce。</param><param name="SecretTag">认证 tag。</param><param name="SecretHint">遮盖提示。</param>
    internal sealed record RevisionRow(Guid Id, Guid CredentialId, string Definition, string Digest, byte[]? SecretCipher, byte[]? SecretNonce, byte[]? SecretTag, string? SecretHint);
    /// <summary>授权主体显示数据。</summary>
    /// <param name="SubjectKind">主体类别。</param><param name="SubjectId">主体标识。</param><param name="Enabled">是否授权。</param><param name="Name">显示名称。</param>
    internal sealed record GrantRow(string SubjectKind, Guid SubjectId, bool Enabled, string Name);
    /// <summary>授权调整审计数据。</summary>
    /// <param name="SubjectKind">主体类别。</param><param name="SubjectId">主体标识。</param><param name="Enabled">调整后状态。</param><param name="Reason">原因。</param><param name="CreatedAt">时间。</param>
    internal sealed record AuditRow(string SubjectKind, Guid SubjectId, bool Enabled, string Reason, DateTime CreatedAt);
}
