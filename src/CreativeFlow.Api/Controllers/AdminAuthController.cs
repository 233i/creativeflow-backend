using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using CreativeFlow.Api.Http;
using CreativeFlow.Api.Http.Handlers;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Contracts.Auth;
using CreativeFlow.Application.Features.Identity;

namespace CreativeFlow.Api.Controllers;

/// <summary>装配管理员认证的 HTTP 入口及其用例/端口依赖。</summary>
[ApiController]
[Route("api/v1/admin/auth")]
[Tags("admin")]
public sealed class AdminAuthController(
    AdminAuthService auth,
    IAdminRepository admins,
    IHostEnvironment environment) : ControllerBase
{
    /// <summary>验证对应入口的登录凭据并建立会话。</summary>
    [HttpPost("login", Name = "AdminLogin")]
    [EnableRateLimiting(ApiRateLimitPolicies.PasswordAuthentication)]
    public async Task<IActionResult> Login(
        [FromBody] AdminLoginRequest request,
        CancellationToken ct) =>
        await AdminHandlers.LoginAsync(request, HttpContext, auth, environment, ct);

    /// <summary>读取当前已验证的管理员身份。</summary>
    [HttpGet("me", Name = "CurrentAdmin")]
    public IActionResult Me() => AdminHandlers.MeAsync(HttpContext);

    /// <summary>撤销当前会话并清除对应 Cookie。</summary>
    [HttpPost("logout", Name = "AdminLogout")]
    public async Task<IActionResult> Logout(CancellationToken ct) =>
        await AdminHandlers.LogoutAsync(HttpContext, admins, environment, ct);

    /// <summary>验证当前密码后更新管理员凭据。</summary>
    [HttpPatch("password", Name = "ChangeAdminPassword")]
    [EnableRateLimiting(ApiRateLimitPolicies.PasswordAuthentication)]
    public async Task<IActionResult> ChangePassword(
        [FromBody] AdminPasswordChangeRequest request,
        CancellationToken ct) =>
        await AdminHandlers.ChangePasswordAsync(request, HttpContext, auth, ct);
}
