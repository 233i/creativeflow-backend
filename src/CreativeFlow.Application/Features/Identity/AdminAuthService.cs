using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using CreativeFlow.Application.Abstractions.Common;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Abstractions.Security;

namespace CreativeFlow.Application.Features.Identity;

/// <summary>独立的管理员认证；普通账户会话绝不足以通过验证。</summary>
public sealed class AdminAuthService(
    IAdminRepository admins,
    IPasswordCredentialHasher passwordHasher,
    IClock clock,
    IOptions<AdminOptions> configuredOptions)
{
    /// <summary>密码允许的最少字符数。</summary>
    public const int MinimumPasswordLength = 10;
    /// <summary>密码允许的最多字符数。</summary>
    public const int MaximumPasswordLength = 128;

    /// <summary>当前组件使用的已绑定配置。</summary>
    private readonly AdminOptions options = configuredOptions.Value;

    /// <summary>按既有初始化规则确保默认管理员存在。</summary>
    public async Task EnsureDefaultAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        var username = NormalizeUsername(options.DefaultUsername);
        if (username.Length is < 3 or > 64 ||
            options.DefaultPassword.Length is < 1 or > MaximumPasswordLength)
        {
            throw new InvalidOperationException("The default administrator credentials are invalid.");
        }

        await admins.CreateDefaultIfEmptyAsync(
            new AdminAccount(
                Guid.CreateVersion7(),
                username,
                passwordHasher.Hash(options.DefaultPassword),
                MustChangePassword: true,
                now,
                now),
            ct);
    }

    /// <summary>验证对应入口的登录凭据并建立会话。</summary>
    public async Task<AdminLoginResult> LoginAsync(
        string? username,
        string? password,
        string? userAgent,
        string? ipAddress,
        CancellationToken ct)
    {
        // 即使账号不存在也通过密码验证器走失败路径；哈希升级不能清除首次改密标记，会话只在验证成功后创建。
        var normalizedUsername = NormalizeUsername(username);
        var admin = normalizedUsername.Length is >= 3 and <= 64
            ? await admins.FindByUsernameAsync(normalizedUsername, ct)
            : null;

        var suppliedPassword = password is { Length: > 0 and <= MaximumPasswordLength }
            ? password
            : string.Empty;
        var verification = passwordHasher.Verify(admin?.PasswordHash, suppliedPassword);
        if (admin is null || verification == PasswordHashVerification.Failed)
        {
            return AdminLoginResult.Failed(AdminLoginStatus.InvalidCredentials);
        }

        var now = clock.UtcNow;
        if (verification == PasswordHashVerification.SucceededRehashNeeded)
        {
            await admins.UpdatePasswordAsync(
                admin.Id,
                passwordHasher.Hash(suppliedPassword),
                clearMustChange: false,
                now,
                ct);
            admin = admin with { PasswordHash = string.Empty, UpdatedAt = now };
        }

        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        await admins.CreateSessionAsync(
            new AdminSession(
                Guid.CreateVersion7(),
                admin.Id,
                AccountAuthService.HashToken(token),
                now,
                now.AddHours(Math.Clamp(options.SessionMaxAgeHours, 1, 168)),
                Truncate(userAgent, 512),
                Truncate(ipAddress, 64)),
            ct);

        return new AdminLoginResult(AdminLoginStatus.Succeeded, admin, token);
    }

    /// <summary>验证当前密码后更新管理员凭据。</summary>
    public async Task<AdminPasswordChangeStatus> ChangePasswordAsync(
        Guid adminId,
        string? currentPassword,
        string? newPassword,
        CancellationToken ct)
    {
        var admin = await admins.FindByIdAsync(adminId, ct);
        if (admin is null ||
            passwordHasher.Verify(admin.PasswordHash, currentPassword ?? string.Empty) ==
                PasswordHashVerification.Failed)
        {
            return AdminPasswordChangeStatus.InvalidCurrentPassword;
        }

        if (newPassword is null ||
            newPassword.Length is < MinimumPasswordLength or > MaximumPasswordLength)
        {
            return AdminPasswordChangeStatus.InvalidNewPassword;
        }

        await admins.UpdatePasswordAsync(
            adminId,
            passwordHasher.Hash(newPassword),
            clearMustChange: true,
            clock.UtcNow,
            ct);
        return AdminPasswordChangeStatus.Succeeded;
    }

    /// <summary>按既有大小写与空白规则规范化管理员用户名。</summary>
    private static string NormalizeUsername(string? value) =>
        value?.Trim().ToLowerInvariant() ?? string.Empty;

    /// <summary>按当前组件的既有上限截断文本。</summary>
    private static string? Truncate(string? value, int length) =>
        string.IsNullOrWhiteSpace(value) ? null : value[..Math.Min(value.Length, length)];

    /// <summary>编码为去除填充的 URL 安全 Base64。</summary>
    private static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public enum AdminLoginStatus
{
    /// <summary>已经成功完成。</summary>
    Succeeded,
    /// <summary>账号或密码验证失败。</summary>
    InvalidCredentials,
}

/// <param name="Status">当前对象的业务状态，存储和接口名称保持既有映射。</param>
/// <param name="Admin">已验证的管理员资料。</param>
/// <param name="RawSessionToken">仅在签发或读取 Cookie 时使用的原始会话令牌。</param>
public sealed record AdminLoginResult(
    AdminLoginStatus Status,
    AdminAccount? Admin,
    string? RawSessionToken)
{
    /// <summary>构造当前认证、执行或协议处理的失败结果。</summary>
    public static AdminLoginResult Failed(AdminLoginStatus status) => new(status, null, null);
}

public enum AdminPasswordChangeStatus
{
    /// <summary>已经成功完成。</summary>
    Succeeded,
    /// <summary>当前密码不匹配，不能执行改密。</summary>
    InvalidCurrentPassword,
    /// <summary>新密码不满足当前校验规则。</summary>
    InvalidNewPassword,
}
