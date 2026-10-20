using CreativeFlow.Application.Abstractions.Security;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Application.Features.Ai;

/// <summary>将 MCP 秘密值封装为可安全写入 JSON 的认证密文字符串。</summary>
/// <remarks>
/// 为保持现有注册信息可用，旧版无前缀值会被视为明文接受；端点读取时会择机使用
/// <see cref="Protect"/> 重写。前缀使迁移含义明确，也使未来新增密钥格式版本可以向后兼容。
/// </remarks>
public static class McpSecretCodec
{
    /// <summary>用于命名空间隔离、查找或验证的固定前缀。</summary>
    private const string Prefix = "creativeflow-secret:v1:";

    /// <summary>判断 MCP 秘密项是否使用当前受保护格式。</summary>
    public static bool IsProtected(string value) => value.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>将 MCP 秘密编码为带版本前缀的受保护 JSON 表示。</summary>
    public static string Protect(IApiKeyProtector protector, string value)
    {
        var secret = protector.Protect(value);
        return string.Join(':',
            Prefix.TrimEnd(':'),
            Convert.ToBase64String(secret.Cipher),
            Convert.ToBase64String(secret.Nonce),
            Convert.ToBase64String(secret.Tag));
    }

    /// <summary>还原受保护 MCP 秘密，同时保留旧格式配置的兼容读取。</summary>
    public static string Unprotect(IApiKeyProtector protector, string value)
    {
        // 旧格式明文配置仍按既有兼容路径读取；只有识别到受保护格式时才要求完整密文结构并解密。
        if (!IsProtected(value)) return value;

        var parts = value.Split(':');
        if (parts.Length != 5
            || parts[0] != "creativeflow-secret"
            || parts[1] != "v1")
        {
            throw new System.Security.Cryptography.CryptographicException(
                "The stored MCP secret has an invalid format.");
        }

        try
        {
            var secret = new ApiKeySecret(
                Convert.FromBase64String(parts[2]),
                Convert.FromBase64String(parts[3]),
                Convert.FromBase64String(parts[4]),
                "••••");
            return protector.Unprotect(secret);
        }
        catch (FormatException exception)
        {
            throw new System.Security.Cryptography.CryptographicException(
                "The stored MCP secret has an invalid encoding.", exception);
        }
        catch (ArgumentException exception)
        {
            throw new System.Security.Cryptography.CryptographicException(
                "The stored MCP secret has invalid cryptographic parameters.", exception);
        }
    }
}
