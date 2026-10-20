using Microsoft.AspNetCore.Mvc;
using CreativeFlow.Api.Http.Handlers;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Contracts.Ai;

namespace CreativeFlow.Api.Controllers;

/// <summary>装配工作流模板的 HTTP 入口及其用例/端口依赖。</summary>
[ApiController]
[Route("api/v1/admin/workflow-templates")]
[Tags("admin")]
public sealed class AdminWorkflowTemplatesController(
    IWorkflowTemplateRepository templates,
    IWorkflowRepository workflows) : ControllerBase
{
    /// <summary>列出工作流模板，保留现有筛选、排序与访问范围。</summary>
    [HttpGet("", Name = "ListAdminWorkflowTemplates")]
    public async Task<IActionResult> List(
        [FromQuery] string? search,
        [FromQuery] string? category,
        [FromQuery] string? status,
        CancellationToken ct) =>
        await WorkflowTemplateHandlers.ListAdminAsync(
            HttpContext,
            templates,
            search,
            category,
            status,
            ct);

    /// <summary>校验输入并创建工作流模板。</summary>
    [HttpPost("", Name = "CreateWorkflowTemplate")]
    public async Task<IActionResult> Create(
        [FromBody] CreateWorkflowTemplateRequest request,
        CancellationToken ct) =>
        await WorkflowTemplateHandlers.CreateAsync(request, HttpContext, templates, ct);

    /// <summary>读取工作流模板，保留缺失资源的既有处理。</summary>
    [HttpGet("{id}", Name = "GetAdminWorkflowTemplate")]
    public async Task<IActionResult> Get([FromRoute] string id, CancellationToken ct) =>
        await WorkflowTemplateHandlers.GetAdminAsync(id, HttpContext, templates, ct);

    /// <summary>更新工作流模板，保留原有访问范围和并发结果。</summary>
    [HttpPut("{id}", Name = "UpdateWorkflowTemplate")]
    public async Task<IActionResult> Update(
        [FromRoute] string id,
        [FromBody] UpdateWorkflowTemplateRequest request,
        CancellationToken ct) =>
        await WorkflowTemplateHandlers.UpdateAsync(id, request, HttpContext, templates, ct);

    /// <summary>删除工作流模板，保留不存在或不可访问时的返回语义。</summary>
    [HttpDelete("{id}", Name = "DeleteWorkflowTemplate")]
    public async Task<IActionResult> Delete([FromRoute] string id, CancellationToken ct) =>
        await WorkflowTemplateHandlers.DeleteAsync(id, HttpContext, templates, ct);

    /// <summary>校验模板内容并将其发布为可实例化的模板。</summary>
    [HttpPost("{id}/publish", Name = "PublishWorkflowTemplate")]
    public async Task<IActionResult> Publish([FromRoute] string id, CancellationToken ct) =>
        await WorkflowTemplateHandlers.PublishAsync(id, HttpContext, templates, ct);

    /// <summary>将工作流模板下架，保留原有历史记录。</summary>
    [HttpPost("{id}/archive", Name = "ArchiveWorkflowTemplate")]
    public async Task<IActionResult> Archive([FromRoute] string id, CancellationToken ct) =>
        await WorkflowTemplateHandlers.ArchiveAsync(id, HttpContext, templates, ct);

    /// <summary>复制工作流模板，按既有规则建立新的草稿。</summary>
    [HttpPost("{id}/duplicate", Name = "DuplicateWorkflowTemplate")]
    public async Task<IActionResult> Duplicate([FromRoute] string id, CancellationToken ct) =>
        await WorkflowTemplateHandlers.DuplicateAsync(id, HttpContext, templates, ct);

    /// <summary>为管理员准备当前模板的调试工作流。</summary>
    [HttpPost("{id}/debug-workflow", Name = "PrepareWorkflowTemplateDebugWorkflow")]
    public async Task<IActionResult> PrepareDebugWorkflow(
        [FromRoute] string id,
        CancellationToken ct) =>
        await WorkflowTemplateHandlers.PrepareDebugWorkflowAsync(
            id,
            HttpContext,
            templates,
            workflows,
            ct);
}
