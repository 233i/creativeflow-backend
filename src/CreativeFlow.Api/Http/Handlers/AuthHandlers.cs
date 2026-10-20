using Microsoft.Extensions.Options;
using CreativeFlow.Api.Auth;
using CreativeFlow.Api.Hubs;
using CreativeFlow.Api.Middleware;
using CreativeFlow.Api.Serialization;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Abstractions.Security;
using CreativeFlow.Application.Contracts.Auth;
using CreativeFlow.Application.Features.Identity;

namespace CreativeFlow.Api.Http.Handlers;

internal static class AuthHandlers
{
    /// <summary>列出当前已启用的外部身份提供方。</summary>
    internal static Task<IActionResult> ListProvidersAsync(
        IExternalLoginProviderRegistry providers)
    {
        return Task.FromResult(ApiResults.Ok(new AuthProvidersResponse(
        [
            .. providers.ListEnabled().Select(provider =>
                new AuthProviderResponse(provider.Key, provider.DisplayName)),
        ])));
    }

    /// <summary>读取本次请求已验证的账号或设备信息。</summary>
    internal static async Task<IActionResult> GetCurrentAsync(
        HttpContext http,
        IAccountRepository accounts,
        CancellationToken ct)
    {
        var accountId = http.GetAuthenticatedAccountId();
        if (accountId is not { } id)
        {
            return ApiResults.Ok(new AuthMeResponse(true, null));
        }

        var account = await accounts.FindByIdAsync(id, ct);
        return ApiResults.Ok(new AuthMeResponse(
            account is null,
            account is null ? null : ToResponse(account)));
    }

    /// <summary>启动所选外部身份提供方的 OAuth 登录流程。</summary>
    internal static Task<IActionResult> LoginAsync(
        string provider,
        HttpRequest request,
        HttpContext http,
        AccountAuthService auth,
        IOptions<AuthenticationOptions> configuredOptions,
        CancellationToken ct) =>
        BeginAsync(provider, request, http, auth, configuredOptions.Value, null, ct);

    /// <summary>开始将外部身份关联到已登录账号的授权流程。</summary>
    internal static Task<IActionResult> LinkAsync(
        string provider,
        HttpRequest request,
        HttpContext http,
        AccountAuthService auth,
        IOptions<AuthenticationOptions> configuredOptions,
        CancellationToken ct)
    {
        var accountId = http.GetAuthenticatedAccountId();
        return accountId is { } id
            ? BeginAsync(provider, request, http, auth, configuredOptions.Value, id, ct)
            : Task.FromResult<IActionResult>(ApiResults.Unauthorized());
    }

    /// <summary>生成 OAuth 授权地址并写入关联 Cookie，保持既有失败响应。</summary>
    private static async Task<IActionResult> BeginAsync(
        string provider,
        HttpRequest request,
        HttpContext http,
        AccountAuthService auth,
        AuthenticationOptions options,
        CreativeFlow.Domain.Identity.AccountId? linkAccountId,
        CancellationToken ct)
    {
        try
        {
            var callback = CallbackUri(options, provider);
            var returnPath = request.Query["returnUrl"].FirstOrDefault() ?? "/";
            var start = await auth.BeginAsync(
                provider.ToLowerInvariant(),
                callback,
                returnPath,
                DeviceCookie.Read(http),
                linkAccountId,
                ct);

            return ApiResults.Redirect(start.AuthorizationUrl);
        }
        catch (ExternalLoginException exception)
        {
            return RedirectWithError(options, "/", exception.Code);
        }
    }

    /// <summary>消费外部授权回调并完成账号会话或身份关联。</summary>
    internal static async Task<IActionResult> CallbackAsync(
        string provider,
        HttpRequest request,
        HttpContext http,
        AccountAuthService auth,
        IOptions<AuthenticationOptions> configuredOptions,
        IHostEnvironment environment,
        CancellationToken ct)
    {
        // 回调必须同时匹配既有 OAuth 事务与浏览器关联信息，成功后才建立登录或关联结果。
        var options = configuredOptions.Value;
        var error = request.Query["error"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(error))
        {
            return RedirectWithError(options, "/", "provider_denied");
        }

        var code = request.Query["code"].FirstOrDefault();
        var state = request.Query["state"].FirstOrDefault();
        try
        {
            var callback = CallbackUri(options, provider);
            var completion = await auth.CompleteAsync(
                provider.ToLowerInvariant(),
                code ?? string.Empty,
                state ?? string.Empty,
                callback,
                DeviceCookie.Read(http),
                ct);

            AccountSessionCookie.Write(
                http,
                completion.RawSessionToken,
                environment.IsDevelopment());
            return RedirectWithSuccess(options, completion.ReturnPath);
        }
        catch (ExternalLoginException exception)
        {
            return RedirectWithError(options, "/", exception.Code);
        }
    }

    /// <summary>撤销当前会话并清除对应 Cookie。</summary>
    internal static async Task<IActionResult> LogoutAsync(
        HttpContext http,
        IAccountRepository accounts,
        IHostEnvironment environment,
        CancellationToken ct)
    {
        if (AccountSessionCookie.Read(http) is { } rawToken)
        {
            await accounts.RevokeSessionAsync(
                AccountAuthService.HashToken(rawToken),
                ct);
        }

        AccountSessionCookie.Delete(http, environment.IsDevelopment());
        // 已退出登录的账户设备不能静默地继续作为已认证设备。客户端也会移除 localStorage 镜像，
        // 使画布保持不可访问，直到下次成功登录创建并关联新的设备身份。
        DeviceCookie.Delete(http, environment.IsDevelopment());
        return ApiResults.NoContent();
    }

    /// <summary>通过邮箱密码创建账号并建立会话。</summary>
    internal static Task<IActionResult> RegisterPasswordAsync(
        PasswordRegistrationRequest request,
        HttpContext http,
        PasswordAuthService auth,
        IHostEnvironment environment,
        CancellationToken ct)
    {
        if (http.GetAuthenticatedAccountId() is not null)
        {
            return Task.FromResult<IActionResult>(ApiResults.Conflict(
                Error("already_authenticated", "sign out before creating another account")));
        }

        return CompletePasswordAsync(
            auth.RegisterAsync(
                request.Email,
                request.Password,
                request.DisplayName,
                DeviceCookie.Read(http),
                http.Request.Headers.UserAgent.ToString(),
                http.Connection.RemoteIpAddress?.ToString(),
                ct),
            http,
            environment,
            isRegistration: true);
    }

    /// <summary>验证邮箱密码并建立账号会话。</summary>
    internal static Task<IActionResult> LoginPasswordAsync(
        PasswordLoginRequest request,
        HttpContext http,
        PasswordAuthService auth,
        IHostEnvironment environment,
        CancellationToken ct) =>
        CompletePasswordAsync(
            auth.LoginAsync(
                request.Email,
                request.Password,
                DeviceCookie.Read(http),
                http.Request.Headers.UserAgent.ToString(),
                http.Connection.RemoteIpAddress?.ToString(),
                ct),
            http,
            environment,
            isRegistration: false);

    /// <summary>处理密码认证结果并签发既有账号/设备 Cookie。</summary>
    private static async Task<IActionResult> CompletePasswordAsync(
        Task<PasswordAuthResult> pending,
        HttpContext http,
        IHostEnvironment environment,
        bool isRegistration)
    {
        // 注册和登录共享响应与 Cookie 写入路径，失败结果不能留下成功会话 Cookie。
        var result = await pending;
        if (result.Status == PasswordAuthStatus.Succeeded &&
            result.Account is { } account &&
            result.RawSessionToken is { } token)
        {
            AccountSessionCookie.Write(http, token, environment.IsDevelopment());
            return ApiResults.Ok(new AuthMeResponse(false, ToResponse(account)));
        }

        return result.Status switch
        {
            PasswordAuthStatus.InvalidEmail when isRegistration =>
                ApiResults.BadRequest(Error("invalid_email", "enter a valid email address")),
            PasswordAuthStatus.InvalidPassword when isRegistration =>
                ApiResults.BadRequest(Error(
                    "invalid_password",
                    $"password must be {PasswordAuthService.MinimumPasswordLength}-" +
                    $"{PasswordAuthService.MaximumPasswordLength} characters")),
            PasswordAuthStatus.InvalidDisplayName when isRegistration =>
                ApiResults.BadRequest(Error("invalid_display_name", "display name is too long")),
            PasswordAuthStatus.EmailUnavailable when isRegistration =>
                ApiResults.Conflict(Error("email_unavailable", "an account already uses this email")),
            PasswordAuthStatus.DeviceUnavailable =>
                ApiResults.Conflict(Error(
                    "device_unavailable",
                    "this browser device belongs to another account; sign out and try again")),
            PasswordAuthStatus.Disabled => ApiResults.Json(
                Error("account_disabled", "this account has been disabled"),
                ApiJsonContext.Default.ErrorResponse,
                statusCode: StatusCodes.Status403Forbidden),
            _ => ApiResults.Json(
                Error("invalid_credentials", "email or password is incorrect"),
                ApiJsonContext.Default.ErrorResponse,
                statusCode: StatusCodes.Status401Unauthorized),
        };
    }

    /// <summary>将业务记录映射为既有 HTTP 响应字段。</summary>
    private static AuthAccountResponse ToResponse(
        CreativeFlow.Application.Abstractions.Persistence.AccountProfile account) =>
        new(
            account.Id.ToString(),
            account.DisplayName,
            account.Email,
            account.AvatarUrl,
            [.. account.ExternalProviders]);

    /// <summary>按当前功能的既有错误契约包装错误信息。</summary>
    private static ErrorResponse Error(string code, string detail) => new(code, [detail]);

    /// <summary>根据公开 origin 和认证提供方构造回调地址。</summary>
    private static Uri CallbackUri(AuthenticationOptions options, string provider)
    {
        if (!Uri.TryCreate(options.PublicOrigin.TrimEnd('/'), UriKind.Absolute, out var origin) ||
            (origin.Scheme != Uri.UriSchemeHttp && origin.Scheme != Uri.UriSchemeHttps))
        {
            throw new ExternalLoginException("auth_origin_not_configured");
        }

        return new Uri(
            $"{origin.ToString().TrimEnd('/')}/api/v1/auth/callback/{provider.ToLowerInvariant()}");
    }

    /// <summary>按照原有成功参数重定向回站内页面。</summary>
    private static IActionResult RedirectWithSuccess(
        AuthenticationOptions options,
        string returnPath)
    {
        var target = AccountAuthService.NormalizeReturnPath(returnPath);
        return ApiResults.Redirect($"{options.PublicOrigin.TrimEnd('/')}{target}");
    }

    /// <summary>按照原有错误参数重定向回站内页面。</summary>
    private static IActionResult RedirectWithError(
        AuthenticationOptions options,
        string returnPath,
        string code)
    {
        var target = AccountAuthService.NormalizeReturnPath(returnPath);
        var separator = target.Contains('?') ? '&' : '?';
        return ApiResults.Redirect(
            $"{options.PublicOrigin.TrimEnd('/')}{target}{separator}auth_error={Uri.EscapeDataString(code)}");
    }
}
