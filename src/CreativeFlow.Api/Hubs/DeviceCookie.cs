using Microsoft.Extensions.Options;
using CreativeFlow.Application.Features.Identity;
using CreativeFlow.Domain.Identity;

namespace CreativeFlow.Api.Hubs;

/// <summary>
/// 读写设备 Cookie。
/// </summary>
/// <remarks>
/// Cookie 为 <c>HttpOnly</c>，JavaScript 无法读取；因此握手响应正文也会返回设备标识，
/// 供客户端同步到 localStorage。两个独立通道携带相同值：清除 localStorage 后 Cookie 仍在，
/// 清除 Cookie 后 localStorage 仍在。
/// </remarks>
public static class DeviceCookie
{
    /// <summary>启动时确定的 Cookie 名称。</summary>
    private static string _cookieName = "creativeflow_did";
    /// <summary>Cookie 有效天数。</summary>
    private static int _maxAgeDays = 730;

    /// <summary>根据启动配置保存当前 Cookie 的名称和有效期。</summary>
    public static void Configure(IOptions<DeviceIdentityOptions> options)
    {
        _cookieName = options.Value.CookieName;
        _maxAgeDays = options.Value.CookieMaxAgeDays;
    }

    /// <summary>读取并校验当前请求携带的 Cookie 值。</summary>
    public static DeviceId? Read(HttpContext context) =>
        context.Request.Cookies.TryGetValue(_cookieName, out var raw) &&
        DeviceId.TryParse(raw, out var deviceId)
            ? deviceId
            : null;

    /// <summary>按既有路径、期限和安全属性向响应写入 Cookie。</summary>
    public static void Write(HttpContext context, DeviceId deviceId, bool isDevelopment)
    {
        context.Response.Cookies.Append(_cookieName, deviceId.Value.ToString(), new CookieOptions
        {
            HttpOnly = true,
            // SameSite=Lax 要求 API 与客户端共享同一源。客户端应通过同源反向代理访问 /api 和 /hubs，
            // 因此浏览器只会看到一个源；生产环境中两者位于同一反向代理之后。
            SameSite = SameSiteMode.Lax,
            // 纯 HTTP 会拒绝安全 Cookie，而本地开发正使用纯 HTTP。
            Secure = !isDevelopment,
            Path = "/",
            MaxAge = TimeSpan.FromDays(_maxAgeDays),
            IsEssential = true,
        });
    }

    /// <summary>使用相同的路径和安全属性清除 Cookie。</summary>
    public static void Delete(HttpContext context, bool isDevelopment)
    {
        context.Response.Cookies.Delete(_cookieName, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = !isDevelopment,
            Path = "/",
        });
    }
}
