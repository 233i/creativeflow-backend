using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Application.Abstractions.Security;

/// <summary>
/// 加密和解密供应商 API 密钥。
/// </summary>
/// <remarks>
/// 密钥材料绝不会到达浏览器：服务器存储密文，只返回遮盖后的提示，
/// 并且只在向上游请求附加凭据所需的短暂时间内解密。
/// <para>
/// 这只能限制数据库被读取造成的损害，无法防御服务器失陷，因为进程持有加密密钥。
/// 配合设备作用域所有权后，被窃取的备份无法直接使用，但被窃取的设备 Cookie 仍有风险。
/// 这是无账户应用固有的安全上限，并会在设置中向用户说明。
/// </para>
/// </remarks>
public interface IApiKeyProtector
{
    /// <summary>使用既有保护格式加密秘密值，避免持久化或回传明文。</summary>
    ApiKeySecret Protect(string apiKey);

    /// <summary>
    /// 恢复明文密钥。
    /// </summary>
    /// <exception cref="System.Security.Cryptography.CryptographicException">
    /// 密文验证失败——可能已被篡改，或配置的加密密钥发生了变化。调用方应提示“重新输入 API 密钥”，
    /// 而不是显示通用错误，因为任何次数的重试都无法修复该问题。
    /// </exception>
    string Unprotect(ApiKeySecret secret);
}
