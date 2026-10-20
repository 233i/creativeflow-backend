namespace CreativeFlow.Application.Features.Identity;

public sealed class AdminOptions
{
    /// <summary>读取该配置对象时使用的现有配置节名称。</summary>
    public const string SectionName = "Admin";

    /// <summary>初始化独立管理员使用的用户名。</summary>
    public string DefaultUsername { get; init; } = "admin";

    /// <summary>仅用于初始化独立管理员的默认密码配置，必须由部署配置提供。</summary>
    public string DefaultPassword { get; init; } = string.Empty;

    /// <summary>账号或管理员会话 Cookie 的配置名称。</summary>
    public string SessionCookieName { get; init; } = "creativeflow_admin_session";

    /// <summary>管理员会话有效期，单位为小时。</summary>
    public int SessionMaxAgeHours { get; init; } = 12;
}
