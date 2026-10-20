using System.Text.Json.Nodes;
using Dapper;
using Npgsql;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Features.Knowledge;

namespace CreativeFlow.Infrastructure.Persistence;

/// <summary>知识授权即时查询，不缓存账号归属或权限；管理修改与审计原子提交。</summary>
public sealed class KnowledgeAccessRepository(NpgsqlDataSource source) : IKnowledgeAccessRepository
{
    /// <summary>匿名设备和未知主体默认拒绝；管理员调试不扩展普通账号的知识访问范围。</summary>
    public async Task<bool> CanUseAsync(Guid library, KnowledgeSubject subject, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        return await connection.ExecuteScalarAsync<bool>("""
            SELECT EXISTS (SELECT 1 FROM knowledge_base b WHERE b.id = @Library AND b.enabled AND (
              (@Kind = 'admin' AND EXISTS (SELECT 1 FROM creativeflow_admin WHERE admin_id = @Id)) OR
              (@Kind = 'account' AND EXISTS (SELECT 1 FROM knowledge_grant g
                JOIN creativeflow_account a ON a.account_id = g.account_id AND a.is_enabled
                JOIN creativeflow_account_device d ON d.account_id = a.account_id AND d.device_id = @Device
                WHERE g.base_id = b.id AND g.account_id = @Id AND g.enabled)) OR
              (@Kind = 'apiKey' AND EXISTS (SELECT 1 FROM knowledge_api_key_grant g
                JOIN workflow_api_key k ON k.api_key_id = g.api_key_id AND k.enabled AND k.revoked_at IS NULL
                  AND (k.expires_at IS NULL OR k.expires_at > clock_timestamp())
                JOIN workflow w ON w.workflow_id = k.workflow_id AND w.device_id = @Device
                WHERE g.base_id = b.id AND g.api_key_id = @Id AND g.enabled))))
            """, new { Library = library, subject.Kind, subject.Id, subject.Device });
    }

    /// <summary>管理列表只包含账号显示资料、授权状态和审计，不返回凭据。</summary>
    public async Task<JsonObject> GrantsAsync(Guid library, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        var grants = await connection.QueryAsync<GrantRow>("""
            SELECT g.account_id, a.display_name, a.email, a.is_enabled AS account_enabled, g.enabled
            FROM knowledge_grant g JOIN creativeflow_account a ON a.account_id = g.account_id
            WHERE g.base_id = @Library ORDER BY a.display_name LIMIT 100
            """, new { Library = library });
        var audit = await connection.QueryAsync<AuditRow>("""
            SELECT account_id, admin_id, enabled, reason, created_at FROM knowledge_grant_audit
            WHERE base_id = @Library ORDER BY created_at DESC LIMIT 100
            """, new { Library = library });
        var keyGrants = await connection.QueryAsync<KeyGrantRow>("""
            SELECT g.api_key_id, k.name, k.workflow_id, w.name AS workflow_name, g.enabled,
                k.enabled AND k.revoked_at IS NULL AND (k.expires_at IS NULL OR k.expires_at > clock_timestamp()) AS key_enabled
            FROM knowledge_api_key_grant g JOIN workflow_api_key k ON k.api_key_id = g.api_key_id
            JOIN workflow w ON w.workflow_id = k.workflow_id WHERE g.base_id = @Library ORDER BY k.name LIMIT 100
            """, new { Library = library });
        var keyAudit = await connection.QueryAsync<KeyAuditRow>("""
            SELECT api_key_id, admin_id, enabled, reason, created_at FROM knowledge_api_key_grant_audit
            WHERE base_id = @Library ORDER BY created_at DESC LIMIT 100
            """, new { Library = library });
        return new JsonObject
        {
            ["apiKeys"] = new JsonArray(keyGrants.Select(row => (JsonNode)new JsonObject
            { ["apiKeyId"] = row.ApiKeyId.ToString(), ["name"] = row.Name, ["workflowId"] = row.WorkflowId.ToString(),
              ["workflowName"] = row.WorkflowName, ["keyEnabled"] = row.KeyEnabled, ["enabled"] = row.Enabled }).ToArray()),
            ["apiKeyAudit"] = new JsonArray(keyAudit.Select(row => (JsonNode)new JsonObject
            { ["apiKeyId"] = row.ApiKeyId.ToString(), ["adminId"] = row.AdminId.ToString(), ["enabled"] = row.Enabled,
              ["reason"] = row.Reason, ["createdAt"] = DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc).ToString("O") }).ToArray()),
            ["grants"] = new JsonArray(grants.Select(row => (JsonNode)new JsonObject
            { ["accountId"] = row.AccountId, ["displayName"] = row.DisplayName, ["email"] = row.Email,
              ["accountEnabled"] = row.AccountEnabled, ["enabled"] = row.Enabled }).ToArray()),
            ["audit"] = new JsonArray(audit.Select(row => (JsonNode)new JsonObject
            { ["accountId"] = row.AccountId, ["adminId"] = row.AdminId, ["enabled"] = row.Enabled,
              ["reason"] = row.Reason, ["createdAt"] = DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc).ToString("O") }).ToArray()),
        };
    }

    /// <summary>验证目标账号和管理员，限制每库一百个授权对象；撤权不删除历史审计。</summary>
    public async Task SetGrantAsync(Guid library, Guid account, Guid admin, bool enabled, string reason, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 300) throw new KnowledgeException("请填写 1–300 字符的授权调整原因。");
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var parameters = new { Library = library, Account = account, Admin = admin, Enabled = enabled, Reason = reason.Trim(), Id = Guid.CreateVersion7() };
        if (!await connection.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM knowledge_base WHERE id = @Library FOR UPDATE)", parameters, transaction)
            || !await connection.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM creativeflow_account WHERE account_id = @Account) AND EXISTS (SELECT 1 FROM creativeflow_admin WHERE admin_id = @Admin)", parameters, transaction))
            throw new KnowledgeException("知识库或授权账号不存在。");
        var previous = await connection.QuerySingleOrDefaultAsync<bool?>("SELECT enabled FROM knowledge_grant WHERE base_id = @Library AND account_id = @Account", parameters, transaction);
        if (previous == enabled) return;
        if (previous is null && await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM knowledge_grant WHERE base_id = @Library", parameters, transaction) >= 100)
            throw new KnowledgeException("每库最多一百个授权对象，请先评审正式容量。");
        await connection.ExecuteAsync("""
            INSERT INTO knowledge_grant(base_id, account_id, enabled) VALUES (@Library, @Account, @Enabled)
            ON CONFLICT (base_id, account_id) DO UPDATE SET enabled = EXCLUDED.enabled, updated_at = clock_timestamp();
            INSERT INTO knowledge_grant_audit(id, base_id, account_id, admin_id, enabled, reason)
            VALUES (@Id, @Library, @Account, @Admin, @Enabled, @Reason);
            """, parameters, transaction);
        await transaction.CommitAsync(ct);
    }

    /// <summary>Key 知识权限与账号权限分表；操作需现存管理员与 Key，重复设置不重复记审计。</summary>
    public async Task SetApiKeyGrantAsync(Guid library, Guid apiKey, Guid admin, bool enabled, string reason, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 300) throw new KnowledgeException("请填写 1–300 字符的授权调整原因。");
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var parameters = new { Library = library, ApiKey = apiKey, Admin = admin, Enabled = enabled, Reason = reason.Trim(), Id = Guid.CreateVersion7() };
        if (!await connection.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM knowledge_base WHERE id = @Library FOR UPDATE)", parameters, transaction)
            || !await connection.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM workflow_api_key WHERE api_key_id = @ApiKey) AND EXISTS (SELECT 1 FROM creativeflow_admin WHERE admin_id = @Admin)", parameters, transaction))
            throw new KnowledgeException("知识库或 API Key 不存在。");
        var previous = await connection.QuerySingleOrDefaultAsync<bool?>("SELECT enabled FROM knowledge_api_key_grant WHERE base_id = @Library AND api_key_id = @ApiKey", parameters, transaction);
        if (previous == enabled) return;
        if (previous is null && await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM knowledge_api_key_grant WHERE base_id = @Library", parameters, transaction) >= 100)
            throw new KnowledgeException("每库最多一百个 API Key 授权对象。");
        await connection.ExecuteAsync("""
            INSERT INTO knowledge_api_key_grant(base_id, api_key_id, enabled) VALUES (@Library, @ApiKey, @Enabled)
            ON CONFLICT (base_id, api_key_id) DO UPDATE SET enabled = EXCLUDED.enabled, updated_at = clock_timestamp();
            INSERT INTO knowledge_api_key_grant_audit(id, base_id, api_key_id, admin_id, enabled, reason)
            VALUES (@Id, @Library, @ApiKey, @Admin, @Enabled, @Reason);
            """, parameters, transaction);
        await transaction.CommitAsync(ct);
    }

    /// <summary>静态映射 Key 授权的安全显示资料。</summary>
    /// <param name="ApiKeyId">Key 记录标识。</param>
    /// <param name="Name">Key 名称。</param>
    /// <param name="WorkflowId">所属工作流标识。</param>
    /// <param name="WorkflowName">所属工作流名称。</param>
    /// <param name="Enabled">独立知识授权状态。</param>
    /// <param name="KeyEnabled">Key 当前是否可用。</param>
    internal sealed record KeyGrantRow(Guid ApiKeyId, string Name, Guid WorkflowId, string WorkflowName, bool Enabled, bool KeyEnabled);

    /// <summary>Key 知识授权的持久化审计。</summary>
    /// <param name="ApiKeyId">被调整的 Key 记录标识。</param>
    /// <param name="AdminId">调整管理员。</param>
    /// <param name="Enabled">调整后状态。</param>
    /// <param name="Reason">调整原因。</param>
    /// <param name="CreatedAt">调整时间。</param>
    internal sealed record KeyAuditRow(Guid ApiKeyId, Guid AdminId, bool Enabled, string Reason, DateTime CreatedAt);

    /// <summary>静态映射授权展示列。</summary>
    /// <param name="AccountId">获授权账号。</param>
    /// <param name="DisplayName">账号显示名。</param>
    /// <param name="Email">账号邮箱。</param>
    /// <param name="AccountEnabled">账号当前启用状态。</param>
    /// <param name="Enabled">授权当前状态。</param>
    internal sealed record GrantRow(Guid AccountId, string DisplayName, string? Email, bool AccountEnabled, bool Enabled);
    /// <summary>静态映射授权审计列。</summary>
    /// <param name="AccountId">被调整账号。</param>
    /// <param name="AdminId">执行调整的管理员。</param>
    /// <param name="Enabled">调整后的授权状态。</param>
    /// <param name="Reason">管理员填写的原因。</param>
    /// <param name="CreatedAt">调整时间。</param>
    internal sealed record AuditRow(Guid AccountId, Guid AdminId, bool Enabled, string Reason, DateTime CreatedAt);
}
