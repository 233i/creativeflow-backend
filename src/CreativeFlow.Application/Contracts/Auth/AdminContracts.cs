namespace CreativeFlow.Application.Contracts.Auth;

/// <param name="Username">管理员登录名。</param>
/// <param name="Password">当前认证输入的明文密码，仅用于即时处理。</param>
public sealed record AdminLoginRequest(string? Username, string? Password);

/// <param name="CurrentPassword">用于验证本次改密操作的当前密码，不持久化明文。</param>
/// <param name="NewPassword">本次准备设置的新密码，明文只用于即时校验与哈希。</param>
public sealed record AdminPasswordChangeRequest(string? CurrentPassword, string? NewPassword);

/// <param name="Id">当前实体或契约项目的唯一标识。</param>
/// <param name="Username">管理员登录名。</param>
/// <param name="MustChangePassword">管理员是否必须先替换初始化密码。</param>
public sealed record AdminResponse(string Id, string Username, bool MustChangePassword);

/// <param name="IsAuthenticated">当前会话是否已经完成认证。</param>
/// <param name="Admin">已验证的管理员资料。</param>
public sealed record AdminMeResponse(bool IsAuthenticated, AdminResponse? Admin);

/// <param name="Id">当前实体或契约项目的唯一标识。</param>
/// <param name="DisplayName">面向用户展示的账号名称。</param>
/// <param name="Email">账号邮箱地址。</param>
/// <param name="IsEnabled">对应账号、能力或配置是否启用。</param>
/// <param name="CreatedAt">记录创建时间，沿用当前 UTC 与序列化约定。</param>
public sealed record ManagedUserResponse(
    string Id,
    string DisplayName,
    string? Email,
    bool IsEnabled,
    string CreatedAt);

/// <param name="Users">当前查询返回的受管用户集合。</param>
/// <param name="Total">符合当前查询条件的总数量。</param>
/// <param name="Offset">查询、流或块内的起始偏移。</param>
/// <param name="Limit">本次查询或处理允许返回的最大数量。</param>
public sealed record ManagedUserListResponse(
    ManagedUserResponse[] Users,
    int Total,
    int Offset,
    int Limit);

/// <param name="IsEnabled">对应账号、能力或配置是否启用。</param>
public sealed record SetManagedUserEnabledRequest(bool IsEnabled);
