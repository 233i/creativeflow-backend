using CreativeFlow.Application.Features.Identity;

namespace CreativeFlow.Api.Auth;

/// <summary>
/// 浏览器中只存储不透明的随机会话令牌。PostgreSQL 持久化的是令牌的 SHA-256 哈希，
/// 防止数据库泄露直接变成可重放的登录凭据。
/// </summary>
public static class AccountSessionCookie
{
    /// <summary>启动时配置的 Cookie 名称。</summary>
    private static string cookieName = "creativeflow_session";
    /// <summary>当前 Cookie 的有效天数。</summary>
    private static int maxAgeDays = 30;

    /// <summary>根据启动配置保存当前 Cookie 的名称和有效期。</summary>
    public static void Configure(AuthenticationOptions options)
    {
        cookieName = options.SessionCookieName;
        maxAgeDays = options.SessionMaxAgeDays;
    }

    /// <summary>读取并校验当前请求携带的 Cookie 值。</summary>
    public static string? Read(HttpContext context) =>
        context.Request.Cookies.TryGetValue(cookieName, out var raw) &&
        !string.IsNullOrWhiteSpace(raw)
            ? raw
            : null;

    /// <summary>按既有路径、期限和安全属性向响应写入 Cookie。</summary>
    public static void Write(HttpContext context, string token, bool isDevelopment)
    {
        context.Response.Cookies.Append(cookieName, token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = !isDevelopment,
            Path = "/",
            MaxAge = TimeSpan.FromDays(maxAgeDays),
            IsEssential = true,
        });
    }

    /// <summary>使用相同的路径和安全属性清除 Cookie。</summary>
    public static void Delete(HttpContext context, bool isDevelopment)
    {
        context.Response.Cookies.Delete(cookieName, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = !isDevelopment,
            Path = "/",
        });
    }
}
