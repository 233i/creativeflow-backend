using CreativeFlow.Api.Hubs;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Domain.Identity;

namespace CreativeFlow.Api.Middleware;

/// <summary>
/// 将设备 Cookie 解析到 <c>HttpContext.Items</c> 和日志作用域中。
/// </summary>
/// <remarks>
/// 只读：绝不签发 Cookie 或创建设备。身份只由握手端点创建，因此不带 Cookie 的请求只是不携带身份，
/// 而不会因健康探测或爬虫请求等情况静默创建身份。
/// </remarks>
public sealed class DeviceContextMiddleware(RequestDelegate next, ILogger<DeviceContextMiddleware> logger)
{
    /// <summary>只读解析设备 Cookie 并建立日志范围，不在普通请求中创建设备。</summary>
    public async Task InvokeAsync(HttpContext context, IVisitorRepository visitors)
    {
        if (DeviceCookie.Read(context) is { } deviceId)
        {
            context.Items[DeviceContextItems.DeviceId] = deviceId;

            var visitorId = await visitors.FindVisitorForDeviceAsync(deviceId, context.RequestAborted);
            if (visitorId is { } visitor)
            {
                context.Items[DeviceContextItems.VisitorId] = visitor;
            }

            using (logger.BeginScope(new Dictionary<string, object>
                   {
                       ["device_id"] = deviceId.Value,
                       ["visitor_id"] = visitorId?.Value ?? Guid.Empty,
                   }))
            {
                await next(context);
                return;
            }
        }

        await next(context);
    }
}

public static class DeviceContextMiddlewareExtensions
{
    /// <summary>把只读设备识别中间件加入现有管线。</summary>
    public static IApplicationBuilder UseDeviceContext(this IApplicationBuilder app) =>
        app.UseMiddleware<DeviceContextMiddleware>();

    /// <summary>
    /// 应用端点可用的设备身份。必须存在与关联设备匹配的有效账户会话；
    /// Cookie 本身只是登录和握手流程的连续性线索。
    /// </summary>
    public static DeviceId? GetDeviceId(this HttpContext context) =>
        context.Items[DeviceContextItems.AccountId] is not AccountId ||
        context.Items[DeviceContextItems.DeviceAccessBlocked] is true
            ? null
            : context.Items[DeviceContextItems.DeviceId] is DeviceId deviceId
                ? deviceId
                : null;

    /// <summary>读取本次请求识别出的访客标识，不授予资源所有权。</summary>
    public static VisitorId? GetVisitorId(this HttpContext context) =>
        context.Items[DeviceContextItems.VisitorId] is VisitorId visitorId
            ? visitorId
            : null;

    /// <summary>返回已验证账户会话，即使其新设备尚未关联。</summary>
    public static AccountId? GetAuthenticatedAccountId(this HttpContext context) =>
        context.Items[DeviceContextItems.SessionAccountId] is AccountId accountId
            ? accountId
            : null;

    /// <summary>
    /// 返回仓储使用的账户作用域。仅当当前设备已关联到验证通过的会话账户时才会设置。
    /// </summary>
    public static AccountId? GetAccountId(this HttpContext context) =>
        context.Items[DeviceContextItems.AccountId] is AccountId accountId
            ? accountId
            : null;
}
