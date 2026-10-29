using System.Text.Json;
using System.Text;
using Microsoft.AspNetCore.SignalR;
using CreativeFlow.Api.Middleware;
using CreativeFlow.Application.Abstractions.Caching;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Abstractions.Realtime;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;
using CreativeFlow.Application.Contracts.Agent;

namespace CreativeFlow.Api.Hubs;

/// <summary>
/// 应用的实时 Hub。
/// </summary>
/// <remarks>
/// 使用普通 <see cref="Hub"/> 与 JSON 协议，客户端方法按名称寻址。名称集中在
/// <see cref="RealtimeMethods"/> 中，避免字面量散落，并让前后端协议审查保持显式。
/// </remarks>
public sealed class CreativeFlowHub(
    IPresenceTracker presence,
    IAgentRepository agents,
    IAgentCanvasBridge canvasBridge,
    ILogger<CreativeFlowHub> logger) : Hub
{
    /// <summary>拒绝没有设备身份的连接；为已握手设备加入消息分组并登记在线状态。</summary>
    public override async Task OnConnectedAsync()
    {
        var context = Context.GetHttpContext();
        var deviceId = context?.GetDeviceId();

        // 没有身份便无法路由消息，也无法归属活动。拒绝连接，使客户端先执行握手。
        if (deviceId is not { } device)
        {
            logger.LogDebug("Rejecting connection {ConnectionId}: no device cookie.", Context.ConnectionId);
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, HubGroups.ForDevice(device));

        if (context?.Items[DeviceContextItems.VisitorId] is VisitorId visitor)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, HubGroups.ForVisitor(visitor));
        }

        await presence.MarkOnlineAsync(device, Context.ConnectionId, Context.ConnectionAborted);
        await base.OnConnectedAsync();
    }

    /// <summary>断线时清除该连接的在线状态、画布执行器和数据库执行器关联，不使用已取消的连接令牌。</summary>
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var context = Context.GetHttpContext();
        if (context is not null && DeviceCookie.Read(context) is { } device)
        {
            // 使用 CancellationToken.None：此处连接令牌已经取消，但仍必须清理在线状态和执行器记录。
            await presence.MarkOfflineAsync(device, Context.ConnectionId, CancellationToken.None);
            _ = canvasBridge.Detach(Context.ConnectionId);
            await agents.ClearExecutorConnectionAsync(
                device, Context.ConnectionId, CancellationToken.None);
        }

        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>校验运行和画布归属后绑定当前浏览器执行连接。</summary>
    public async Task<string> AttachAgentRun(string runId, string canvasId)
    {
        if (ReadDevice() is not { } device
            || !AgentRunId.TryParse(runId, out var parsedRun)
            || !CanvasId.TryParse(canvasId, out var parsedCanvas))
        {
            throw new HubException("Agent run not found.");
        }

        var claimed = await agents.ClaimExecutorConnectionAsync(
            device, parsedRun, parsedCanvas, Context.ConnectionId, Context.ConnectionAborted);
        if (!claimed)
        {
            throw new HubException("Agent run not found.");
        }

        await canvasBridge.AttachAsync(parsedRun, Context.ConnectionId, Context.ConnectionAborted);
        return Context.ConnectionId;
    }

    /// <summary>解除当前浏览器对指定 Agent 运行的执行绑定。</summary>
    public async Task DetachAgentRun(string runId)
    {
        if (ReadDevice() is not { } device || !AgentRunId.TryParse(runId, out var parsedRun))
        {
            return;
        }

        var run = await agents.FindRunAsync(device, parsedRun, Context.ConnectionAborted);
        if (run is null
            || !string.Equals(run.ExecutorConnectionId, Context.ConnectionId, StringComparison.Ordinal))
        {
            return;
        }

        _ = canvasBridge.Detach(Context.ConnectionId);
        await agents.ClearExecutorConnectionAsync(device, Context.ConnectionId, Context.ConnectionAborted);
    }

    /// <summary>校验连接所有权和 JSON 后提交工具结果。</summary>
    public async Task SubmitToolResult(
        string runId,
        string toolUseId,
        string resultJson,
        bool isError)
    {
        if (ReadDevice() is not { } device
            || !AgentRunId.TryParse(runId, out var parsedRun)
            || string.IsNullOrWhiteSpace(toolUseId)
            || Encoding.UTF8.GetByteCount(resultJson) > AgentTransportLimits.ToolResultBytes)
        {
            throw new HubException("Agent tool call not found.");
        }

        try
        {
            using var _ = JsonDocument.Parse(resultJson);
        }
        catch (JsonException)
        {
            throw new HubException("Agent tool result must be valid JSON.");
        }

        var run = await agents.FindRunAsync(device, parsedRun, Context.ConnectionAborted);
        if (run is null
            || !string.Equals(run.ExecutorConnectionId, Context.ConnectionId, StringComparison.Ordinal))
        {
            throw new HubException("Agent tool call not found.");
        }

        _ = canvasBridge.Submit(parsedRun, toolUseId, resultJson, isError);
    }

    /// <summary>校验当前连接或等待项后提交用户审批结果。</summary>
    public async Task SubmitApproval(string runId, string approvalId, bool approved)
    {
        if (ReadDevice() is not { } device
            || !AgentRunId.TryParse(runId, out var parsedRun)
            || string.IsNullOrWhiteSpace(approvalId))
        {
            throw new HubException("Agent approval not found.");
        }

        var run = await agents.FindRunAsync(device, parsedRun, Context.ConnectionAborted);
        if (run is null
            || !string.Equals(run.ExecutorConnectionId, Context.ConnectionId, StringComparison.Ordinal))
        {
            throw new HubException("Agent approval not found.");
        }

        _ = canvasBridge.SubmitApproval(parsedRun, approvalId, approved);
    }

    /// <summary>供客户端验证传输仍存活的往返检查。</summary>
    public Task<string> Ping() => Task.FromResult("pong");

    /// <summary>读取本次 Hub 调用已验证的设备范围。</summary>
    private DeviceId? ReadDevice()
    {
        var context = Context.GetHttpContext();
        return context?.GetDeviceId();
    }

}
