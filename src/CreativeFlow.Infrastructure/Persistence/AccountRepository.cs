using Dapper;
using Npgsql;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Domain.Identity;

namespace CreativeFlow.Infrastructure.Persistence;

/// <summary>
/// 基于 PostgreSQL 的账户仓储。供应商身份被规范化到单表中，
/// 因此新增 OAuth 供应商无需更改账户架构。
/// </summary>
public sealed class AccountRepository(NpgsqlDataSource dataSource) : IAccountRepository
{
    /// <summary>按令牌哈希查找未过期且账号启用的登录会话，刷新最近活动时间后返回账号资料。</summary>
    public async Task<AccountProfile?> FindBySessionTokenHashAsync(
        byte[] tokenHash,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var accountId = await connection.QueryFirstOrDefaultAsync<Guid?>
        (
            """
            SELECT s.account_id
            FROM creativeflow_auth_session s
            JOIN creativeflow_account a ON a.account_id = s.account_id
            WHERE s.token_hash = @TokenHash
              AND s.expires_at > @Now
              AND a.is_enabled
            """,
            new SessionLookupArgs { TokenHash = tokenHash, Now = now });

        if (accountId is not { } value) return null;

        await connection.ExecuteAsync(
            "UPDATE creativeflow_auth_session SET last_seen_at = @Now WHERE token_hash = @TokenHash",
            new SessionLookupArgs { TokenHash = tokenHash, Now = now });

        return await LoadProfileAsync(connection, new AccountId(value));
    }

    /// <summary>按账号标识加载账号资料及其关联身份信息；不存在时返回空。</summary>
    public async Task<AccountProfile?> FindByIdAsync(
        AccountId accountId,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await LoadProfileAsync(connection, accountId);
    }

    /// <summary>按规范化邮箱读取密码哈希、失败次数和锁定时间，供密码验证使用。</summary>
    public async Task<PasswordCredential?> FindPasswordCredentialAsync(
        string normalizedEmail,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var row = await connection.QueryFirstOrDefaultAsync<PasswordCredentialRow>(
            """
            SELECT account_id, password_hash, failed_attempts, locked_until
            FROM creativeflow_password_credential
            WHERE normalized_email = @NormalizedEmail
            """,
            new PasswordEmailArgs { NormalizedEmail = normalizedEmail });
        return row?.ToDomain();
    }

    /// <summary>在同一事务创建账号、密码凭据及可选设备关联，将邮箱或设备占用映射为明确结果。</summary>
    public async Task<PasswordAccountCreationResult> CreatePasswordAccountAsync(
        PasswordAccountRegistration registration,
        DeviceId? deviceId,
        CancellationToken ct)
    {
        // 账号、密码凭据与设备关联共享原有事务；唯一约束冲突映射为明确业务结果，避免留下部分账号。
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        await connection.ExecuteAsync(
            """
            INSERT INTO creativeflow_account (account_id, display_name, email, avatar_url, created_at, updated_at)
            VALUES (@AccountId, @DisplayName, @Email, NULL, @CreatedAt, @CreatedAt)
            """,
            PasswordRegistrationArgs.From(registration),
            transaction);

        var credentialInserted = await connection.ExecuteAsync(
            """
            INSERT INTO creativeflow_password_credential
                (account_id, normalized_email, password_hash, failed_attempts, locked_until,
                 created_at, updated_at)
            VALUES
                (@AccountId, @NormalizedEmail, @PasswordHash, 0, NULL, @CreatedAt, @CreatedAt)
            ON CONFLICT (normalized_email) DO NOTHING
            """,
            PasswordRegistrationArgs.From(registration),
            transaction);

        if (credentialInserted == 0)
        {
            await transaction.RollbackAsync(ct);
            return PasswordAccountCreationResult.EmailUnavailable;
        }

        if (deviceId is { } device)
        {
            var linked = await connection.ExecuteAsync(
                """
                INSERT INTO creativeflow_account_device (account_id, device_id, linked_at)
                SELECT @AccountId, device_id, @LinkedAt
                FROM device
                WHERE device_id = @DeviceId
                ON CONFLICT (device_id) DO NOTHING
                """,
                new AccountDeviceArgs
                {
                    AccountId = registration.AccountId.Value,
                    DeviceId = device.Value,
                    LinkedAt = registration.CreatedAt,
                },
                transaction);
            if (linked == 0)
            {
                await transaction.RollbackAsync(ct);
                return PasswordAccountCreationResult.DeviceUnavailable;
            }
        }

        await transaction.CommitAsync(ct);
        return PasswordAccountCreationResult.Created;
    }

    /// <summary>累计密码验证失败次数，按阈值设置锁定时间，并重置已过期锁定对应的计数。</summary>
    public async Task RecordPasswordFailureAsync(
        AccountId accountId,
        DateTimeOffset now,
        DateTimeOffset lockUntil,
        int maximumAttempts,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(
            """
            UPDATE creativeflow_password_credential
            SET failed_attempts = CASE
                    WHEN locked_until IS NOT NULL AND locked_until <= @Now THEN 1
                    ELSE failed_attempts + 1
                END,
                locked_until = CASE
                    WHEN locked_until IS NOT NULL AND locked_until <= @Now THEN NULL
                    WHEN failed_attempts + 1 >= @MaximumAttempts THEN @LockUntil
                    ELSE locked_until
                END,
                updated_at = @Now
            WHERE account_id = @AccountId
            """,
            new PasswordFailureArgs
            {
                AccountId = accountId.Value,
                Now = now,
                LockUntil = lockUntil,
                MaximumAttempts = maximumAttempts,
            });
    }

    /// <summary>清零指定账号的密码失败次数并解除凭据锁定。</summary>
    public async Task ResetPasswordFailuresAsync(AccountId accountId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(
            """
            UPDATE creativeflow_password_credential
            SET failed_attempts = 0, locked_until = NULL
            WHERE account_id = @AccountId
            """,
            new AccountIdArgs { AccountId = accountId.Value });
    }

    /// <summary>更新指定账号的密码哈希及凭据更新时间，不修改其他账号资料。</summary>
    public async Task UpdatePasswordHashAsync(
        AccountId accountId,
        string passwordHash,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(
            """
            UPDATE creativeflow_password_credential
            SET password_hash = @PasswordHash, updated_at = @Now
            WHERE account_id = @AccountId
            """,
            new PasswordHashUpdateArgs
            {
                AccountId = accountId.Value,
                PasswordHash = passwordHash,
                Now = now,
            });
    }

    /// <summary>通过外部渠道与主体标识查询已绑定账号；没有关联时返回空。</summary>
    public async Task<AccountId?> FindByExternalIdentityAsync(
        string providerKey,
        string subject,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var accountId = await connection.QueryFirstOrDefaultAsync<Guid?>(
            """
            SELECT account_id
            FROM creativeflow_external_identity
            WHERE provider_key = @ProviderKey AND subject = @Subject
            """,
            new ExternalIdentityLookupArgs { ProviderKey = providerKey, Subject = subject });

        return accountId is { } value ? new AccountId(value) : null;
    }

    /// <summary>在事务内查找或创建外部身份对应账号，并发身份冲突时复用胜出账号、清理未关联的临时账号。</summary>
    public async Task<AccountProfile> GetOrCreateFromExternalAsync(
        ExternalLoginProfile profile,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        var existing = await connection.QueryFirstOrDefaultAsync<Guid?>(
            """
            SELECT account_id
            FROM creativeflow_external_identity
            WHERE provider_key = @ProviderKey AND subject = @Subject
            """,
            new ExternalIdentityLookupArgs
            {
                ProviderKey = profile.ProviderKey,
                Subject = profile.Subject,
            },
            transaction);

        var accountId = existing is { } known
            ? new AccountId(known)
            : AccountId.New();

        if (existing is null)
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO creativeflow_account (account_id, display_name, email, avatar_url, created_at, updated_at)
                VALUES (@AccountId, @DisplayName, @Email, @AvatarUrl, @Now, @Now)
                """,
                new AccountWriteArgs
                {
                    AccountId = accountId.Value,
                    DisplayName = profile.DisplayName,
                    Email = profile.Email,
                    AvatarUrl = profile.AvatarUrl,
                    Now = now,
                },
                transaction);

            var inserted = await connection.ExecuteAsync(
                """
                INSERT INTO creativeflow_external_identity
                    (external_identity_id, account_id, provider_key, subject, display_name,
                     email, avatar_url, created_at, last_login_at)
                VALUES
                    (@ExternalIdentityId, @AccountId, @ProviderKey, @Subject, @DisplayName,
                     @Email, @AvatarUrl, @Now, @Now)
                ON CONFLICT (provider_key, subject) DO NOTHING
                """,
                new ExternalIdentityWriteArgs
                {
                    ExternalIdentityId = Guid.CreateVersion7(),
                    AccountId = accountId.Value,
                    ProviderKey = profile.ProviderKey,
                    Subject = profile.Subject,
                    DisplayName = profile.DisplayName,
                    Email = profile.Email,
                    AvatarUrl = profile.AvatarUrl,
                    Now = now,
                },
                transaction);

            if (inserted == 0)
            {
                // 另一请求赢得了供应商主体竞争。在同一事务中删除上方创建但未被引用的账户，
                // 并使用胜出记录。
                var winner = await connection.QuerySingleAsync<Guid>(
                    """
                    SELECT account_id
                    FROM creativeflow_external_identity
                    WHERE provider_key = @ProviderKey AND subject = @Subject
                    """,
                    new ExternalIdentityLookupArgs
                    {
                        ProviderKey = profile.ProviderKey,
                        Subject = profile.Subject,
                    },
                    transaction);
                await connection.ExecuteAsync(
                    "DELETE FROM creativeflow_account WHERE account_id = @AccountId",
                    new AccountIdArgs { AccountId = accountId.Value },
                    transaction);
                accountId = new AccountId(winner);
            }
        }

        await UpdateExternalIdentityAsync(connection, transaction, accountId, profile, now);
        await transaction.CommitAsync(ct);

        return await LoadProfileAsync(connection, accountId)
            ?? throw new InvalidOperationException("The external identity account disappeared.");
    }

    /// <summary>将外部身份绑定到当前账号，区分已关联和归属冲突，成功时沿用原资料更新规则。</summary>
    public async Task<ExternalIdentityLinkResult> LinkExternalIdentityAsync(
        AccountId accountId,
        ExternalLoginProfile profile,
        DateTimeOffset now,
        CancellationToken ct)
    {
        // 先区分身份属于其他账号和当前账号已有关联两种冲突，再执行原有身份写入与资料更新。
        await using var connection = await dataSource.OpenConnectionAsync(ct);

        var existing = await connection.QueryFirstOrDefaultAsync<Guid?>(
            """
            SELECT account_id
            FROM creativeflow_external_identity
            WHERE provider_key = @ProviderKey AND subject = @Subject
            """,
            new ExternalIdentityLookupArgs
            {
                ProviderKey = profile.ProviderKey,
                Subject = profile.Subject,
            });

        if (existing is { } existingAccount && existingAccount != accountId.Value)
        {
            return ExternalIdentityLinkResult.BelongsToAnotherAccount;
        }

        if (existing is { } sameAccount && sameAccount == accountId.Value)
        {
            return ExternalIdentityLinkResult.AlreadyLinked;
        }

        var providerAlreadyLinked = await connection.QueryFirstOrDefaultAsync<Guid?>(
            """
            SELECT account_id
            FROM creativeflow_external_identity
            WHERE account_id = @AccountId AND provider_key = @ProviderKey
            """,
            new AccountProviderArgs
            {
                AccountId = accountId.Value,
                ProviderKey = profile.ProviderKey,
            });

        if (providerAlreadyLinked is { } linkedAccount)
        {
            return linkedAccount == accountId.Value
                ? ExternalIdentityLinkResult.ProviderAlreadyLinked
                : ExternalIdentityLinkResult.BelongsToAnotherAccount;
        }

        await connection.ExecuteAsync(
            """
            INSERT INTO creativeflow_external_identity
                (external_identity_id, account_id, provider_key, subject, display_name,
                 email, avatar_url, created_at, last_login_at)
            VALUES
                (@ExternalIdentityId, @AccountId, @ProviderKey, @Subject, @DisplayName,
                 @Email, @AvatarUrl, @Now, @Now)
            """,
            new ExternalIdentityWriteArgs
            {
                ExternalIdentityId = Guid.CreateVersion7(),
                AccountId = accountId.Value,
                ProviderKey = profile.ProviderKey,
                Subject = profile.Subject,
                DisplayName = profile.DisplayName,
                Email = profile.Email,
                AvatarUrl = profile.AvatarUrl,
                Now = now,
            });

        await UpdateAccountProfileAsync(connection, accountId, profile, now);
        return ExternalIdentityLinkResult.Linked;
    }

    /// <summary>保存账号登录会话的令牌哈希、到期时间及客户端信息，不保存会话令牌明文。</summary>
    public async Task CreateSessionAsync(AccountSession session, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(
            """
            INSERT INTO creativeflow_auth_session
                (session_id, account_id, token_hash, created_at, expires_at, last_seen_at,
                 user_agent, ip_address)
            VALUES
                (@SessionId, @AccountId, @TokenHash, @CreatedAt, @ExpiresAt, @CreatedAt,
                 @UserAgent, NULLIF(@IpAddress, '')::inet)
            """,
            new SessionWriteArgs
            {
                SessionId = session.SessionId,
                AccountId = session.AccountId.Value,
                TokenHash = session.TokenHash,
                CreatedAt = session.CreatedAt,
                ExpiresAt = session.ExpiresAt,
                UserAgent = session.UserAgent,
                IpAddress = session.IpAddress,
            });
    }

    /// <summary>按令牌哈希删除账号登录会话，使后续鉴权不再匹配该会话。</summary>
    public async Task RevokeSessionAsync(byte[] tokenHash, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(
            "DELETE FROM creativeflow_auth_session WHERE token_hash = @TokenHash",
            new TokenHashArgs { TokenHash = tokenHash });
    }

    /// <summary>查询设备当前绑定的账号标识，未关联设备返回空。</summary>
    public async Task<AccountId?> FindAccountForDeviceAsync(
        DeviceId deviceId,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var accountId = await connection.QueryFirstOrDefaultAsync<Guid?>(
            "SELECT account_id FROM creativeflow_account_device WHERE device_id = @DeviceId",
            new DeviceIdArgs { DeviceId = deviceId.Value });
        return accountId is { } value ? new AccountId(value) : null;
    }

    /// <summary>尝试将已存在设备关联到账号；已归属该账号视为成功，属于其他账号则返回 false。</summary>
    public async Task<bool> LinkDeviceAsync(
        AccountId accountId,
        DeviceId deviceId,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var inserted = await connection.ExecuteAsync(
            """
            INSERT INTO creativeflow_account_device (account_id, device_id, linked_at)
            SELECT @AccountId, device_id, @LinkedAt
            FROM device
            WHERE device_id = @DeviceId
            ON CONFLICT (device_id) DO NOTHING
            """,
            new AccountDeviceArgs
            {
                AccountId = accountId.Value,
                DeviceId = deviceId.Value,
                LinkedAt = DateTimeOffset.UtcNow,
            });

        if (inserted > 0) return true;

        var owner = await connection.QueryFirstOrDefaultAsync<Guid?>(
            "SELECT account_id FROM creativeflow_account_device WHERE device_id = @DeviceId",
            new DeviceIdArgs { DeviceId = deviceId.Value });
        return owner == accountId.Value;
    }

    /// <summary>持久化 OAuth state 哈希、PKCE 和账号绑定上下文，供回调一次性消费。</summary>
    public async Task CreateOAuthTransactionAsync(
        OAuthTransaction transaction,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(
            """
            INSERT INTO creativeflow_oauth_transaction
                (transaction_id, state_hash, provider_key, link_account_id, device_id,
                 return_path, code_verifier, created_at, expires_at)
            VALUES
                (@TransactionId, @StateHash, @ProviderKey, @LinkAccountId, @DeviceId,
                 @ReturnPath, @CodeVerifier, @CreatedAt, @ExpiresAt)
            """,
            new OAuthTransactionArgs
            {
                TransactionId = transaction.TransactionId,
                StateHash = transaction.StateHash,
                ProviderKey = transaction.ProviderKey,
                LinkAccountId = transaction.LinkAccountId?.Value,
                DeviceId = transaction.DeviceId?.Value,
                ReturnPath = transaction.ReturnPath,
                CodeVerifier = transaction.CodeVerifier,
                CreatedAt = transaction.CreatedAt,
                ExpiresAt = transaction.ExpiresAt,
            });
    }

    /// <summary>原子删除并返回未过期的 OAuth 事务，确保相同 state 不能再次消费。</summary>
    public async Task<OAuthTransaction?> ConsumeOAuthTransactionAsync(
        byte[] stateHash,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var row = await connection.QueryFirstOrDefaultAsync<OAuthTransactionRow>(
            """
            DELETE FROM creativeflow_oauth_transaction
            WHERE state_hash = @StateHash AND expires_at > @Now
            RETURNING transaction_id, state_hash, provider_key, link_account_id, device_id,
                      return_path, code_verifier, created_at, expires_at
            """,
            new OAuthConsumeArgs { StateHash = stateHash, Now = now });
        return row?.ToDomain();
    }

    /// <summary>读取账号及其现有外部身份关联资料。</summary>
    private static async Task<AccountProfile?> LoadProfileAsync(
        NpgsqlConnection connection,
        AccountId accountId,
        NpgsqlTransaction? transaction = null)
    {
        var row = await connection.QueryFirstOrDefaultAsync<AccountRow>(
            """
            SELECT account_id, display_name, email, avatar_url, is_enabled
            FROM creativeflow_account
            WHERE account_id = @AccountId
            """,
            new AccountIdArgs { AccountId = accountId.Value },
            transaction);
        if (row is null) return null;

        var providers = await connection.QueryAsync<string>(
            """
            SELECT provider_key
            FROM creativeflow_external_identity
            WHERE account_id = @AccountId
            ORDER BY created_at
            """,
            new AccountIdArgs { AccountId = accountId.Value },
            transaction);

        return new AccountProfile(
            new AccountId(row.AccountId),
            row.DisplayName,
            row.Email,
            row.AvatarUrl,
            providers.ToArray(),
            row.IsEnabled);
    }

    /// <summary>刷新已经关联的外部身份资料。</summary>
    private static async Task UpdateExternalIdentityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AccountId accountId,
        ExternalLoginProfile profile,
        DateTimeOffset now)
    {
        await connection.ExecuteAsync(
            """
            UPDATE creativeflow_external_identity
            SET display_name = @DisplayName,
                email = @Email,
                avatar_url = @AvatarUrl,
                last_login_at = @Now
            WHERE account_id = @AccountId AND provider_key = @ProviderKey
            """,
            new ExternalIdentityUpdateArgs
            {
                AccountId = accountId.Value,
                ProviderKey = profile.ProviderKey,
                DisplayName = profile.DisplayName,
                Email = profile.Email,
                AvatarUrl = profile.AvatarUrl,
                Now = now,
            },
            transaction);

        await UpdateAccountProfileAsync(connection, accountId, profile, now, transaction);
    }

    /// <summary>按外部身份返回的现有字段更新账号资料。</summary>
    private static async Task UpdateAccountProfileAsync(
        NpgsqlConnection connection,
        AccountId accountId,
        ExternalLoginProfile profile,
        DateTimeOffset now,
        NpgsqlTransaction? transaction = null)
    {
        await connection.ExecuteAsync(
            """
            UPDATE creativeflow_account
            SET display_name = @DisplayName,
                email = CASE
                    WHEN EXISTS (
                        SELECT 1
                        FROM creativeflow_password_credential
                        WHERE account_id = @AccountId
                    ) THEN email
                    ELSE COALESCE(@Email, email)
                END,
                avatar_url = COALESCE(@AvatarUrl, avatar_url),
                updated_at = @Now
            WHERE account_id = @AccountId
            """,
            new AccountWriteArgs
            {
                AccountId = accountId.Value,
                DisplayName = profile.DisplayName,
                Email = profile.Email,
                AvatarUrl = profile.AvatarUrl,
                Now = now,
            },
            transaction);
    }
}

internal sealed class AccountRow
{
    /// <summary>账号标识；设备范围的扩展仍以已验证会话为依据。</summary>
    public Guid AccountId { get; init; }
    /// <summary>面向用户展示的账号名称。</summary>
    public string DisplayName { get; init; } = string.Empty;
    /// <summary>账号邮箱地址。</summary>
    public string? Email { get; init; }
    /// <summary>账号头像地址。</summary>
    public string? AvatarUrl { get; init; }
    /// <summary>对应账号、能力或配置是否启用。</summary>
    public bool IsEnabled { get; init; }
}

internal sealed class PasswordCredentialRow
{
    /// <summary>账号标识；设备范围的扩展仍以已验证会话为依据。</summary>
    public Guid AccountId { get; init; }
    /// <summary>版本化密码凭据，不保存密码明文。</summary>
    public string PasswordHash { get; init; } = string.Empty;
    /// <summary>当前累计的认证失败次数。</summary>
    public int FailedAttempts { get; init; }
    /// <summary>密码认证锁定的截止时间。</summary>
    public DateTime? LockedUntil { get; init; }

    /// <summary>把数据库静态行映射为业务记录，保留既有状态和时间语义。</summary>
    public PasswordCredential ToDomain() => new(
        new AccountId(AccountId),
        PasswordHash,
        FailedAttempts,
        LockedUntil is { } locked
            ? new DateTimeOffset(DateTime.SpecifyKind(locked, DateTimeKind.Utc))
            : null);
}

internal sealed class SessionLookupArgs
{
    /// <summary>会话令牌的验证摘要，避免持久化原始令牌。</summary>
    public byte[] TokenHash { get; init; } = [];
    /// <summary>本次操作使用的时间基准。</summary>
    public DateTimeOffset Now { get; init; }
}

internal sealed class TokenHashArgs
{
    /// <summary>会话令牌的验证摘要，避免持久化原始令牌。</summary>
    public byte[] TokenHash { get; init; } = [];
}

internal sealed class AccountIdArgs
{
    /// <summary>账号标识；设备范围的扩展仍以已验证会话为依据。</summary>
    public Guid AccountId { get; init; }
}

internal sealed class DeviceIdArgs
{
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; }
}

internal sealed class ExternalIdentityLookupArgs
{
    /// <summary>外部身份提供方的稳定名称。</summary>
    public string ProviderKey { get; init; } = string.Empty;
    /// <summary>外部身份提供方给出的稳定用户标识。</summary>
    public string Subject { get; init; } = string.Empty;
}

internal sealed class AccountProviderArgs
{
    /// <summary>账号标识；设备范围的扩展仍以已验证会话为依据。</summary>
    public Guid AccountId { get; init; }
    /// <summary>外部身份提供方的稳定名称。</summary>
    public string ProviderKey { get; init; } = string.Empty;
}

internal sealed class AccountWriteArgs
{
    /// <summary>账号标识；设备范围的扩展仍以已验证会话为依据。</summary>
    public Guid AccountId { get; init; }
    /// <summary>面向用户展示的账号名称。</summary>
    public string DisplayName { get; init; } = string.Empty;
    /// <summary>账号邮箱地址。</summary>
    public string? Email { get; init; }
    /// <summary>账号头像地址。</summary>
    public string? AvatarUrl { get; init; }
    /// <summary>本次操作使用的时间基准。</summary>
    public DateTimeOffset Now { get; init; }
}

internal sealed class PasswordEmailArgs
{
    /// <summary>用于唯一性比较和登录查询的规范化邮箱。</summary>
    public string NormalizedEmail { get; init; } = string.Empty;
}

internal sealed class PasswordRegistrationArgs
{
    /// <summary>账号标识；设备范围的扩展仍以已验证会话为依据。</summary>
    public Guid AccountId { get; init; }
    /// <summary>面向用户展示的账号名称。</summary>
    public string DisplayName { get; init; } = string.Empty;
    /// <summary>账号邮箱地址。</summary>
    public string Email { get; init; } = string.Empty;
    /// <summary>用于唯一性比较和登录查询的规范化邮箱。</summary>
    public string NormalizedEmail { get; init; } = string.Empty;
    /// <summary>版本化密码凭据，不保存密码明文。</summary>
    public string PasswordHash { get; init; } = string.Empty;
    /// <summary>记录创建时间，沿用当前 UTC 与序列化约定。</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>把业务记录投影为当前存储操作的静态参数。</summary>
    public static PasswordRegistrationArgs From(PasswordAccountRegistration registration) => new()
    {
        AccountId = registration.AccountId.Value,
        DisplayName = registration.DisplayName,
        Email = registration.Email,
        NormalizedEmail = registration.NormalizedEmail,
        PasswordHash = registration.PasswordHash,
        CreatedAt = registration.CreatedAt,
    };
}

internal sealed class PasswordFailureArgs
{
    /// <summary>账号标识；设备范围的扩展仍以已验证会话为依据。</summary>
    public Guid AccountId { get; init; }
    /// <summary>本次操作使用的时间基准。</summary>
    public DateTimeOffset Now { get; init; }
    /// <summary>本次失败累计后应设置的锁定截止时间。</summary>
    public DateTimeOffset LockUntil { get; init; }
    /// <summary>操作允许的最大尝试次数。</summary>
    public int MaximumAttempts { get; init; }
}

internal sealed class PasswordHashUpdateArgs
{
    /// <summary>账号标识；设备范围的扩展仍以已验证会话为依据。</summary>
    public Guid AccountId { get; init; }
    /// <summary>版本化密码凭据，不保存密码明文。</summary>
    public string PasswordHash { get; init; } = string.Empty;
    /// <summary>本次操作使用的时间基准。</summary>
    public DateTimeOffset Now { get; init; }
}

internal sealed class ExternalIdentityWriteArgs
{
    /// <summary>已关联外部身份记录的唯一标识。</summary>
    public Guid ExternalIdentityId { get; init; }
    /// <summary>账号标识；设备范围的扩展仍以已验证会话为依据。</summary>
    public Guid AccountId { get; init; }
    /// <summary>外部身份提供方的稳定名称。</summary>
    public string ProviderKey { get; init; } = string.Empty;
    /// <summary>外部身份提供方给出的稳定用户标识。</summary>
    public string Subject { get; init; } = string.Empty;
    /// <summary>面向用户展示的账号名称。</summary>
    public string DisplayName { get; init; } = string.Empty;
    /// <summary>账号邮箱地址。</summary>
    public string? Email { get; init; }
    /// <summary>账号头像地址。</summary>
    public string? AvatarUrl { get; init; }
    /// <summary>本次操作使用的时间基准。</summary>
    public DateTimeOffset Now { get; init; }
}

internal sealed class ExternalIdentityUpdateArgs
{
    /// <summary>账号标识；设备范围的扩展仍以已验证会话为依据。</summary>
    public Guid AccountId { get; init; }
    /// <summary>外部身份提供方的稳定名称。</summary>
    public string ProviderKey { get; init; } = string.Empty;
    /// <summary>面向用户展示的账号名称。</summary>
    public string DisplayName { get; init; } = string.Empty;
    /// <summary>账号邮箱地址。</summary>
    public string? Email { get; init; }
    /// <summary>账号头像地址。</summary>
    public string? AvatarUrl { get; init; }
    /// <summary>本次操作使用的时间基准。</summary>
    public DateTimeOffset Now { get; init; }
}

internal sealed class SessionWriteArgs
{
    /// <summary>账号会话或 Agent 会话的关联标识。</summary>
    public Guid SessionId { get; init; }
    /// <summary>账号标识；设备范围的扩展仍以已验证会话为依据。</summary>
    public Guid AccountId { get; init; }
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

internal sealed class AccountDeviceArgs
{
    /// <summary>账号标识；设备范围的扩展仍以已验证会话为依据。</summary>
    public Guid AccountId { get; init; }
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; }
    /// <summary>账号与设备或外部身份建立关联的时间。</summary>
    public DateTimeOffset LinkedAt { get; init; }
}

internal sealed class OAuthTransactionArgs
{
    /// <summary>一次性 OAuth 事务的标识。</summary>
    public Guid TransactionId { get; init; }
    /// <summary>OAuth state 的验证摘要。</summary>
    public byte[] StateHash { get; init; } = [];
    /// <summary>外部身份提供方的稳定名称。</summary>
    public string ProviderKey { get; init; } = string.Empty;
    /// <summary>当前外部身份关联操作的目标账号标识。</summary>
    public Guid? LinkAccountId { get; init; }
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid? DeviceId { get; init; }
    /// <summary>完成认证后返回站内页面的路径。</summary>
    public string ReturnPath { get; init; } = "/";
    /// <summary>OAuth PKCE 的原始校验值，仅在交换授权码时使用。</summary>
    public string CodeVerifier { get; init; } = string.Empty;
    /// <summary>记录创建时间，沿用当前 UTC 与序列化约定。</summary>
    public DateTimeOffset CreatedAt { get; init; }
    /// <summary>会话、凭据、链接或资源的失效时间。</summary>
    public DateTimeOffset ExpiresAt { get; init; }
}

internal sealed class OAuthConsumeArgs
{
    /// <summary>OAuth state 的验证摘要。</summary>
    public byte[] StateHash { get; init; } = [];
    /// <summary>本次操作使用的时间基准。</summary>
    public DateTimeOffset Now { get; init; }
}

internal sealed class OAuthTransactionRow
{
    /// <summary>一次性 OAuth 事务的标识。</summary>
    public Guid TransactionId { get; init; }
    /// <summary>OAuth state 的验证摘要。</summary>
    public byte[] StateHash { get; init; } = [];
    /// <summary>外部身份提供方的稳定名称。</summary>
    public string ProviderKey { get; init; } = string.Empty;
    /// <summary>当前外部身份关联操作的目标账号标识。</summary>
    public Guid? LinkAccountId { get; init; }
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid? DeviceId { get; init; }
    /// <summary>完成认证后返回站内页面的路径。</summary>
    public string ReturnPath { get; init; } = "/";
    /// <summary>OAuth PKCE 的原始校验值，仅在交换授权码时使用。</summary>
    public string CodeVerifier { get; init; } = string.Empty;
    /// <summary>记录创建时间，沿用当前 UTC 与序列化约定。</summary>
    public DateTime CreatedAt { get; init; }
    /// <summary>会话、凭据、链接或资源的失效时间。</summary>
    public DateTime ExpiresAt { get; init; }

    /// <summary>把数据库静态行映射为业务记录，保留既有状态和时间语义。</summary>
    public OAuthTransaction ToDomain() => new(
        TransactionId,
        StateHash,
        ProviderKey,
        LinkAccountId is { } accountId ? new AccountId(accountId) : null,
        DeviceId is { } deviceId ? new DeviceId(deviceId) : null,
        ReturnPath,
        CodeVerifier,
        Utc(CreatedAt),
        Utc(ExpiresAt));

    /// <summary>将数据库时间值按原有约定转换为 UTC DateTimeOffset。</summary>
    private static DateTimeOffset Utc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
