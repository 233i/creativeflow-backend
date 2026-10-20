using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using CreativeFlow.Api.Http;
using CreativeFlow.Api.Http.Handlers;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Abstractions.Security;
using CreativeFlow.Application.Contracts.Auth;
using CreativeFlow.Application.Features.Identity;

namespace CreativeFlow.Api.Controllers;

/// <summary>装配用户认证的 HTTP 入口及其用例/端口依赖。</summary>
[ApiController]
[Route("api/v1/auth")]
[Tags("auth")]
public sealed class AuthController(
    IExternalLoginProviderRegistry externalProviders,
    IAccountRepository accounts,
    AccountAuthService accountAuth,
    PasswordAuthService passwordAuth,
    IOptions<AuthenticationOptions> authenticationOptions,
    IHostEnvironment environment) : ControllerBase
{
    /// <summary>列出当前已启用的外部身份提供方。</summary>
    [HttpGet("providers", Name = "AuthProviders")]
    public async Task<IActionResult> ListProviders() =>
        await AuthHandlers.ListProvidersAsync(externalProviders);

    /// <summary>读取本次请求已验证的账号或设备信息。</summary>
    [HttpGet("me", Name = "CurrentAccount")]
    public async Task<IActionResult> GetCurrent(CancellationToken ct) =>
        await AuthHandlers.GetCurrentAsync(HttpContext, accounts, ct);

    /// <summary>启动所选外部身份提供方的 OAuth 登录流程。</summary>
    [HttpGet("login/{provider}", Name = "StartExternalLogin")]
    public async Task<IActionResult> Login([FromRoute] string provider, CancellationToken ct) =>
        await AuthHandlers.LoginAsync(
            provider,
            Request,
            HttpContext,
            accountAuth,
            authenticationOptions,
            ct);

    /// <summary>开始将外部身份关联到已登录账号的授权流程。</summary>
    [HttpGet("link/{provider}", Name = "LinkExternalLogin")]
    public async Task<IActionResult> Link([FromRoute] string provider, CancellationToken ct) =>
        await AuthHandlers.LinkAsync(
            provider,
            Request,
            HttpContext,
            accountAuth,
            authenticationOptions,
            ct);

    /// <summary>消费外部授权回调并完成账号会话或身份关联。</summary>
    [HttpGet("callback/{provider}", Name = "ExternalLoginCallback")]
    public async Task<IActionResult> Callback([FromRoute] string provider, CancellationToken ct) =>
        await AuthHandlers.CallbackAsync(
            provider,
            Request,
            HttpContext,
            accountAuth,
            authenticationOptions,
            environment,
            ct);

    /// <summary>通过邮箱密码创建账号并建立会话。</summary>
    [HttpPost("password/register", Name = "RegisterWithPassword")]
    [EnableRateLimiting(ApiRateLimitPolicies.PasswordAuthentication)]
    public async Task<IActionResult> RegisterPassword(
        [FromBody] PasswordRegistrationRequest request,
        CancellationToken ct) =>
        await AuthHandlers.RegisterPasswordAsync(
            request,
            HttpContext,
            passwordAuth,
            environment,
            ct);

    /// <summary>验证邮箱密码并建立账号会话。</summary>
    [HttpPost("password/login", Name = "LoginWithPassword")]
    [EnableRateLimiting(ApiRateLimitPolicies.PasswordAuthentication)]
    public async Task<IActionResult> LoginPassword(
        [FromBody] PasswordLoginRequest request,
        CancellationToken ct) =>
        await AuthHandlers.LoginPasswordAsync(
            request,
            HttpContext,
            passwordAuth,
            environment,
            ct);

    /// <summary>撤销当前会话并清除对应 Cookie。</summary>
    [HttpPost("logout", Name = "Logout")]
    public async Task<IActionResult> Logout(CancellationToken ct) =>
        await AuthHandlers.LogoutAsync(HttpContext, accounts, environment, ct);
}
