using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using CreativeFlow.Application.Abstractions.Security;

namespace CreativeFlow.Infrastructure.Security;

/// <summary>
/// ASP.NET Core Identity 的自描述 PBKDF2 格式，使用当前 HMAC-SHA512 计算强度。
/// </summary>
internal sealed class AspNetCorePasswordCredentialHasher : IPasswordCredentialHasher
{
    // OWASP 当前建议的 PBKDF2-HMAC-SHA512 最低值。编码后的 Identity V3 负载携带该迭代次数，
    // 使 VerifyHashedPassword 能在未来提高强度后请求升级。
    /// <summary>写入 Identity V3 凭据的 PBKDF2-HMAC-SHA512 迭代次数。</summary>
    private const int IterationCount = 220_000;

    /// <summary>满足 PasswordHasher 用户参数要求的固定占位对象，不参与账号归属。</summary>
    private static readonly object Subject = new();
    /// <summary>ASP.NET Core 版本化密码哈希实现。</summary>
    private readonly PasswordHasher<object> hasher;
    /// <summary>用于均衡未知账号密码校验路径的合成哈希。</summary>
    private readonly string dummyHash;

    /// <summary>初始化 AspNetCorePasswordCredentialHasher 的依赖与实例状态。</summary>
    public AspNetCorePasswordCredentialHasher()
    {
        hasher = new PasswordHasher<object>(Microsoft.Extensions.Options.Options.Create(
            new PasswordHasherOptions
        {
            CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3,
            IterationCount = IterationCount,
        }));
        dummyHash = hasher.HashPassword(Subject, "timing-only-password-value");
    }

    /// <summary>使用 ASP.NET Core 密码哈希器生成包含随机盐和版本参数的密码凭据。</summary>
    public string Hash(string password) => hasher.HashPassword(Subject, password);

    /// <summary>验证已存密码哈希并返回匹配及重哈希状态；无凭据时仍用哑哈希执行验证。</summary>
    public PasswordHashVerification Verify(string? passwordHash, string providedPassword)
    {
        var hasStoredCredential = !string.IsNullOrWhiteSpace(passwordHash);
        PasswordVerificationResult result;
        try
        {
            result = hasher.VerifyHashedPassword(
                Subject,
                hasStoredCredential ? passwordHash! : dummyHash,
                providedPassword);
        }
        catch (FormatException)
        {
            return PasswordHashVerification.Failed;
        }

        if (!hasStoredCredential) return PasswordHashVerification.Failed;
        return result switch
        {
            PasswordVerificationResult.Success => PasswordHashVerification.Succeeded,
            PasswordVerificationResult.SuccessRehashNeeded =>
                PasswordHashVerification.SucceededRehashNeeded,
            _ => PasswordHashVerification.Failed,
        };
    }
}
