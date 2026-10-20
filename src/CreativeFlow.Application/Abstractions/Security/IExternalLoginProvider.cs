using CreativeFlow.Application.Abstractions.Persistence;

namespace CreativeFlow.Application.Abstractions.Security;

/// <summary>
/// 与供应商无关的 OAuth 适配器。新增供应商只需新增适配器并显式注册依赖注入；
/// 账户和会话表不包含供应商特有的列。
/// </summary>
public interface IExternalLoginProvider
{
    /// <summary>当前查找、签名或映射所使用的键。</summary>
    string Key { get; }
    /// <summary>面向用户展示的账号名称。</summary>
    string DisplayName { get; }
    /// <summary>对应账号、能力或配置是否启用。</summary>
    bool IsEnabled { get; }

    /// <summary>构造包含原有 state、scope 和 PKCE 参数的外部授权地址。</summary>
    string BuildAuthorizationUrl(
        Uri callbackUri,
        string state,
        string codeChallenge);

    /// <summary>向外部身份提供方交换授权码并读取用户身份。</summary>
    Task<ExternalLoginProfile> ExchangeCodeAsync(
        string code,
        string codeVerifier,
        Uri callbackUri,
        CancellationToken ct);
}

public interface IExternalLoginProviderRegistry
{
    /// <summary>按当前查找键读取已有配置或目录项。</summary>
    IExternalLoginProvider? Find(string key);
    /// <summary>列出已经启用的外部身份提供方。</summary>
    IReadOnlyList<ExternalLoginProviderDescriptor> ListEnabled();
}

/// <param name="Key">当前查找、签名或映射所使用的键。</param>
/// <param name="DisplayName">面向用户展示的账号名称。</param>
public sealed record ExternalLoginProviderDescriptor(string Key, string DisplayName);
