using CreativeFlow.Domain.Identity;

namespace CreativeFlow.Application.Abstractions.Persistence;

/// <summary>独立管理员身份和用户控制功能的持久化边界。</summary>
public interface IAdminRepository
{
    /// <summary>仅在管理员表为空时创建初始化管理员。</summary>
    Task<bool> CreateDefaultIfEmptyAsync(AdminAccount admin, CancellationToken ct);

    /// <summary>根据规范化管理员用户名查找账号。</summary>
    Task<AdminAccount?> FindByUsernameAsync(string username, CancellationToken ct);

    /// <summary>按唯一标识读取对应记录，未找到时返回空值。</summary>
    Task<AdminAccount?> FindByIdAsync(Guid adminId, CancellationToken ct);

    /// <summary>根据有效会话令牌摘要读取独立管理员资料。</summary>
    Task<AdminAccount?> FindBySessionTokenHashAsync(
        byte[] tokenHash,
        DateTimeOffset now,
        CancellationToken ct);

    /// <summary>更新管理员密码及必须改密状态。</summary>
    Task UpdatePasswordAsync(
        Guid adminId,
        string passwordHash,
        bool clearMustChange,
        DateTimeOffset now,
        CancellationToken ct);

    /// <summary>保存独立管理员会话的令牌摘要与有效期。</summary>
    Task CreateSessionAsync(AdminSession session, CancellationToken ct);

    /// <summary>撤销指定令牌摘要对应的会话。</summary>
    Task RevokeSessionAsync(byte[] tokenHash, CancellationToken ct);

    /// <summary>根据管理员筛选条件读取账号列表。</summary>
    Task<ManagedAccountPage> ListAccountsAsync(
        string? search,
        int offset,
        int limit,
        CancellationToken ct);

    /// <summary>
    /// 启用或禁用账户。禁用时会在同一事务中撤销该账户的所有活动会话，
    /// 使访问立即停止，而不是等到 Cookie 过期。
    /// </summary>
    Task<bool> SetAccountEnabledAsync(
        AccountId accountId,
        bool enabled,
        DateTimeOffset now,
        CancellationToken ct);
}

/// <param name="Id">当前实体或契约项目的唯一标识。</param>
/// <param name="Username">管理员登录名。</param>
/// <param name="PasswordHash">版本化密码凭据，不保存密码明文。</param>
/// <param name="MustChangePassword">管理员是否必须先替换初始化密码。</param>
/// <param name="CreatedAt">记录创建时间，沿用当前 UTC 与序列化约定。</param>
/// <param name="UpdatedAt">记录最近一次修改的 UTC 时间。</param>
public sealed record AdminAccount(
    Guid Id,
    string Username,
    string PasswordHash,
    bool MustChangePassword,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <param name="SessionId">账号会话或 Agent 会话的关联标识。</param>
/// <param name="AdminId">独立管理员账号标识。</param>
/// <param name="TokenHash">会话令牌的验证摘要，避免持久化原始令牌。</param>
/// <param name="CreatedAt">记录创建时间，沿用当前 UTC 与序列化约定。</param>
/// <param name="ExpiresAt">会话、凭据、链接或资源的失效时间。</param>
/// <param name="UserAgent">浏览器发送的 User-Agent 信息。</param>
/// <param name="IpAddress">请求来源的 IP 地址。</param>
public sealed record AdminSession(
    Guid SessionId,
    Guid AdminId,
    byte[] TokenHash,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    string? UserAgent,
    string? IpAddress);

/// <param name="Id">当前实体或契约项目的唯一标识。</param>
/// <param name="DisplayName">面向用户展示的账号名称。</param>
/// <param name="Email">账号邮箱地址。</param>
/// <param name="IsEnabled">对应账号、能力或配置是否启用。</param>
/// <param name="CreatedAt">记录创建时间，沿用当前 UTC 与序列化约定。</param>
public sealed record ManagedAccount(
    AccountId Id,
    string DisplayName,
    string? Email,
    bool IsEnabled,
    DateTimeOffset CreatedAt);

/// <param name="Accounts">本次返回的账号集合。</param>
/// <param name="Total">符合当前查询条件的总数量。</param>
public sealed record ManagedAccountPage(
    IReadOnlyList<ManagedAccount> Accounts,
    int Total);
