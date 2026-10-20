using Microsoft.AspNetCore.Mvc;
using CreativeFlow.Api.Http.Handlers;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Abstractions.Security;
using CreativeFlow.Application.Contracts.Ai;

namespace CreativeFlow.Api.Controllers;

/// <summary>装配MCP 服务配置的 HTTP 入口及其用例/端口依赖。</summary>
[ApiController]
[Route("api/v1/mcp")]
[Tags("mcp")]
public sealed class McpController(
    IMcpServerRepository servers,
    IApiKeyProtector protector,
    IMcpRuntime runtime) : ControllerBase
{
    /// <summary>列出MCP 服务配置，保留现有筛选、排序与访问范围。</summary>
    [HttpGet("servers", Name = "ListMcpServers")]
    public async Task<IActionResult> List(CancellationToken ct) =>
        await McpHandlers.ListAsync(HttpContext, servers, protector, ct);

    /// <summary>校验输入并创建MCP 服务配置。</summary>
    [HttpPost("servers", Name = "CreateMcpServer")]
    public async Task<IActionResult> Create(
        [FromBody] CreateMcpServerRequest request,
        CancellationToken ct) =>
        await McpHandlers.CreateAsync(request, HttpContext, servers, protector, ct);

    /// <summary>校验并导入MCP 服务配置，保持原有重复项处理策略。</summary>
    [HttpPost("servers/import", Name = "ImportMcpServers")]
    public async Task<IActionResult> Import(
        [FromBody] ImportMcpServersRequest request,
        CancellationToken ct) =>
        await McpHandlers.ImportAsync(request, HttpContext, servers, protector, ct);

    /// <summary>更新MCP 服务配置，保留原有访问范围和并发结果。</summary>
    [HttpPatch("servers/{id}", Name = "UpdateMcpServer")]
    public async Task<IActionResult> Update(
        [FromRoute] string id,
        [FromBody] UpdateMcpServerRequest request,
        CancellationToken ct) =>
        await McpHandlers.UpdateAsync(id, request, HttpContext, servers, protector, ct);

    /// <summary>删除MCP 服务配置，保留不存在或不可访问时的返回语义。</summary>
    [HttpDelete("servers/{id}", Name = "DeleteMcpServer")]
    public async Task<IActionResult> Delete([FromRoute] string id, CancellationToken ct) =>
        await McpHandlers.DeleteAsync(id, HttpContext, servers, ct);

    /// <summary>检查MCP 服务配置对应的上游可用性。</summary>
    [HttpPost("servers/{id}/check", Name = "CheckMcpServer")]
    public async Task<IActionResult> Check([FromRoute] string id, CancellationToken ct) =>
        await McpHandlers.CheckAsync(id, HttpContext, servers, runtime, ct);

    /// <summary>读取当前可用工具目录。</summary>
    [HttpGet("servers/{id}/tools", Name = "ListMcpServerTools")]
    public async Task<IActionResult> ListTools([FromRoute] string id, CancellationToken ct) =>
        await McpHandlers.ListToolsAsync(id, HttpContext, servers, runtime, ct);
}
