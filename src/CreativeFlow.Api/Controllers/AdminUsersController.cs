using Microsoft.AspNetCore.Mvc;
using CreativeFlow.Api.Http.Handlers;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Contracts.Auth;

namespace CreativeFlow.Api.Controllers;

/// <summary>装配用户管理的 HTTP 入口及其用例/端口依赖。</summary>
[ApiController]
[Route("api/v1/admin/users")]
[Tags("admin")]
public sealed class AdminUsersController(IAdminRepository admins) : ControllerBase
{
    /// <summary>列出受管账号，保留现有筛选、排序与访问范围。</summary>
    [HttpGet("", Name = "ListManagedUsers")]
    public async Task<IActionResult> List(
        [FromQuery] string? search,
        [FromQuery] int? offset,
        [FromQuery] int? limit,
        CancellationToken ct) =>
        await AdminHandlers.ListUsersAsync(HttpContext, admins, search, offset, limit, ct);

    /// <summary>按管理员请求修改目标账号的启用状态。</summary>
    [HttpPatch("{id}/enabled", Name = "SetManagedUserEnabled")]
    public async Task<IActionResult> SetEnabled(
        [FromRoute] string id,
        [FromBody] SetManagedUserEnabledRequest request,
        CancellationToken ct) =>
        await AdminHandlers.SetUserEnabledAsync(id, request, HttpContext, admins, ct);
}
