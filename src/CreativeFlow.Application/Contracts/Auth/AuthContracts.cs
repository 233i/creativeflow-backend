namespace CreativeFlow.Application.Contracts.Auth;

/// <param name="Key">当前查找、签名或映射所使用的键。</param>
/// <param name="DisplayName">面向用户展示的账号名称。</param>
public sealed record AuthProviderResponse(string Key, string DisplayName);

/// <param name="Providers">当前可用的渠道或身份提供方集合。</param>
public sealed record AuthProvidersResponse(AuthProviderResponse[] Providers);

/// <param name="Id">当前实体或契约项目的唯一标识。</param>
/// <param name="DisplayName">面向用户展示的账号名称。</param>
/// <param name="Email">账号邮箱地址。</param>
/// <param name="AvatarUrl">账号头像地址。</param>
/// <param name="ExternalProviders">账号已关联的外部身份提供方。</param>
public sealed record AuthAccountResponse(
    string Id,
    string DisplayName,
    string? Email,
    string? AvatarUrl,
    string[] ExternalProviders);

/// <param name="IsGuest">当前身份是否仍处于访客范围。</param>
/// <param name="Account">账号资料。</param>
public sealed record AuthMeResponse(
    bool IsGuest,
    AuthAccountResponse? Account);

/// <param name="Email">账号邮箱地址。</param>
/// <param name="Password">当前认证输入的明文密码，仅用于即时处理。</param>
/// <param name="DisplayName">面向用户展示的账号名称。</param>
public sealed record PasswordRegistrationRequest(
    string? Email,
    string? Password,
    string? DisplayName);

/// <param name="Email">账号邮箱地址。</param>
/// <param name="Password">当前认证输入的明文密码，仅用于即时处理。</param>
public sealed record PasswordLoginRequest(
    string? Email,
    string? Password);
