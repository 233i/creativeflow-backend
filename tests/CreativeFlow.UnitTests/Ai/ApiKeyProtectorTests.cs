using System.Security.Cryptography;
using System.Text;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Infrastructure.Options;
using CreativeFlow.Infrastructure.Security;

namespace CreativeFlow.UnitTests.Ai;

/// <summary>
/// API 密钥保护。
/// </summary>
/// <remarks>
/// 此功能中风险最高的代码：失败要么暴露凭据，要么使所有已存储密钥无法恢复。
/// 随机数唯一性和篡改检测是最重要的两项测试——GCM 随机数复用会泄露两段明文的异或结果和认证子密钥，
/// 未认证密文还可能被替换为指向攻击者控制端点的内容。
/// </remarks>
public class ApiKeyProtectorTests
{
    /// <summary>使用固定值以便复现失败，绝不在测试之外使用。</summary>
    private const string TestKey = "dGVzdC1rZXktZm9yLXVuaXQtdGVzdHMtMzJieXRlcyE=";

    /// <summary>以测试专用密钥构建秘密保护器，隔离真实部署配置。</summary>
    private static AesGcmApiKeyProtector CreateProtector(string? key = null) =>
        new(new AiOptions { EncryptionKey = key ?? TestKey });

    /// <summary>回归场景：Round Trips A Key。</summary>
    [Fact]
    public void RoundTripsAKey()
    {
        var protector = CreateProtector();
        const string original = "synthetic-proj-abcdefghijklmnop1234567890";

        Assert.Equal(original, protector.Unprotect(protector.Protect(original)));
    }

    /// <summary>回归场景：Round Trips Non Ascii Keys。</summary>
    [Fact]
    public void RoundTripsNonAsciiKeys()
    {
        // 部分供应商签发包含非 ASCII 字符的令牌；若混淆字节与字符，
        // 会以只有该供应商用户才会遇到的方式破坏令牌。
        var protector = CreateProtector();
        const string original = "密钥-ключ-🔑-abc";

        Assert.Equal(original, protector.Unprotect(protector.Protect(original)));
    }

    /// <summary>回归场景：Uses A Fresh Nonce For Every Encryption。</summary>
    [Fact]
    public void UsesAFreshNonceForEveryEncryption()
    {
        // GCM 的关键属性。同一密钥下复用随机数会造成灾难，因此即使明文相同也必须保持唯一。
        var protector = CreateProtector();
        const string original = "synthetic-same-key-every-time";

        var nonces = Enumerable
            .Range(0, 50)
            .Select(_ => Convert.ToHexString(protector.Protect(original).Nonce))
            .ToList();

        Assert.Equal(nonces.Count, nonces.Distinct().Count());
    }

    /// <summary>回归场景：Produces Different Ciphertext For Identical Input。</summary>
    [Fact]
    public void ProducesDifferentCiphertextForIdenticalInput()
    {
        var protector = CreateProtector();

        var first = protector.Protect("sk-identical");
        var second = protector.Protect("sk-identical");

        Assert.NotEqual(Convert.ToHexString(first.Cipher), Convert.ToHexString(second.Cipher));
    }

    /// <summary>回归场景：Rejects Tampered Ciphertext。</summary>
    [Fact]
    public void RejectsTamperedCiphertext()
    {
        var protector = CreateProtector();
        var secret = protector.Protect("synthetic-original-value");

        var tampered = secret.Cipher.ToArray();
        tampered[0] ^= 0xFF;

        // 使用 ThrowsAny 而非 Throws：AES-GCM 抛出子类 AuthenticationTagMismatchException。
        // 生产代码捕获基类，因此这才是值得断言的契约。
        Assert.ThrowsAny<CryptographicException>(
            () => protector.Unprotect(secret with { Cipher = tampered }));
    }

    /// <summary>回归场景：Rejects A Tampered Tag。</summary>
    [Fact]
    public void RejectsATamperedTag()
    {
        var protector = CreateProtector();
        var secret = protector.Protect("synthetic-original-value");

        var tag = secret.Tag.ToArray();
        tag[0] ^= 0xFF;

        Assert.ThrowsAny<CryptographicException>(
            () => protector.Unprotect(secret with { Tag = tag }));
    }

    /// <summary>回归场景：Cannot Decrypt With A Different Key。</summary>
    [Fact]
    public void CannotDecryptWithADifferentKey()
    {
        // 密钥轮换的结果：所有已存储密钥均无法恢复。该行为记录在 operations.md 中，
        // 并在此断言，使其后果始终清晰可见。
        var secret = CreateProtector().Protect("synthetic-original-value");

        var other = CreateProtector(
            Convert.ToBase64String(Encoding.UTF8.GetBytes("a-completely-different-key-32byt")));

        Assert.ThrowsAny<CryptographicException>(() => other.Unprotect(secret));
    }

    /// <summary>回归场景：Fails Fast On A Misconfigured Key。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    // 不是 base64。
    [InlineData("this is not base64 !!!")]
    // base64 有效，但长度不符合 AES-256 要求。
    [InlineData("dG9vLXNob3J0")]
    public void FailsFastOnAMisconfiguredKey(string configured)
    {
        // 应在启动时而非首次使用时报错：缺失或格式错误的密钥属于部署错误，
        // 应在任何请求到达应用前暴露。
        Assert.Throws<InvalidOperationException>(() => CreateProtector(configured));
    }

    /// <summary>回归场景：Hint Never Reveals More Than The Last Four Characters。</summary>
    [Theory]
    [InlineData("synthetic-1234567890abcdef", "••••cdef")]
    [InlineData("short", "••••")]
    [InlineData("12345678", "••••")]
    [InlineData("123456789", "••••6789")]
    public void HintNeverRevealsMoreThanTheLastFourCharacters(string key, string expected)
    {
        // 提示是唯一会到达浏览器的部分。
        Assert.Equal(expected, ApiKeySecret.BuildHint(key));
    }

    /// <summary>回归场景：Hint Is Not A Substring Of A Short Key。</summary>
    [Fact]
    public void HintIsNotASubstringOfAShortKey()
    {
        // 若密钥短到四个字符已占较大比例，则完全遮盖而不是部分展示。
        Assert.DoesNotContain("shor", ApiKeySecret.BuildHint("short"));
    }
}
