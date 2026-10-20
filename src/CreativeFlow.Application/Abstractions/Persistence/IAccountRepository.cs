using CreativeFlow.Domain.Identity;

namespace CreativeFlow.Application.Abstractions.Persistence;

/// <summary>
/// 账户、外部登录、OAuth 事务和会话的持久化存储。
/// </summary>
/// <remarks>
/// 原始 OAuth 状态和会话令牌绝不会跨越此端口。调用方只传递可验证浏览器之值的哈希，
/// 因此数据库读取结果或日志无法被重放为会话。
/// </remarks>
public interface IAccountRepository
{
    /// <summary>根据有效会话令牌摘要读取普通账号资料。</summary>
    Task<AccountProfile?> FindBySessionTokenHashAsync(
        byte[] tokenHash,
        DateTimeOffset now,
        CancellationToken ct);

    /// <summary>按唯一标识读取对应记录，未找到时返回空值。</summary>
    Task<AccountProfile?> FindByIdAsync(AccountId accountId, CancellationToken ct);

    /// <summary>按规范化邮箱读取密码登录凭据。</summary>
    Task<PasswordCredential?> FindPasswordCredentialAsync(
        string normalizedEmail,
        CancellationToken ct);

    /// <summary>原子创建密码账号和关联凭据，沿用邮箱唯一性处理。</summary>
    Task<PasswordAccountCreationResult> CreatePasswordAccountAsync(
        PasswordAccountRegistration registration,
        DeviceId? deviceId,
        CancellationToken ct);

    /// <summary>累计密码认证失败并更新既有锁定状态。</summary>
    Task RecordPasswordFailureAsync(
        AccountId accountId,
        DateTimeOffset now,
        DateTimeOffset lockUntil,
        int maximumAttempts,
        CancellationToken ct);

    /// <summary>成功认证后清除失败计数与锁定状态。</summary>
    Task ResetPasswordFailuresAsync(AccountId accountId, CancellationToken ct);

    /// <summary>更新账号的版本化密码哈希。</summary>
    Task UpdatePasswordHashAsync(
        AccountId accountId,
        string passwordHash,
        DateTimeOffset now,
        CancellationToken ct);

    /// <summary>根据提供方与 subject 查找已关联账号。</summary>
    Task<AccountId?> FindByExternalIdentityAsync(
        string providerKey,
        string subject,
        CancellationToken ct);

    /// <summary>根据外部身份解析或创建账号资料。</summary>
    Task<AccountProfile> GetOrCreateFromExternalAsync(
        ExternalLoginProfile profile,
        DateTimeOffset now,
        CancellationToken ct);

    /// <summary>将外部身份关联到目标账号，保留唯一性冲突语义。</summary>
    Task<ExternalIdentityLinkResult> LinkExternalIdentityAsync(
        AccountId accountId,
        ExternalLoginProfile profile,
        DateTimeOffset now,
        CancellationToken ct);

    /// <summary>保存普通账号会话的令牌摘要、有效期和请求信息。</summary>
    Task CreateSessionAsync(AccountSession session, CancellationToken ct);

    /// <summary>撤销指定令牌摘要对应的会话。</summary>
    Task RevokeSessionAsync(byte[] tokenHash, CancellationToken ct);

    /// <summary>读取设备当前持久化的账号归属。</summary>
    Task<AccountId?> FindAccountForDeviceAsync(DeviceId deviceId, CancellationToken ct);

    /// <summary>按既有所有权规则建立账号与设备的明确关联。</summary>
    Task<bool> LinkDeviceAsync(AccountId accountId, DeviceId deviceId, CancellationToken ct);

    /// <summary>保存一次性 OAuth state、PKCE 和返回地址。</summary>
    Task CreateOAuthTransactionAsync(OAuthTransaction transaction, CancellationToken ct);

    /// <summary>原子消费仍有效的一次性 OAuth 事务。</summary>
    Task<OAuthTransaction?> ConsumeOAuthTransactionAsync(
        byte[] stateHash,
        DateTimeOffset now,
        CancellationToken ct);
}

/// <param name="Id">当前实体或契约项目的唯一标识。</param>
/// <param name="DisplayName">面向用户展示的账号名称。</param>
/// <param name="Email">账号邮箱地址。</param>
/// <param name="AvatarUrl">账号头像地址。</param>
/// <param name="ExternalProviders">账号已关联的外部身份提供方。</param>
/// <param name="IsEnabled">对应账号、能力或配置是否启用。</param>
public sealed record AccountProfile(
    AccountId Id,
    string DisplayName,
    string? Email,
    string? AvatarUrl,
    IReadOnlyList<string> ExternalProviders,
    bool IsEnabled = true);

/// <param name="AccountId">账号标识；设备范围的扩展仍以已验证会话为依据。</param>
/// <param name="PasswordHash">版本化密码凭据，不保存密码明文。</param>
/// <param name="FailedAttempts">当前累计的认证失败次数。</param>
/// <param name="LockedUntil">密码认证锁定的截止时间。</param>
public sealed record PasswordCredential(
    AccountId AccountId,
    string PasswordHash,
    int FailedAttempts,
    DateTimeOffset? LockedUntil);

/// <param name="AccountId">账号标识；设备范围的扩展仍以已验证会话为依据。</param>
/// <param name="DisplayName">面向用户展示的账号名称。</param>
/// <param name="Email">账号邮箱地址。</param>
/// <param name="NormalizedEmail">用于唯一性比较和登录查询的规范化邮箱。</param>
/// <param name="PasswordHash">版本化密码凭据，不保存密码明文。</param>
/// <param name="CreatedAt">记录创建时间，沿用当前 UTC 与序列化约定。</param>
public sealed record PasswordAccountRegistration(
    AccountId AccountId,
    string DisplayName,
    string Email,
    string NormalizedEmail,
    string PasswordHash,
    DateTimeOffset CreatedAt);

public enum PasswordAccountCreationResult
{
    /// <summary>已成功创建账号，并在提供设备时完成关联。</summary>
    Created,
    /// <summary>该邮箱当前不可用于新账号注册。</summary>
    EmailUnavailable,
    /// <summary>设备当前无法按既有规则关联到该账号。</summary>
    DeviceUnavailable,
}

/// <summary>供应商适配器返回的规范化身份。</summary>
/// <param name="ProviderKey">外部身份提供方的稳定名称。</param>
/// <param name="Subject">外部身份提供方给出的稳定用户标识。</param>
/// <param name="DisplayName">面向用户展示的账号名称。</param>
/// <param name="Email">账号邮箱地址。</param>
/// <param name="AvatarUrl">账号头像地址。</param>
public sealed record ExternalLoginProfile(
    string ProviderKey,
    string Subject,
    string DisplayName,
    string? Email,
    string? AvatarUrl);

/// <param name="SessionId">账号会话或 Agent 会话的关联标识。</param>
/// <param name="AccountId">账号标识；设备范围的扩展仍以已验证会话为依据。</param>
/// <param name="TokenHash">会话令牌的验证摘要，避免持久化原始令牌。</param>
/// <param name="CreatedAt">记录创建时间，沿用当前 UTC 与序列化约定。</param>
/// <param name="ExpiresAt">会话、凭据、链接或资源的失效时间。</param>
/// <param name="UserAgent">浏览器发送的 User-Agent 信息。</param>
/// <param name="IpAddress">请求来源的 IP 地址。</param>
public sealed record AccountSession(
    Guid SessionId,
    AccountId AccountId,
    byte[] TokenHash,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    string? UserAgent,
    string? IpAddress);

/// <param name="TransactionId">一次性 OAuth 事务的标识。</param>
/// <param name="StateHash">OAuth state 的验证摘要。</param>
/// <param name="ProviderKey">外部身份提供方的稳定名称。</param>
/// <param name="LinkAccountId">当前外部身份关联操作的目标账号标识。</param>
/// <param name="DeviceId">资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</param>
/// <param name="ReturnPath">完成认证后返回站内页面的路径。</param>
/// <param name="CodeVerifier">OAuth PKCE 的原始校验值，仅在交换授权码时使用。</param>
/// <param name="CreatedAt">记录创建时间，沿用当前 UTC 与序列化约定。</param>
/// <param name="ExpiresAt">会话、凭据、链接或资源的失效时间。</param>
public sealed record OAuthTransaction(
    Guid TransactionId,
    byte[] StateHash,
    string ProviderKey,
    AccountId? LinkAccountId,
    DeviceId? DeviceId,
    string ReturnPath,
    string CodeVerifier,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

public enum ExternalIdentityLinkResult
{
    /// <summary>外部身份已经关联到指定账号。</summary>
    Linked,
    /// <summary>该外部身份已属于当前账号，无需重复关联。</summary>
    AlreadyLinked,
    /// <summary>该外部身份已经属于其他账号。</summary>
    BelongsToAnotherAccount,
    /// <summary>当前账号已关联同一提供方的另一外部身份。</summary>
    ProviderAlreadyLinked,
}
