using Dapper;
using Npgsql;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Domain.Identity;

namespace CreativeFlow.Infrastructure.Persistence;

/// <summary>初始化 AdminRepository 的依赖与实例状态。</summary>
public sealed class AdminRepository(NpgsqlDataSource dataSource) : IAdminRepository
{
    /// <summary>仅在管理员表为空时创建默认管理员，已有管理员或用户名冲突时不覆盖记录。</summary>
    public async Task<bool> CreateDefaultIfEmptyAsync(AdminAccount admin, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var affected = await connection.ExecuteAsync(
            """
            INSERT INTO creativeflow_admin
                (admin_id, username, password_hash, must_change_password, created_at, updated_at)
            SELECT @AdminId, @Username, @PasswordHash, true, @CreatedAt, @UpdatedAt
            WHERE NOT EXISTS (SELECT 1 FROM creativeflow_admin)
            ON CONFLICT (username) DO NOTHING
            """,
            AdminWriteArgs.From(admin));
        return affected > 0;
    }

    /// <summary>按用户名读取管理员密码凭据及强制改密状态。</summary>
    public async Task<AdminAccount?> FindByUsernameAsync(string username, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var row = await connection.QueryFirstOrDefaultAsync<AdminRow>(
            """
            SELECT admin_id, username, password_hash, must_change_password, created_at, updated_at
            FROM creativeflow_admin
            WHERE username = @Username
            """,
            new AdminUsernameArgs { Username = username });
        return row?.ToDomain();
    }

    /// <summary>按管理员标识读取账号资料；不存在时返回空。</summary>
    public async Task<AdminAccount?> FindByIdAsync(Guid adminId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var row = await connection.QueryFirstOrDefaultAsync<AdminRow>(
            """
            SELECT admin_id, username, password_hash, must_change_password, created_at, updated_at
            FROM creativeflow_admin
            WHERE admin_id = @AdminId
            """,
            new AdminIdArgs { AdminId = adminId });
        return row?.ToDomain();
    }

    /// <summary>通过未过期的会话令牌哈希读取管理员并刷新会话最近活动时间。</summary>
    public async Task<AdminAccount?> FindBySessionTokenHashAsync(
        byte[] tokenHash,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var row = await connection.QueryFirstOrDefaultAsync<AdminRow>(
            """
            SELECT a.admin_id, a.username, a.password_hash, a.must_change_password,
                   a.created_at, a.updated_at
            FROM creativeflow_admin_session s
            JOIN creativeflow_admin a ON a.admin_id = s.admin_id
            WHERE s.token_hash = @TokenHash AND s.expires_at > @Now
            """,
            new AdminSessionLookupArgs { TokenHash = tokenHash, Now = now });

        if (row is null)
        {
            return null;
        }

        await connection.ExecuteAsync(
            "UPDATE creativeflow_admin_session SET last_seen_at = @Now WHERE token_hash = @TokenHash",
            new AdminSessionLookupArgs { TokenHash = tokenHash, Now = now });
        return row.ToDomain();
    }

    /// <summary>更新管理员密码哈希和更新时间，并按参数决定是否清除强制改密标记。</summary>
    public async Task UpdatePasswordAsync(
        Guid adminId,
        string passwordHash,
        bool clearMustChange,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(
            """
            UPDATE creativeflow_admin
            SET password_hash = @PasswordHash,
                must_change_password = CASE
                    WHEN @ClearMustChange THEN false
                    ELSE must_change_password
                END,
                updated_at = @Now
            WHERE admin_id = @AdminId
            """,
            new AdminPasswordArgs
            {
                AdminId = adminId,
                PasswordHash = passwordHash,
                ClearMustChange = clearMustChange,
                Now = now,
            });
    }

    /// <summary>保存管理员登录会话的令牌哈希、到期时间及客户端信息。</summary>
    public async Task CreateSessionAsync(AdminSession session, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(
            """
            INSERT INTO creativeflow_admin_session
                (session_id, admin_id, token_hash, created_at, expires_at, last_seen_at,
                 user_agent, ip_address)
            VALUES
                (@SessionId, @AdminId, @TokenHash, @CreatedAt, @ExpiresAt, @CreatedAt,
                 @UserAgent, NULLIF(@IpAddress, '')::inet)
            """,
            new AdminSessionWriteArgs
            {
                SessionId = session.SessionId,
                AdminId = session.AdminId,
                TokenHash = session.TokenHash,
                CreatedAt = session.CreatedAt,
                ExpiresAt = session.ExpiresAt,
                UserAgent = session.UserAgent,
                IpAddress = session.IpAddress,
            });
    }

    /// <summary>按令牌哈希删除管理员登录会话。</summary>
    public async Task RevokeSessionAsync(byte[] tokenHash, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(
            "DELETE FROM creativeflow_admin_session WHERE token_hash = @TokenHash",
            new AdminTokenArgs { TokenHash = tokenHash });
    }

    /// <summary>按搜索条件分页读取用户账号及总数，供后台用户管理展示。</summary>
    public async Task<ManagedAccountPage> ListAccountsAsync(
        string? search,
        int offset,
        int limit,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var normalizedSearch = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        var filter = new AccountSearchArgs { Search = normalizedSearch };
        var total = await connection.ExecuteScalarAsync<long>(
            """
            SELECT COUNT(*)
            FROM creativeflow_account
            WHERE @Search IS NULL
               OR display_name ILIKE '%' || @Search || '%'
               OR email ILIKE '%' || @Search || '%'
            """,
            filter);

        var rows = await connection.QueryAsync<ManagedAccountRow>(
            """
            SELECT account_id, display_name, email, is_enabled, created_at
            FROM creativeflow_account
            WHERE @Search IS NULL
               OR display_name ILIKE '%' || @Search || '%'
               OR email ILIKE '%' || @Search || '%'
            ORDER BY created_at DESC, account_id DESC
            OFFSET @Offset LIMIT @Limit
            """,
            new AccountPageArgs
            {
                Search = normalizedSearch,
                Offset = offset,
                Limit = limit,
            });

        return new ManagedAccountPage(
            rows.Select(row => row.ToDomain()).ToList(),
            checked((int)total));
    }

    /// <summary>在事务中启用或禁用用户账号；禁用时同时撤销该账号的全部登录会话。</summary>
    public async Task<bool> SetAccountEnabledAsync(
        AccountId accountId,
        bool enabled,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        var affected = await connection.ExecuteAsync(
            """
            UPDATE creativeflow_account
            SET is_enabled = @Enabled, updated_at = @Now
            WHERE account_id = @AccountId
            """,
            new AccountEnabledArgs
            {
                AccountId = accountId.Value,
                Enabled = enabled,
                Now = now,
            },
            transaction);

        if (affected > 0 && !enabled)
        {
            await connection.ExecuteAsync(
                "DELETE FROM creativeflow_auth_session WHERE account_id = @AccountId",
                new ManagedAccountIdArgs { AccountId = accountId.Value },
                transaction);
        }

        await transaction.CommitAsync(ct);
        return affected > 0;
    }
}

internal sealed class AdminRow
{
    /// <summary>独立管理员账号标识。</summary>
    public Guid AdminId { get; init; }
    /// <summary>管理员登录名。</summary>
    public string Username { get; init; } = string.Empty;
    /// <summary>版本化密码凭据，不保存密码明文。</summary>
    public string PasswordHash { get; init; } = string.Empty;
    /// <summary>管理员是否必须先替换初始化密码。</summary>
    public bool MustChangePassword { get; init; }
    /// <summary>记录创建时间，沿用当前 UTC 与序列化约定。</summary>
    public DateTime CreatedAt { get; init; }
    /// <summary>记录最近一次修改的 UTC 时间。</summary>
    public DateTime UpdatedAt { get; init; }

    /// <summary>把数据库静态行映射为业务记录，保留既有状态和时间语义。</summary>
    public AdminAccount ToDomain() => new(
        AdminId,
        Username,
        PasswordHash,
        MustChangePassword,
        Utc(CreatedAt),
        Utc(UpdatedAt));

    /// <summary>将数据库时间值按原有约定转换为 UTC DateTimeOffset。</summary>
    private static DateTimeOffset Utc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}

internal sealed class ManagedAccountRow
{
    /// <summary>账号标识；设备范围的扩展仍以已验证会话为依据。</summary>
    public Guid AccountId { get; init; }
    /// <summary>面向用户展示的账号名称。</summary>
    public string DisplayName { get; init; } = string.Empty;
    /// <summary>账号邮箱地址。</summary>
    public string? Email { get; init; }
    /// <summary>对应账号、能力或配置是否启用。</summary>
    public bool IsEnabled { get; init; }
    /// <summary>记录创建时间，沿用当前 UTC 与序列化约定。</summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>把数据库静态行映射为业务记录，保留既有状态和时间语义。</summary>
    public ManagedAccount ToDomain() => new(
        new AccountId(AccountId),
        DisplayName,
        Email,
        IsEnabled,
        new DateTimeOffset(DateTime.SpecifyKind(CreatedAt, DateTimeKind.Utc)));
}

internal sealed class AdminWriteArgs
{
    /// <summary>独立管理员账号标识。</summary>
    public Guid AdminId { get; init; }
    /// <summary>管理员登录名。</summary>
    public string Username { get; init; } = string.Empty;
    /// <summary>版本化密码凭据，不保存密码明文。</summary>
    public string PasswordHash { get; init; } = string.Empty;
    /// <summary>记录创建时间，沿用当前 UTC 与序列化约定。</summary>
    public DateTimeOffset CreatedAt { get; init; }
    /// <summary>记录最近一次修改的 UTC 时间。</summary>
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>把业务记录投影为当前存储操作的静态参数。</summary>
    public static AdminWriteArgs From(AdminAccount admin) => new()
    {
        AdminId = admin.Id,
        Username = admin.Username,
        PasswordHash = admin.PasswordHash,
        CreatedAt = admin.CreatedAt,
        UpdatedAt = admin.UpdatedAt,
    };
}

internal sealed class AdminUsernameArgs
{
    /// <summary>管理员登录名。</summary>
    public string Username { get; init; } = string.Empty;
}

internal sealed class AdminIdArgs
{
    /// <summary>独立管理员账号标识。</summary>
    public Guid AdminId { get; init; }
}

internal sealed class AdminPasswordArgs
{
    /// <summary>独立管理员账号标识。</summary>
    public Guid AdminId { get; init; }
    /// <summary>版本化密码凭据，不保存密码明文。</summary>
    public string PasswordHash { get; init; } = string.Empty;
    /// <summary>是否清除管理员必须改密的标记。</summary>
    public bool ClearMustChange { get; init; }
    /// <summary>本次操作使用的时间基准。</summary>
    public DateTimeOffset Now { get; init; }
}

internal sealed class AdminSessionLookupArgs
{
    /// <summary>会话令牌的验证摘要，避免持久化原始令牌。</summary>
    public byte[] TokenHash { get; init; } = [];
    /// <summary>本次操作使用的时间基准。</summary>
    public DateTimeOffset Now { get; init; }
}

internal sealed class AdminSessionWriteArgs
{
    /// <summary>账号会话或 Agent 会话的关联标识。</summary>
    public Guid SessionId { get; init; }
    /// <summary>独立管理员账号标识。</summary>
    public Guid AdminId { get; init; }
    /// <summary>会话令牌的验证摘要，避免持久化原始令牌。</summary>
    public byte[] TokenHash { get; init; } = [];
    /// <summary>记录创建时间，沿用当前 UTC 与序列化约定。</summary>
    public DateTimeOffset CreatedAt { get; init; }
    /// <summary>会话、凭据、链接或资源的失效时间。</summary>
    public DateTimeOffset ExpiresAt { get; init; }
    /// <summary>浏览器发送的 User-Agent 信息。</summary>
    public string? UserAgent { get; init; }
    /// <summary>请求来源的 IP 地址。</summary>
    public string? IpAddress { get; init; }
}

internal sealed class AdminTokenArgs
{
    /// <summary>会话令牌的验证摘要，避免持久化原始令牌。</summary>
    public byte[] TokenHash { get; init; } = [];
}

internal sealed class AccountSearchArgs
{
    /// <summary>列表查询的搜索文本。</summary>
    public string? Search { get; init; }
}

internal sealed class AccountPageArgs
{
    /// <summary>列表查询的搜索文本。</summary>
    public string? Search { get; init; }
    /// <summary>查询、流或块内的起始偏移。</summary>
    public int Offset { get; init; }
    /// <summary>本次查询或处理允许返回的最大数量。</summary>
    public int Limit { get; init; }
}

internal sealed class AccountEnabledArgs
{
    /// <summary>账号标识；设备范围的扩展仍以已验证会话为依据。</summary>
    public Guid AccountId { get; init; }
    /// <summary>当前配置或资源是否启用。</summary>
    public bool Enabled { get; init; }
    /// <summary>本次操作使用的时间基准。</summary>
    public DateTimeOffset Now { get; init; }
}

internal sealed class ManagedAccountIdArgs
{
    /// <summary>账号标识；设备范围的扩展仍以已验证会话为依据。</summary>
    public Guid AccountId { get; init; }
}
