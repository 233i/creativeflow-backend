namespace CreativeFlow.Api.Http;

/// <summary>HTTP 交付层使用的命名限流策略。</summary>
public static class ApiRateLimitPolicies
{
    /// <summary>密码认证端点绑定的限流策略名。</summary>
    public const string PasswordAuthentication = "password-auth";
}
