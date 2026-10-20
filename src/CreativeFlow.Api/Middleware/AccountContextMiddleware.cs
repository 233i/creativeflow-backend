using CreativeFlow.Api.Auth;
using CreativeFlow.Api.Hubs;
using CreativeFlow.Application.Abstractions.Identity;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Features.Identity;
using CreativeFlow.Domain.Identity;

namespace CreativeFlow.Api.Middleware;

/// <summary>
/// 验证不透明账户会话，并且只为账户明确拥有的设备启用账户级数据作用域。
/// </summary>
public sealed class AccountContextMiddleware(
    RequestDelegate next,
    ILogger<AccountContextMiddleware> logger)
{
    /// <summary>验证账号 session 与设备归属，仅为匹配的请求设置数据库账号范围。</summary>
    public async Task InvokeAsync(
        HttpContext context,
        IAccountRepository accounts,
        IAccountContext accountContext,
        IHostEnvironment environment)
    {
        var rawToken = AccountSessionCookie.Read(context);
        AccountId? sessionAccountId = null;

        if (rawToken is not null)
        {
            var session = await accounts.FindBySessionTokenHashAsync(
                AccountAuthService.HashToken(rawToken),
                DateTimeOffset.UtcNow,
                context.RequestAborted);

            if (session is not null)
            {
                sessionAccountId = session.Id;
                context.Items[DeviceContextItems.SessionAccountId] = session.Id;
            }
            else
            {
                // 已过期或撤销的 Cookie 没有用途，不应永久发送。
                AccountSessionCookie.Delete(context, environment.IsDevelopment());
            }
        }

        if (DeviceCookie.Read(context) is { } deviceId)
        {
            var deviceOwner = await accounts.FindAccountForDeviceAsync(
                deviceId,
                context.RequestAborted);

            if (deviceOwner is { } owner)
            {
                if (sessionAccountId == owner)
                {
                    accountContext.AccountId = owner;
                    context.Items[DeviceContextItems.AccountId] = owner;
                }
                else
                {
                    // Cookie 仍可用于识别浏览器并保持登录连续性，但设备一旦被账户认领，
                    // 没有匹配的账户会话时绝不能授权业务访问。
                    context.Items[DeviceContextItems.DeviceAccessBlocked] = true;
                    logger.LogDebug(
                        "Blocked account-owned device {DeviceId} without its matching account session.",
                        deviceId.Value);
                }
            }
        }

        await next(context);
    }
}

public static class AccountContextMiddlewareExtensions
{
    /// <summary>把账号会话验证中间件加入现有管线。</summary>
    public static IApplicationBuilder UseAccountContext(this IApplicationBuilder app) =>
        app.UseMiddleware<AccountContextMiddleware>();
}
