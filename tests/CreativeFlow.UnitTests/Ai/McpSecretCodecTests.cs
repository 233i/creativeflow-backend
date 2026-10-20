using CreativeFlow.Application.Features.Ai;
using CreativeFlow.Infrastructure.Options;
using CreativeFlow.Infrastructure.Security;

namespace CreativeFlow.UnitTests.Ai;

public class McpSecretCodecTests
{
    /// <summary>当前测试独立使用的合成密钥，不用于真实凭据。</summary>
    private const string TestKey = "dGVzdC1rZXktZm9yLXVuaXQtdGVzdHMtMzJieXRlcyE=";

    /// <summary>回归场景：Protect Produces Authenticated Packed Ciphertext。</summary>
    [Fact]
    public void ProtectProducesAuthenticatedPackedCiphertext()
    {
        var protector = CreateProtector();
        var packed = McpSecretCodec.Protect(protector, "Bearer private-token");

        Assert.StartsWith("creativeflow-secret:v1:", packed);
        Assert.DoesNotContain("private-token", packed, StringComparison.Ordinal);
        Assert.Equal("Bearer private-token", McpSecretCodec.Unprotect(protector, packed));
    }

    /// <summary>回归场景：Legacy Plaintext Remains Readable For Migration。</summary>
    [Fact]
    public void LegacyPlaintextRemainsReadableForMigration()
    {
        var protector = CreateProtector();

        Assert.False(McpSecretCodec.IsProtected("legacy-token"));
        Assert.Equal("legacy-token", McpSecretCodec.Unprotect(protector, "legacy-token"));
    }

    /// <summary>回归场景：Tampered Ciphertext Cannot Be Recovered。</summary>
    [Fact]
    public void TamperedCiphertextCannotBeRecovered()
    {
        var protector = CreateProtector();
        var packed = McpSecretCodec.Protect(protector, "secret");
        var changed = packed[..^2] + "AA";

        Assert.Throws<System.Security.Cryptography.CryptographicException>(
            () => McpSecretCodec.Unprotect(protector, changed));
    }

    /// <summary>以测试专用密钥构建秘密保护器，隔离真实部署配置。</summary>
    private static AesGcmApiKeyProtector CreateProtector() =>
        new(new AiOptions { EncryptionKey = TestKey });
}
