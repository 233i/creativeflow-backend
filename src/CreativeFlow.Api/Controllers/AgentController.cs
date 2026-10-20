using Microsoft.AspNetCore.Mvc;
using CreativeFlow.Api.Http.Handlers;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Contracts.Agent;

namespace CreativeFlow.Api.Controllers;

/// <summary>装配Agent 会话与运行的 HTTP 入口及其用例/端口依赖。</summary>
[ApiController]
[Route("api/v1/agent")]
[Tags("agent")]
public sealed class AgentController(
    ICanvasRepository canvases,
    IAgentRepository agents,
    IProviderRepository providers,
    IAgentRunControl runControl,
    ICanvasAssetRepository assets) : ControllerBase
{
    /// <summary>创建与当前画布或身份关联的会话。</summary>
    [HttpPost("sessions", Name = "CreateAgentSession")]
    public async Task<IActionResult> CreateSession(
        [FromBody] CreateAgentSessionRequest request,
        CancellationToken ct) =>
        await AgentHandlers.CreateSessionAsync(request, HttpContext, canvases, agents, ct);

    /// <summary>查找当前画布对应的 Agent 会话。</summary>
    [HttpGet("sessions", Name = "GetAgentSessionByCanvas")]
    public async Task<IActionResult> GetSessionByCanvas(
        [FromQuery] string canvasId,
        CancellationToken ct) =>
        await AgentHandlers.GetSessionByCanvasAsync(canvasId, HttpContext, agents, ct);

    /// <summary>按会话顺序读取已保存的 Agent 消息。</summary>
    [HttpGet("sessions/{id}/messages", Name = "ListAgentMessages")]
    public async Task<IActionResult> ListMessages([FromRoute] string id, CancellationToken ct) =>
        await AgentHandlers.ListMessagesAsync(id, HttpContext, agents, ct);

    /// <summary>清理指定 Agent 会话的历史消息。</summary>
    [HttpDelete("sessions/{id}/messages", Name = "ClearAgentMessages")]
    public async Task<IActionResult> ClearMessages([FromRoute] string id, CancellationToken ct) =>
        await AgentHandlers.ClearMessagesAsync(id, HttpContext, agents, ct);

    /// <summary>校验输入并创建持久化运行，随后由既有后台执行机制推进。</summary>
    [HttpPost("sessions/{id}/runs", Name = "CreateAgentRun")]
    public async Task<IActionResult> CreateRun(
        [FromRoute] string id,
        [FromBody] CreateAgentRunRequest request,
        CancellationToken ct) =>
        await AgentHandlers.CreateRunAsync(
            id,
            request,
            HttpContext,
            agents,
            providers,
            runControl,
            assets,
            ct);

    /// <summary>读取当前仍在执行或等待的 Agent 运行。</summary>
    [HttpGet("runs/active", Name = "GetActiveAgentRun")]
    public async Task<IActionResult> GetActiveRun(
        [FromQuery] string canvasId,
        CancellationToken ct) =>
        await AgentHandlers.GetActiveRunAsync(canvasId, HttpContext, agents, ct);

    /// <summary>读取指定运行及其当前持久化状态。</summary>
    [HttpGet("runs/{id}", Name = "GetAgentRun")]
    public async Task<IActionResult> GetRun([FromRoute] string id, CancellationToken ct) =>
        await AgentHandlers.GetRunAsync(id, HttpContext, agents, ct);

    /// <summary>按持久序号增量读取 Agent 运行事件。</summary>
    [HttpGet("runs/{id}/events", Name = "ListAgentEvents")]
    public async Task<IActionResult> ListEvents(
        [FromRoute] string id,
        [FromQuery] long? afterSeq,
        CancellationToken ct) =>
        await AgentHandlers.ListEventsAsync(id, HttpContext, agents, ct, afterSeq);

    /// <summary>请求取消指定运行，沿用当前终止状态规则。</summary>
    [HttpDelete("runs/{id}", Name = "CancelAgentRun")]
    public async Task<IActionResult> CancelRun([FromRoute] string id, CancellationToken ct) =>
        await AgentHandlers.CancelRunAsync(id, HttpContext, agents, runControl, ct);

    /// <summary>读取当前可用工具目录。</summary>
    [HttpGet("tools", Name = "ListAgentTools")]
    public IActionResult ListTools() => AgentHandlers.ListTools(HttpContext);
}
