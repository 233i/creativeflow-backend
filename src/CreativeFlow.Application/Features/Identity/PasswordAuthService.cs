using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using CreativeFlow.Application.Abstractions.Common;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Abstractions.Security;
using CreativeFlow.Domain.Identity;

namespace CreativeFlow.Application.Features.Identity;

/// <summary>基于现有不透明账户会话实现邮箱和密码注册登录。</summary>
public sealed class PasswordAuthService(
    IAccountRepository accounts,
    IPasswordCredentialHasher passwordHasher,
    IClock clock,
    IOptions<AuthenticationOptions> configuredOptions)
{
    /// <summary>密码允许的最少字符数。</summary>
    public const int MinimumPasswordLength = 10;
    /// <summary>密码允许的最多字符数。</summary>
    public const int MaximumPasswordLength = 128;
    /// <summary>账号显示名称允许的最多字符数。</summary>
    public const int MaximumDisplayNameLength = 100;
    /// <summary>邮箱地址允许的最多字符数。</summary>
    public const int MaximumEmailLength = 254;

    /// <summary>触发密码锁定之前允许的累计失败次数。</summary>
    private const int MaximumFailedAttempts = 5;
    /// <summary>达到密码失败上限后的锁定时长。</summary>
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);
    /// <summary>当前组件使用的已绑定配置。</summary>
    private readonly AuthenticationOptions options = configuredOptions.Value;

    /// <summary>校验邮箱、密码及显示名，创建账号并按设备关联结果签发会话。</summary>
    public async Task<PasswordAuthResult> RegisterAsync(
        string? email,
        string? password,
        string? displayName,
        DeviceId? deviceId,
        string? userAgent,
        string? ipAddress,
        CancellationToken ct)
    {
        // 先规范化和校验用户输入，再生成密码哈希；账号与设备能否创建关联由仓储结果统一决定。
        if (!TryNormalizeEmail(email, out var canonicalEmail, out var normalizedEmail))
        {
            return PasswordAuthResult.Failed(PasswordAuthStatus.InvalidEmail);
        }

        if (!IsValidPassword(password))
        {
            return PasswordAuthResult.Failed(PasswordAuthStatus.InvalidPassword);
        }

        if (!TryNormalizeDisplayName(displayName, canonicalEmail, out var normalizedName))
        {
            return PasswordAuthResult.Failed(PasswordAuthStatus.InvalidDisplayName);
        }

        var now = clock.UtcNow;
        var accountId = AccountId.New();
        var creation = await accounts.CreatePasswordAccountAsync(
            new PasswordAccountRegistration(
                accountId,
                normalizedName,
                canonicalEmail,
                normalizedEmail,
                passwordHasher.Hash(password!),
                now),
            deviceId,
            ct);

        if (creation != PasswordAccountCreationResult.Created)
        {
            return PasswordAuthResult.Failed(creation switch
            {
                PasswordAccountCreationResult.EmailUnavailable => PasswordAuthStatus.EmailUnavailable,
                _ => PasswordAuthStatus.DeviceUnavailable,
            });
        }

        var profile = new AccountProfile(
            accountId, normalizedName, canonicalEmail, null, []);
        return await CreateSessionAsync(profile, userAgent, ipAddress, now, ct);
    }

    /// <summary>验证对应入口的登录凭据并建立会话。</summary>
    public async Task<PasswordAuthResult> LoginAsync(
        string? email,
        string? password,
        DeviceId? deviceId,
        string? userAgent,
        string? ipAddress,
        CancellationToken ct)
    {
        var validEmail = TryNormalizeEmail(email, out _, out var normalizedEmail);
        var validPassword = IsValidPassword(password);
        var credential = validEmail
            ? await accounts.FindPasswordCredentialAsync(normalizedEmail, ct)
            : null;

        // 凭据不存在时，哈希器会对内部虚拟哈希执行同样昂贵的操作，
        // 避免 API 暴露明显的账户枚举计时信号。
        var verification = passwordHasher.Verify(
            credential?.PasswordHash,
            validPassword ? password! : string.Empty);
        var now = clock.UtcNow;

        if (credential is null || !validPassword || verification == PasswordHashVerification.Failed)
        {
            if (credential is not null)
            {
                await accounts.RecordPasswordFailureAsync(
                    credential.AccountId,
                    now,
                    now.Add(LockoutDuration),
                    MaximumFailedAttempts,
                    ct);
            }
            return PasswordAuthResult.Failed(PasswordAuthStatus.InvalidCredentials);
        }

        if (credential.LockedUntil is { } lockedUntil && lockedUntil > now)
        {
            return PasswordAuthResult.Failed(PasswordAuthStatus.TemporarilyLocked);
        }

        var profile = await accounts.FindByIdAsync(credential.AccountId, ct);
        if (profile is null)
        {
            return PasswordAuthResult.Failed(PasswordAuthStatus.InvalidCredentials);
        }

        if (!profile.IsEnabled)
        {
            return PasswordAuthResult.Failed(PasswordAuthStatus.Disabled);
        }

        if (deviceId is { } device)
        {
            var owner = await accounts.FindAccountForDeviceAsync(device, ct);
            if (owner is { } existingOwner && existingOwner != profile.Id)
            {
                return PasswordAuthResult.Failed(PasswordAuthStatus.DeviceUnavailable);
            }
            if (!await accounts.LinkDeviceAsync(profile.Id, device, ct))
            {
                return PasswordAuthResult.Failed(PasswordAuthStatus.DeviceUnavailable);
            }
        }

        await accounts.ResetPasswordFailuresAsync(profile.Id, ct);
        if (verification == PasswordHashVerification.SucceededRehashNeeded)
        {
            await accounts.UpdatePasswordHashAsync(
                profile.Id,
                passwordHasher.Hash(password!),
                now,
                ct);
        }

        return await CreateSessionAsync(profile, userAgent, ipAddress, now, ct);
    }

    /// <summary>创建与当前画布或身份关联的会话。</summary>
    private async Task<PasswordAuthResult> CreateSessionAsync(
        AccountProfile profile,
        string? userAgent,
        string? ipAddress,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var rawSessionToken = Base64Url(RandomNumberGenerator.GetBytes(32));
        await accounts.CreateSessionAsync(
            new AccountSession(
                Guid.CreateVersion7(),
                profile.Id,
                AccountAuthService.HashToken(rawSessionToken),
                now,
                now.AddDays(Math.Clamp(options.SessionMaxAgeDays, 1, 365)),
                Truncate(userAgent, 512),
                Truncate(ipAddress, 64)),
            ct);

        return new PasswordAuthResult(
            PasswordAuthStatus.Succeeded,
            profile,
            rawSessionToken);
    }

    /// <summary>验证邮箱格式并生成用于登录和唯一性比较的规范化值。</summary>
    internal static bool TryNormalizeEmail(
        string? value,
        out string canonicalEmail,
        out string normalizedEmail)
    {
        // 邮箱规范化会影响账号唯一性，必须保留当前长度、格式和大小写处理，不能只做字符串裁剪。
        canonicalEmail = string.Empty;
        normalizedEmail = string.Empty;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var candidate = value.Trim().Normalize(NormalizationForm.FormKC);
        if (candidate.Length is < 3 or > MaximumEmailLength ||
            candidate.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            return false;
        }

        var separator = candidate.LastIndexOf('@');
        if (separator <= 0 || separator != candidate.IndexOf('@') || separator == candidate.Length - 1)
        {
            return false;
        }

        var local = candidate[..separator];
        var domain = candidate[(separator + 1)..];
        if (local.Length > 64 || local.StartsWith('.') || local.EndsWith('.') || local.Contains(".."))
        {
            return false;
        }

        string asciiDomain;
        try
        {
            asciiDomain = new IdnMapping().GetAscii(domain).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (local.Length + 1 + asciiDomain.Length > MaximumEmailLength ||
            asciiDomain.Length is < 3 or > 253 || !asciiDomain.Contains('.') ||
            asciiDomain.Split('.').Any(label => label.Length is 0 or > 63 ||
                label.StartsWith('-') || label.EndsWith('-') ||
                label.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '-'))))
        {
            return false;
        }

        canonicalEmail = $"{local}@{asciiDomain}";
        normalizedEmail = $"{local.ToLowerInvariant()}@{asciiDomain}";
        return true;
    }

    /// <summary>验证并规范化账号显示名。</summary>
    private static bool TryNormalizeDisplayName(
        string? value,
        string email,
        out string displayName)
    {
        displayName = string.IsNullOrWhiteSpace(value)
            ? email[..email.IndexOf('@')]
            : value.Trim();
        return displayName.Length is > 0 and <= MaximumDisplayNameLength &&
               !displayName.Any(char.IsControl);
    }

    /// <summary>按现有长度与内容边界校验密码输入。</summary>
    private static bool IsValidPassword(string? password) =>
        password is not null && password.Length is >= MinimumPasswordLength and <= MaximumPasswordLength;

    /// <summary>按当前组件的既有上限截断文本。</summary>
    private static string? Truncate(string? value, int maximumLength) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Length <= maximumLength ? value : value[..maximumLength];

    /// <summary>编码为去除填充的 URL 安全 Base64。</summary>
    private static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <param name="Status">当前对象的业务状态，存储和接口名称保持既有映射。</param>
/// <param name="Account">账号资料。</param>
/// <param name="RawSessionToken">仅在签发或读取 Cookie 时使用的原始会话令牌。</param>
public sealed record PasswordAuthResult(
    PasswordAuthStatus Status,
    AccountProfile? Account,
    string? RawSessionToken)
{
    /// <summary>构造当前认证、执行或协议处理的失败结果。</summary>
    public static PasswordAuthResult Failed(PasswordAuthStatus status) => new(status, null, null);
}

public enum PasswordAuthStatus
{
    /// <summary>已经成功完成。</summary>
    Succeeded,
    /// <summary>邮箱格式或长度不符合当前规则。</summary>
    InvalidEmail,
    /// <summary>密码不满足当前校验规则。</summary>
    InvalidPassword,
    /// <summary>显示名称不满足当前校验规则。</summary>
    InvalidDisplayName,
    /// <summary>该邮箱当前不可用于新账号注册。</summary>
    EmailUnavailable,
    /// <summary>账号或密码验证失败。</summary>
    InvalidCredentials,
    /// <summary>账号因连续认证失败而处于临时锁定期。</summary>
    TemporarilyLocked,
    /// <summary>设备当前无法按既有规则关联到该账号。</summary>
    DeviceUnavailable,
    /// <summary>当前账号或工作流发布入口已停用。</summary>
    Disabled,
}
