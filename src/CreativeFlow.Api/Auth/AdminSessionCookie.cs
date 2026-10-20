using CreativeFlow.Application.Features.Identity;

namespace CreativeFlow.Api.Auth;

public static class AdminSessionCookie
{
    /// <summary>启动时配置的 Cookie 名称。</summary>
    private static string cookieName = "creativeflow_admin_session";
    /// <summary>当前管理员 Cookie 的有效小时数。</summary>
    private static int maxAgeHours = 12;

    /// <summary>根据启动配置保存当前 Cookie 的名称和有效期。</summary>
    public static void Configure(AdminOptions options)
    {
        cookieName = options.SessionCookieName;
        maxAgeHours = options.SessionMaxAgeHours;
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
            SameSite = SameSiteMode.Strict,
            Secure = !isDevelopment,
            Path = "/",
            MaxAge = TimeSpan.FromHours(Math.Clamp(maxAgeHours, 1, 168)),
            IsEssential = true,
        });
    }

    /// <summary>使用相同的路径和安全属性清除 Cookie。</summary>
    public static void Delete(HttpContext context, bool isDevelopment)
    {
        context.Response.Cookies.Delete(cookieName, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = !isDevelopment,
            Path = "/",
        });
    }
}
