namespace CreativeFlow.Application.Features.Identity;

/// <summary>服务器端 OAuth 和账户会话配置。</summary>
public sealed class AuthenticationOptions
{
    /// <summary>读取该配置对象时使用的现有配置节名称。</summary>
    public const string SectionName = "Authentication";

    /// <summary>
    /// 面向客户端的源。OAuth 回调必须使用客户端实际访问的精确源，而不是内部 API 源。
    /// 生产环境中必须显式配置。
    /// </summary>
    public string PublicOrigin { get; set; } = "http://localhost:5298";

    /// <summary>账号或管理员会话 Cookie 的配置名称。</summary>
    public string SessionCookieName { get; set; } = "creativeflow_session";
    /// <summary>账号会话有效期，单位为天。</summary>
    public int SessionMaxAgeDays { get; set; } = 30;
    /// <summary>一次性 OAuth 事务有效期，单位为分钟。</summary>
    public int OAuthTransactionMinutes { get; set; } = 10;

    /// <summary>可选 GitHub OAuth 的配置。</summary>
    public GitHubAuthenticationOptions GitHub { get; set; } = new();
}

public sealed class GitHubAuthenticationOptions
{
    /// <summary>当前配置或资源是否启用。</summary>
    public bool Enabled { get; set; }
    /// <summary>OAuth 应用的公开客户端标识。</summary>
    public string ClientId { get; set; } = string.Empty;
    /// <summary>OAuth 应用的客户端秘密，仅在服务端使用。</summary>
    public string ClientSecret { get; set; } = string.Empty;
    /// <summary>OAuth 请求的 scope 配置。</summary>
    public string Scope { get; set; } = "read:user user:email";
}
