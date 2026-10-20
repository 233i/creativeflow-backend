using Microsoft.AspNetCore.SignalR;
using CreativeFlow.Api.Hubs;
using CreativeFlow.Api.Serialization;
using CreativeFlow.Application.Abstractions.Realtime;
using CreativeFlow.Application.Contracts.Agent;
using CreativeFlow.Domain.Identity;

namespace CreativeFlow.Api.Realtime;

/// <summary>
/// 代表应用层发送实时消息。
/// </summary>
/// <remarks>
/// 位于组合根，因为它需要具体的 <see cref="CreativeFlowHub"/> 类型；若放入基础设施层，会把交付层类型向内引入。
/// 使用非强类型形式的 <c>IHubContext&lt;CreativeFlowHub&gt;</c>，与显式维护的客户端方法名契约一致。
/// </remarks>
public sealed class SignalRNotifier(IHubContext<CreativeFlowHub> hub) : IRealtimeNotifier, IAgentRealtimeSink
{
    /// <summary>向指定设备的 SignalR 分组发送原方法名和业务载荷。</summary>
    public Task SendToDeviceAsync<T>(DeviceId deviceId, string method, T payload, CancellationToken ct) =>
        hub.Clients.Group(HubGroups.ForDevice(deviceId)).SendAsync(method, payload, ct);

    /// <summary>向指定访客的 SignalR 分组发送原方法名和业务载荷。</summary>
    public Task SendToVisitorAsync<T>(VisitorId visitorId, string method, T payload, CancellationToken ct) =>
        hub.Clients.Group(HubGroups.ForVisitor(visitorId)).SendAsync(method, payload, ct);

    /// <summary>将指定业务消息广播给当前全部 SignalR 连接。</summary>
    public Task BroadcastAsync<T>(string method, T payload, CancellationToken ct) =>
        hub.Clients.All.SendAsync(method, payload, ct);

    /// <summary>以 AgentEvent 方法向运行所属设备分组发送事件信封。</summary>
    public Task SendEventAsync(
        DeviceId deviceId,
        AgentEventEnvelope payload,
        CancellationToken ct) =>
        hub.Clients.Group(HubGroups.ForDevice(deviceId))
            .SendAsync(AgentRealtimeMethods.AgentEvent, payload, ct);

    /// <summary>仅向指定执行器连接发送画布工具调用，避免同一设备的其他页面重复执行。</summary>
    public Task SendToolCallAsync(
        string connectionId,
        AgentToolCallEnvelope payload,
        CancellationToken ct) =>
        hub.Clients.Client(connectionId)
            .SendAsync(AgentRealtimeMethods.AgentToolCall, payload, ct);

    /// <summary>仅向指定执行器连接发送工具审批请求。</summary>
    public Task SendApprovalRequestAsync(
        string connectionId,
        AgentApprovalEnvelope payload,
        CancellationToken ct) =>
        hub.Clients.Client(connectionId)
            .SendAsync(AgentRealtimeMethods.AgentApprovalRequest, payload, ct);
}
