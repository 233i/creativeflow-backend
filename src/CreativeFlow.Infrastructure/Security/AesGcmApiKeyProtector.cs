using System.Security.Cryptography;
using System.Text;
using CreativeFlow.Application.Abstractions.Security;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Infrastructure.Options;

namespace CreativeFlow.Infrastructure.Security;

/// <summary>
/// 使用 AES-256-GCM 保护供应商 API 密钥。
/// </summary>
/// <remarks>
/// 使用 GCM 而非 CBC，因为它在加密的同时还提供认证：被修改的密文会解密失败，
/// 而不会产生看似合理的乱码并作为凭据发送给第三方。
/// <para>
/// 每次加密都会生成新的 96 位随机数，并与密文一同存储。在同一密钥下复用随机数对 GCM 是灾难性的，
/// 会泄露两段明文的异或结果和认证子密钥；因此随机数绝不从记录派生，也不跨写入复用。
/// </para>
/// <para>
/// <see cref="AesGcm"/> 不是线程安全的，因此每次调用都创建新实例，而不缓存在此单例上。
/// 与后续网络往返相比，密钥调度成本很低。
/// </para>
/// </remarks>
internal sealed class AesGcmApiKeyProtector : IApiKeyProtector
{
    /// <summary>当前实例用于加密或签名的密钥材料。</summary>
    private readonly byte[] _key;

    /// <summary>初始化 AesGcmApiKeyProtector 的依赖与实例状态。</summary>
    public AesGcmApiKeyProtector(AiOptions options)
    {
        // 启动时必须取得有效的 32 字节密钥；静默生成新密钥会让已有渠道凭据无法解密。
        var configured = options.EncryptionKey;

        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException(
                $"{AiOptions.SectionName}:EncryptionKey is not configured. Generate one with "
                + "`openssl rand -base64 32`. Rotating it makes every stored API key "
                + "undecryptable, so treat it as durable secret material.");
        }

        byte[] key;
        try
        {
            key = Convert.FromBase64String(configured);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                $"{AiOptions.SectionName}:EncryptionKey must be base64-encoded.", exception);
        }

        if (key.Length != 32)
        {
            throw new InvalidOperationException(
                $"{AiOptions.SectionName}:EncryptionKey must decode to 32 bytes for AES-256; "
                + $"got {key.Length}.");
        }

        _key = key;
    }

    /// <summary>验证非空 API Key 后生成随机 nonce，以 AES-GCM 加密并返回可持久化的密文和认证信息。</summary>
    public ApiKeySecret Protect(string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        var plaintext = Encoding.UTF8.GetBytes(apiKey);
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var cipher = new byte[plaintext.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];

        using var aes = new AesGcm(_key, AesGcm.TagByteSizes.MaxSize);
        aes.Encrypt(nonce, plaintext, cipher, tag);

        CryptographicOperations.ZeroMemory(plaintext);

        return new ApiKeySecret(cipher, nonce, tag, ApiKeySecret.BuildHint(apiKey));
    }

    /// <summary>认证并解密已保存的 API Key；认证标签不匹配时抛出异常，禁止返回未验证的明文。</summary>
    public string Unprotect(ApiKeySecret secret)
    {
        var plaintext = new byte[secret.Cipher.Length];

        using var aes = new AesGcm(_key, AesGcm.TagByteSizes.MaxSize);

        // 标签验证失败时抛出 CryptographicException，这是预期行为：
        // 无法认证的密钥绝不能转发给上游。
        aes.Decrypt(secret.Nonce, secret.Cipher, secret.Tag, plaintext);

        var result = Encoding.UTF8.GetString(plaintext);
        CryptographicOperations.ZeroMemory(plaintext);

        return result;
    }
}
