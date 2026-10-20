using CreativeFlow.Api.Auth;
using CreativeFlow.Api.Middleware;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Contracts.Auth;
using CreativeFlow.Application.Features.Identity;
using CreativeFlow.Domain.Identity;

namespace CreativeFlow.Api.Http.Handlers;

internal static class AdminHandlers
{
    /// <summary>验证对应入口的登录凭据并建立会话。</summary>
    internal static async Task<IActionResult> LoginAsync(
        AdminLoginRequest request,
        HttpContext http,
        AdminAuthService auth,
        IHostEnvironment environment,
        CancellationToken ct)
    {
        var result = await auth.LoginAsync(
            request.Username,
            request.Password,
            http.Request.Headers.UserAgent.ToString(),
            http.Connection.RemoteIpAddress?.ToString(),
            ct);
        if (result.Status != AdminLoginStatus.Succeeded ||
            result.Admin is null ||
            result.RawSessionToken is null)
        {
            return ApiResults.Unauthorized();
        }

        AdminSessionCookie.Write(http, result.RawSessionToken, environment.IsDevelopment());
        return ApiResults.Ok(new AdminMeResponse(true, ToResponse(result.Admin)));
    }

    /// <summary>读取当前已验证的管理员身份。</summary>
    internal static IActionResult MeAsync(HttpContext http)
    {
        var admin = http.GetAuthenticatedAdmin();
        return ApiResults.Ok(new AdminMeResponse(
            admin is not null,
            admin is null ? null : ToResponse(admin)));
    }

    /// <summary>撤销当前会话并清除对应 Cookie。</summary>
    internal static async Task<IActionResult> LogoutAsync(
        HttpContext http,
        IAdminRepository admins,
        IHostEnvironment environment,
        CancellationToken ct)
    {
        if (AdminSessionCookie.Read(http) is { } token)
        {
            await admins.RevokeSessionAsync(AccountAuthService.HashToken(token), ct);
        }

        AdminSessionCookie.Delete(http, environment.IsDevelopment());
        return ApiResults.NoContent();
    }

    /// <summary>验证当前密码后更新管理员凭据。</summary>
    internal static async Task<IActionResult> ChangePasswordAsync(
        AdminPasswordChangeRequest request,
        HttpContext http,
        AdminAuthService auth,
        CancellationToken ct)
    {
        if (http.GetAuthenticatedAdmin() is not { } admin)
        {
            return ApiResults.Unauthorized();
        }

        var status = await auth.ChangePasswordAsync(
            admin.Id,
            request.CurrentPassword,
            request.NewPassword,
            ct);
        return status switch
        {
            AdminPasswordChangeStatus.Succeeded => ApiResults.NoContent(),
            AdminPasswordChangeStatus.InvalidNewPassword => ApiResults.BadRequest(new ErrorResponse(
                "invalid_new_password",
                [
                    $"password must be {AdminAuthService.MinimumPasswordLength}-" +
                    $"{AdminAuthService.MaximumPasswordLength} characters",
                ])),
            _ => ApiResults.BadRequest(new ErrorResponse(
                "invalid_current_password",
                ["the current password is incorrect"])),
        };
    }

    /// <summary>按管理员查询条件返回受管用户列表。</summary>
    internal static async Task<IActionResult> ListUsersAsync(
        HttpContext http,
        IAdminRepository admins,
        string? search,
        int? offset,
        int? limit,
        CancellationToken ct)
    {
        if (http.GetAuthenticatedAdmin() is null)
        {
            return ApiResults.Unauthorized();
        }

        var pageOffset = Math.Max(offset ?? 0, 0);
        var pageLimit = Math.Clamp(limit ?? 50, 1, 100);
        var page = await admins.ListAccountsAsync(search, pageOffset, pageLimit, ct);
        return ApiResults.Ok(new ManagedUserListResponse(
            [.. page.Accounts.Select(account => new ManagedUserResponse(
                account.Id.ToString(),
                account.DisplayName,
                account.Email,
                account.IsEnabled,
                account.CreatedAt.ToString("O")))],
            page.Total,
            pageOffset,
            pageLimit));
    }

    /// <summary>修改受管账号的启用状态。</summary>
    internal static async Task<IActionResult> SetUserEnabledAsync(
        string id,
        SetManagedUserEnabledRequest request,
        HttpContext http,
        IAdminRepository admins,
        CancellationToken ct)
    {
        if (http.GetAuthenticatedAdmin() is null)
        {
            return ApiResults.Unauthorized();
        }

        if (!AccountId.TryParse(id, out var accountId))
        {
            return ApiResults.BadRequest();
        }

        return await admins.SetAccountEnabledAsync(
            accountId,
            request.IsEnabled,
            DateTimeOffset.UtcNow,
            ct)
            ? ApiResults.NoContent()
            : ApiResults.NotFound();
    }

    /// <summary>将业务记录映射为既有 HTTP 响应字段。</summary>
    private static AdminResponse ToResponse(AdminAccount admin) => new(
        admin.Id.ToString(),
        admin.Username,
        admin.MustChangePassword);
}
