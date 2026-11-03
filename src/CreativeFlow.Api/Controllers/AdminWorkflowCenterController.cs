using Microsoft.AspNetCore.Mvc;
using CreativeFlow.Api.Middleware;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Infrastructure.Options;
using CreativeFlow.Api.Http.Handlers;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;
using System.Text.Json.Nodes;

namespace CreativeFlow.Api.Controllers;

/// <summary>以原管理员权限管理统一工作流列表、受保护的软删除及只读历史，不改变资源归属。</summary>
[ApiController]
[Route("api/v1/admin/workflow-center")]
[Tags("admin")]
public sealed class AdminWorkflowCenterController(IWorkflowCenterRepository repository, ExternalWorkflowApiOptions options) : ControllerBase
{
    /// <summary>分页聚合后台工作流与历史模板，拒绝无效条件，不扩大普通用户资源访问范围。</summary>
    [HttpGet("", Name = "AdminWorkflowCenter")]
    public async Task<IActionResult> List([FromQuery] WorkflowCenterQuery query, CancellationToken ct)
    {
        if (HttpContext.GetAuthenticatedAdmin() is null) return Unauthorized();
        if (query.Offset < 0 || query.Limit is not (20 or 50) || query.Search?.Length > 200
            || (!string.IsNullOrWhiteSpace(query.Id) && (!Guid.TryParse(query.Id, out var id) || id == Guid.Empty))
            || !Allowed(query.Kind, "media", "general") || !Allowed(query.Source, "workflow", "template")
            || !Allowed(query.View, "api", "template", "changed", "failed", "deleted")
            || !Allowed(query.ApiStatus, "published", "disabled", "unpublished", "none")
            || !Allowed(query.TemplateStatus, "published", "draft", "archived", "none")
            || query.Sort is not ("updated_desc" or "updated_asc" or "name_asc" or "name_desc")
            || query.UpdatedFrom > query.UpdatedTo)
            return BadRequest(new ErrorResponse("invalid_request", ["工作流筛选条件无效，请检查类型、状态、时间范围和页码。"]));
        Response.Headers.CacheControl = "private, no-store";
        return Ok(await repository.ListAsync(query, options.Enabled, ct));
    }

    /// <summary>预览删除影响；管理员确认前不会修改工作流或取消任何任务。</summary>
    [HttpGet("{source}/{id:guid}/deletion-preview", Name = "AdminWorkflowDeletionPreview")]
    public async Task<IActionResult> PreviewDeletion(string source, Guid id, CancellationToken ct)
    {
        if (HttpContext.GetAuthenticatedAdmin() is null) return Unauthorized();
        if (source is not ("workflow" or "template")) return NotFound();
        Response.Headers.CacheControl = "private, no-store";
        var preview = await repository.PreviewDeletionAsync(source, id, ct);
        return preview is null ? NotFound() : Ok(preview);
    }

    /// <summary>确认名称和版本后原子软删除；前端预览不能替代提交时的状态检查。</summary>
    [HttpDelete("{source}/{id:guid}", Name = "AdminDeleteManagedWorkflow")]
    public async Task<IActionResult> Delete(string source, Guid id, [FromBody] DeleteWorkflowRequest request, CancellationToken ct)
    {
        if (HttpContext.GetAuthenticatedAdmin() is not { } admin) return Unauthorized();
        if (source is not ("workflow" or "template")) return NotFound();
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 200 || string.IsNullOrWhiteSpace(request.Revision) || request.Revision.Length > 32)
            return BadRequest(new ErrorResponse("invalid_request", ["请先读取删除影响，并输入完整工作流名称确认。"]));
        Response.Headers.CacheControl = "private, no-store";
        var result = await repository.DeleteAsync(source, id, admin.Id, request, ct);
        return result.Status switch
        {
            "deleted" => NoContent(),
            "not_found" => NotFound(),
            "changed" => Conflict(new ErrorResponse("workflow_changed", ["工作流已被修改，请重新查询删除影响并确认名称。"])),
            _ => Conflict(new ErrorResponse("workflow_delete_blocked", ["请先下架公共创作、停用已发布 API 和下架模板，并结束所有排队或运行中的任务，再删除工作流。"])),
        };
    }

    /// <summary>可选筛选为空时放行，否则必须精确匹配服务端枚举。</summary>
    private static bool Allowed(string? value, params string[] values) =>
        string.IsNullOrEmpty(value) || values.Contains(value, StringComparer.Ordinal);

    /// <summary>管理员按当前可见来源分页读取全部运行摘要；不扩展节点正文的授权范围。</summary>
    [HttpGet("{source}/{id}/runs", Name = "AdminWorkflowCenterRuns")]
    public async Task<IActionResult> Runs(string source, Guid id, [FromQuery] string? status, [FromQuery] int offset = 0,
        [FromQuery] int limit = 20, CancellationToken ct = default)
    {
        if (HttpContext.GetAuthenticatedAdmin() is null) return Unauthorized();
        if (source is not ("workflow" or "template") || offset < 0 || limit is not (20 or 50)
            || !Allowed(status, "queued", "running", "succeeded", "failed", "cancelled"))
            return BadRequest(new ErrorResponse("invalid_request", ["运行筛选条件无效。"]));
        var resource = await repository.ListAsync(new WorkflowCenterQuery(Id: id.ToString(), Source: source, IncludeDeleted: true), options.Enabled, ct);
        if (resource.Items.Length == 0) return NotFound();
        Response.Headers.CacheControl = "private, no-store";
        return Ok(await repository.ListRunsAsync(source, id, status, offset, limit, ct));
    }

    /// <summary>读取与该来源严格关联的历史图或节点状态，交付前后复验原资源授权。</summary>
    [HttpGet("{source}/{id:guid}/runs/{runId:guid}/{view}", Name = "AdminWorkflowCenterRunSnapshot")]
    public async Task<IActionResult> Run(string source, Guid id, Guid runId, string view,
        [FromServices] IWorkflowRunRepository runs, CancellationToken ct)
    {
        if (HttpContext.GetAuthenticatedAdmin() is null) return Unauthorized();
        Response.Headers.CacheControl = "private, no-store";
        if (source is not ("workflow" or "template") || view is not ("snapshot" or "detail")) return NotFound();
        var resource = await repository.ListAsync(new WorkflowCenterQuery(Id: id.ToString(), Source: source, IncludeDeleted: true), options.Enabled, ct);
        if (resource.Items.Length == 0) return NotFound();
        var scope = await repository.FindRunAsync(source, id, runId, ct);
        if (scope is null) return NotFound();
        var run = scope.TriggerType == "api"
            ? await runs.FindExternalAsync(new WorkflowId(scope.WorkflowId), new WorkflowRunId(runId), ct)
            : scope.TriggerType == "public_creative" ? await runs.FindPublicAsync(new WorkflowId(scope.WorkflowId), new WorkflowRunId(runId), ct)
            : await runs.FindAsync(new DeviceId(scope.DeviceId), new WorkflowId(scope.WorkflowId), new WorkflowRunId(runId), ct);
        if (run is null) return NotFound();
        if (await WorkflowV2Handlers.CheckResourcesAsync(run, HttpContext, ct) is { } denied) return denied;
        IActionResult result = view == "snapshot"
            ? Ok(new JsonObject { ["runId"] = run.Id.ToString(), ["graph"] = JsonNode.Parse(run.GraphJson) })
            : Ok(WorkflowHandlers.ToRunResponse(run, await runs.ListNodesAsync(run.Id, ct)));
        if (await WorkflowV2Handlers.CheckResourcesAsync(run, HttpContext, ct) is { } revoked) return revoked;
        if (await repository.FindRunAsync(source, id, runId, ct) is null) return NotFound();
        return result;
    }
}
