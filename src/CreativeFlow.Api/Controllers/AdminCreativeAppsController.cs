using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using CreativeFlow.Api.Http;
using CreativeFlow.Api.Middleware;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Features.CreativeApps;

namespace CreativeFlow.Api.Controllers;

/// <summary>管理员发布公共创作的入口，与 API 发布版本互相独立。</summary>
[ApiController]
[Route("api/v1/admin/workflow-apis/{id:guid}/creative-app")]
[TypeFilter(typeof(WorkflowApiAccessFilter), Arguments = new object[] { true })]
[Tags("admin")]
public sealed class AdminCreativeAppsController(ICreativeAppRepository apps, CreativeAppService service) : ControllerBase
{
    /// <summary>读取发布历史和当前上架状态。</summary>
    [HttpGet(Name = "GetAdminCreativeApp")]
    public Task<IActionResult> Get(Guid id, CancellationToken ct) => Execute(async () => Ok(await apps.ManagementAsync(id, ct)));
    /// <summary>校验并发布当前媒体草稿为公共创作版本。</summary>
    [HttpPut(Name = "PublishCreativeApp")]
    [RequestSizeLimit(128 * 1024)]
    public Task<IActionResult> Publish(Guid id, [FromBody] JsonObject request, CancellationToken ct) => Execute(async () =>
        Ok(CreativeAppContract.Describe(await service.PublishAsync(id, HttpContext.GetAuthenticatedAdmin()!.Id, request, ct))));
    /// <summary>停止新提交，保留已有任务及历史结果。</summary>
    [HttpDelete(Name = "UnpublishCreativeApp")]
    public Task<IActionResult> Unpublish(Guid id, CancellationToken ct) => Execute(async () =>
        await apps.ActivateAsync(id, null, HttpContext.GetAuthenticatedAdmin()!.Id, ct) ? NoContent() : NotFound());
    /// <summary>检查并重新启用指定历史版本。</summary>
    [HttpPost("versions/{release:guid}/activate", Name = "ActivateCreativeAppVersion")]
    public Task<IActionResult> Activate(Guid id, Guid release, CancellationToken ct) => Execute(async () =>
        await service.ActivateAsync(id, release, HttpContext.GetAuthenticatedAdmin()!.Id, ct) ? NoContent() : NotFound());
    /// <summary>检查管理员身份，只返回明确可公开的发布错误。</summary>
    private async Task<IActionResult> Execute(Func<Task<IActionResult>> action)
    {
        if (HttpContext.GetAuthenticatedAdmin() is null) return Unauthorized();
        try { return await action(); }
        catch (CreativeAppException error) { return StatusCode(error.Status, new JsonObject { ["error"] = error.Code, ["message"] = error.Message }); }
    }
}
