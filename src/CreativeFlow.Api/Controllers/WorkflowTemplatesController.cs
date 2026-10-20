using Microsoft.AspNetCore.Mvc;
using CreativeFlow.Api.Http.Handlers;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Contracts.Ai;

namespace CreativeFlow.Api.Controllers;

/// <summary>装配工作流模板的 HTTP 入口及其用例/端口依赖。</summary>
[ApiController]
[Route("api/v1/workflow-templates")]
[Tags("workflow-templates")]
public sealed class WorkflowTemplatesController(
    IWorkflowTemplateRepository templates,
    IWorkflowRepository workflows) : ControllerBase
{
    /// <summary>列出工作流模板，保留现有筛选、排序与访问范围。</summary>
    [HttpGet("", Name = "ListWorkflowTemplates")]
    public async Task<IActionResult> List(
        [FromQuery] string? search,
        [FromQuery] string? category,
        CancellationToken ct) =>
        await WorkflowTemplateHandlers.ListPublishedAsync(
            HttpContext,
            templates,
            search,
            category,
            ct);

    /// <summary>读取工作流模板，保留缺失资源的既有处理。</summary>
    [HttpGet("{id}", Name = "GetWorkflowTemplate")]
    public async Task<IActionResult> Get([FromRoute] string id, CancellationToken ct) =>
        await WorkflowTemplateHandlers.GetPublishedAsync(id, HttpContext, templates, ct);

    /// <summary>从已发布模板创建独立工作流并记录使用次数。</summary>
    [HttpPost("{id}/instantiate", Name = "InstantiateWorkflowTemplate")]
    public async Task<IActionResult> Instantiate(
        [FromRoute] string id,
        [FromBody] InstantiateWorkflowTemplateRequest request,
        CancellationToken ct) =>
        await WorkflowTemplateHandlers.InstantiateAsync(
            id,
            request,
            HttpContext,
            templates,
            workflows,
            ct);
}
