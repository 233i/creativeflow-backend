using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Abstractions.Security;
using CreativeFlow.Domain.Identity;

namespace CreativeFlow.Application.Features.Identity;

/// <summary>
/// 协调与供应商无关的 OAuth 以及持久化账户和会话存储。
/// </summary>
public sealed class AccountAuthService(
    IAccountRepository accounts,
    IExternalLoginProviderRegistry providers,
    IOptions<AuthenticationOptions> configuredOptions)
{
    /// <summary>当前组件使用的已绑定配置。</summary>
    private readonly AuthenticationOptions options = configuredOptions.Value;

    /// <summary>保存一次性 OAuth 事务并生成外部授权跳转参数。</summary>
    public async Task<ExternalLoginStart> BeginAsync(
        string providerKey,
        Uri callbackUri,
        string returnPath,
        DeviceId? deviceId,
        AccountId? linkAccountId,
        CancellationToken ct)
    {
        var provider = providers.Find(providerKey)
            ?? throw new ExternalLoginException("provider_unavailable");

        var normalizedReturnPath = NormalizeReturnPath(returnPath);
        var now = DateTimeOffset.UtcNow;
        var state = CreateToken(32);
        var verifier = CreateToken(48);
        var transaction = new OAuthTransaction(
            Guid.CreateVersion7(),
            HashToken(state),
            provider.Key,
            linkAccountId,
            deviceId,
            normalizedReturnPath,
            verifier,
            now,
            now.AddMinutes(Math.Clamp(options.OAuthTransactionMinutes, 1, 30)));

        await accounts.CreateOAuthTransactionAsync(transaction, ct);

        return new ExternalLoginStart(
            provider.BuildAuthorizationUrl(callbackUri, state, CreateCodeChallenge(verifier)),
            normalizedReturnPath);
    }

    /// <summary>消费 OAuth 回调并按既有账号关联规则建立会话。</summary>
    public async Task<ExternalLoginCompletion> CompleteAsync(
        string providerKey,
        string code,
        string state,
        Uri callbackUri,
        DeviceId? currentDeviceId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
        {
            throw new ExternalLoginException("invalid_callback");
        }

        var transaction = await accounts.ConsumeOAuthTransactionAsync(HashToken(state), DateTimeOffset.UtcNow, ct)
            ?? throw new ExternalLoginException("invalid_or_expired_state");

        if (!string.Equals(transaction.ProviderKey, providerKey, StringComparison.Ordinal))
        {
            throw new ExternalLoginException("provider_mismatch");
        }

        var provider = providers.Find(providerKey)
            ?? throw new ExternalLoginException("provider_unavailable");

        ExternalLoginProfile profile;
        try
        {
            profile = await provider.ExchangeCodeAsync(code, transaction.CodeVerifier, callbackUri, ct);
        }
        catch (ExternalLoginException)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            throw new ExternalLoginException("provider_unreachable", exception);
        }

        var deviceId = currentDeviceId ?? transaction.DeviceId;
        var deviceOwner = deviceId is { } current
            ? await accounts.FindAccountForDeviceAsync(current, ct)
            : null;

        AccountProfile account;
        if (transaction.LinkAccountId is { } linkAccountId)
        {
            var link = await accounts.LinkExternalIdentityAsync(
                linkAccountId, profile, DateTimeOffset.UtcNow, ct);
            if (link is ExternalIdentityLinkResult.BelongsToAnotherAccount
                or ExternalIdentityLinkResult.ProviderAlreadyLinked)
            {
                throw new ExternalLoginException("provider_already_linked");
            }

            account = await accounts.FindByIdAsync(linkAccountId, ct)
                ?? throw new ExternalLoginException("account_not_found");
        }
        else
        {
            var existingIdentityAccount = await accounts.FindByExternalIdentityAsync(
                profile.ProviderKey, profile.Subject, ct);

            // 已归某账户所有的设备，不能仅因使用了不同供应商就被静默切换到另一账户。
            // 用户可以显式退出登录（在登录入口创建新设备），或使用账户关联流程。
            if (deviceOwner is { } owner &&
                (existingIdentityAccount is null || existingIdentityAccount.Value != owner))
            {
                throw new ExternalLoginException("device_belongs_to_another_account");
            }

            account = existingIdentityAccount is { } known
                ? await accounts.FindByIdAsync(known, ct)
                    ?? throw new ExternalLoginException("account_not_found")
                : await accounts.GetOrCreateFromExternalAsync(profile, DateTimeOffset.UtcNow, ct);
        }

        if (!account.IsEnabled)
        {
            throw new ExternalLoginException("account_disabled");
        }

        if (deviceId is { } device)
        {
            if (deviceOwner is { } owner && owner != account.Id)
            {
                throw new ExternalLoginException("device_belongs_to_another_account");
            }

            if (!await accounts.LinkDeviceAsync(account.Id, device, ct))
            {
                throw new ExternalLoginException("device_belongs_to_another_account");
            }
        }

        var now = DateTimeOffset.UtcNow;
        var rawSessionToken = CreateToken(32);
        await accounts.CreateSessionAsync(
            new AccountSession(
                Guid.CreateVersion7(),
                account.Id,
                HashToken(rawSessionToken),
                now,
                now.AddDays(Math.Clamp(options.SessionMaxAgeDays, 1, 365)),
                null,
                null),
            ct);

        return new ExternalLoginCompletion(
            rawSessionToken,
            account,
            transaction.ReturnPath);
    }

    /// <summary>对不透明会话令牌生成存储与查询使用的摘要。</summary>
    public static byte[] HashToken(string token) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(token));

    /// <summary>只保留符合原有规则的站内认证返回路径。</summary>
    public static string NormalizeReturnPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !value.StartsWith("/", StringComparison.Ordinal) ||
            value.StartsWith("//", StringComparison.Ordinal) ||
            value.Contains("\\", StringComparison.Ordinal))
        {
            return "/";
        }

        return value.Length <= 512 ? value : "/";
    }

    /// <summary>生成具有原有随机强度的不透明会话或认证令牌。</summary>
    private static string CreateToken(int byteCount) =>
        Base64Url(RandomNumberGenerator.GetBytes(byteCount));

    /// <summary>根据 PKCE verifier 构造授权请求的 challenge。</summary>
    private static string CreateCodeChallenge(string verifier) =>
        Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(verifier)));

    /// <summary>编码为去除填充的 URL 安全 Base64。</summary>
    private static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <param name="AuthorizationUrl">浏览器跳转到外部身份提供方的授权地址。</param>
/// <param name="ReturnPath">完成认证后返回站内页面的路径。</param>
public sealed record ExternalLoginStart(string AuthorizationUrl, string ReturnPath);

/// <param name="RawSessionToken">仅在签发或读取 Cookie 时使用的原始会话令牌。</param>
/// <param name="Account">账号资料。</param>
/// <param name="ReturnPath">完成认证后返回站内页面的路径。</param>
public sealed record ExternalLoginCompletion(
    string RawSessionToken,
    AccountProfile Account,
    string ReturnPath);

/// <summary>初始化 ExternalLoginException 的依赖与实例状态。</summary>
public sealed class ExternalLoginException(string code, Exception? inner = null)
    : Exception(code, inner)
{
    /// <summary>业务错误、风险项或外部认证返回的代码，含义由所属契约定义。</summary>
    public string Code { get; } = code;
}
