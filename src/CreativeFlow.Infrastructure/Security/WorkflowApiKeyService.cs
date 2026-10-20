using System.Security.Cryptography;
using System.Text;
using CreativeFlow.Application.Abstractions.Security;
using CreativeFlow.Infrastructure.Options;

namespace CreativeFlow.Infrastructure.Security;

internal sealed class WorkflowApiKeyService : IWorkflowApiKeyService
{
    /// <summary>用于定位 API Key 记录的公开前缀。</summary>
    private const string KeyPrefix = "cf_live_";
    /// <summary>API Key 用于数据库查找的公开前缀长度。</summary>
    private const int LookupPrefixLength = 20;
    /// <summary>派生工作流 API Key 哈希密钥时使用的用途分隔字节。</summary>
    private static readonly byte[] Context = Encoding.UTF8.GetBytes(
        "creativeflow-workflow-api-key-v1");

    /// <summary>用于计算工作流 API Key 验证摘要的服务端密钥。</summary>
    private readonly byte[] _hashKey;

    /// <summary>初始化 WorkflowApiKeyService 的依赖与实例状态。</summary>
    public WorkflowApiKeyService(ExternalWorkflowApiOptions options, AiOptions aiOptions)
    {
        var configured = string.IsNullOrWhiteSpace(options.ApiKeyHashSecret)
            ? aiOptions.EncryptionKey
            : options.ApiKeyHashSecret;

        byte[] secret;
        try
        {
            secret = Convert.FromBase64String(configured);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                $"{ExternalWorkflowApiOptions.SectionName}:ApiKeyHashSecret must be base64-encoded.",
                exception);
        }

        if (secret.Length < 32)
        {
            throw new InvalidOperationException(
                $"{ExternalWorkflowApiOptions.SectionName}:ApiKeyHashSecret must decode to at least 32 bytes.");
        }

        _hashKey = HMACSHA256.HashData(secret, Context);
        CryptographicOperations.ZeroMemory(secret);
    }

    /// <summary>生成随机外部工作流 API Key，并返回明文、查询前缀及用于持久化的哈希材料。</summary>
    public WorkflowApiKeyMaterial Generate()
    {
        var random = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToBase64String(random)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        CryptographicOperations.ZeroMemory(random);

        var plaintext = KeyPrefix + token;
        return new WorkflowApiKeyMaterial(
            plaintext,
            plaintext[..LookupPrefixLength],
            plaintext[^4..],
            Hash(plaintext));
    }

    /// <summary>仅从格式满足既定前缀和长度要求的 API Key 中读取查询前缀，否则返回空。</summary>
    public string? ReadPrefix(string? plaintext) =>
        plaintext is { Length: >= LookupPrefixLength }
        && plaintext.StartsWith(KeyPrefix, StringComparison.Ordinal)
            ? plaintext[..LookupPrefixLength]
            : null;

    /// <summary>使用服务端哈希密钥为外部 API Key 计算 HMAC-SHA256 摘要。</summary>
    public byte[] Hash(string plaintext) =>
        HMACSHA256.HashData(_hashKey, Encoding.UTF8.GetBytes(plaintext));

    /// <summary>校验期望哈希长度并以固定时间比较 API Key 摘要，避免普通字符串比较泄露匹配信息。</summary>
    public bool Verify(string plaintext, byte[] expectedHash)
    {
        if (expectedHash.Length != 32) return false;
        var actual = Hash(plaintext);
        return CryptographicOperations.FixedTimeEquals(actual, expectedHash);
    }
}
