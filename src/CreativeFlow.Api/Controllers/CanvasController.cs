using Microsoft.AspNetCore.Mvc;
using CreativeFlow.Api.Http.Handlers;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Contracts.Ai;

namespace CreativeFlow.Api.Controllers;

/// <summary>装配画布文档的 HTTP 入口及其用例/端口依赖。</summary>
[ApiController]
[Route("api/v1/canvas")]
[Tags("canvas")]
public sealed class CanvasController(ICanvasRepository canvases) : ControllerBase
{
    /// <summary>列出画布文档，保留现有筛选、排序与访问范围。</summary>
    [HttpGet("", Name = "ListCanvases")]
    public async Task<IActionResult> List(CancellationToken ct) =>
        await CanvasHandlers.ListAsync(HttpContext, canvases, ct);

    /// <summary>校验输入并创建画布文档。</summary>
    [HttpPost("", Name = "CreateCanvas")]
    public async Task<IActionResult> Create(
        [FromBody] CreateCanvasRequest request,
        CancellationToken ct) =>
        await CanvasHandlers.CreateAsync(request, HttpContext, canvases, ct);

    /// <summary>读取画布文档，保留缺失资源的既有处理。</summary>
    [HttpGet("{id}", Name = "GetCanvas")]
    public async Task<IActionResult> Get([FromRoute] string id, CancellationToken ct) =>
        await CanvasHandlers.GetAsync(id, HttpContext, canvases, ct);

    /// <summary>更新画布文档，保留原有访问范围和并发结果。</summary>
    [HttpPut("{id}", Name = "UpdateCanvas")]
    public async Task<IActionResult> Update(
        [FromRoute] string id,
        [FromBody] UpdateCanvasRequest request,
        CancellationToken ct) =>
        await CanvasHandlers.UpdateAsync(id, request, HttpContext, canvases, ct);

    /// <summary>删除画布文档，保留不存在或不可访问时的返回语义。</summary>
    [HttpDelete("{id}", Name = "DeleteCanvas")]
    public async Task<IActionResult> Delete([FromRoute] string id, CancellationToken ct) =>
        await CanvasHandlers.DeleteAsync(id, HttpContext, canvases, ct);
}
