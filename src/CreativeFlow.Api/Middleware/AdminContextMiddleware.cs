using CreativeFlow.Api.Auth;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Features.Identity;

namespace CreativeFlow.Api.Middleware;

/// <summary>初始化 AdminContextMiddleware 的依赖与实例状态。</summary>
public sealed class AdminContextMiddleware(RequestDelegate next)
{
    /// <summary>读取并验证独立管理员会话，将有效身份写入请求上下文。</summary>
    public async Task InvokeAsync(
        HttpContext context,
        IAdminRepository admins,
        IHostEnvironment environment)
    {
        if (AdminSessionCookie.Read(context) is { } rawToken)
        {
            var admin = await admins.FindBySessionTokenHashAsync(
                AccountAuthService.HashToken(rawToken),
                DateTimeOffset.UtcNow,
                context.RequestAborted);
            if (admin is null)
            {
                AdminSessionCookie.Delete(context, environment.IsDevelopment());
            }
            else
            {
                context.Items[AdminContextItems.Admin] = admin;
            }
        }

        await next(context);
    }
}

public static class AdminContextItems
{
    /// <summary>已验证的管理员资料。</summary>
    public const string Admin = "creativeflow.admin";
}

public static class AdminContextExtensions
{
    /// <summary>把独立管理员会话中间件加入现有管线。</summary>
    public static IApplicationBuilder UseAdminContext(this IApplicationBuilder app) =>
        app.UseMiddleware<AdminContextMiddleware>();

    /// <summary>读取本次请求已经验证的管理员资料。</summary>
    public static AdminAccount? GetAuthenticatedAdmin(this HttpContext context) =>
        context.Items.TryGetValue(AdminContextItems.Admin, out var value)
            ? value as AdminAccount
            : null;
}
