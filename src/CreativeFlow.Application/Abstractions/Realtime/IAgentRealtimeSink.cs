using CreativeFlow.Application.Contracts.Agent;
using CreativeFlow.Domain.Identity;

namespace CreativeFlow.Application.Abstractions.Realtime;

/// <summary>
/// 发送智能体已知的实时契约，同时不向应用层暴露 SignalR 或开放的泛型负载接口。
/// </summary>
public interface IAgentRealtimeSink
{
    /// <summary>向运行所属设备发送 Agent 事件信封。</summary>
    Task SendEventAsync(DeviceId deviceId, AgentEventEnvelope payload, CancellationToken ct);

    /// <summary>向已绑定的浏览器连接发送画布工具调用。</summary>
    Task SendToolCallAsync(
        string connectionId,
        AgentToolCallEnvelope payload,
        CancellationToken ct);

    /// <summary>向已绑定的浏览器连接发送工具审批请求。</summary>
    Task SendApprovalRequestAsync(
        string connectionId,
        AgentApprovalEnvelope payload,
        CancellationToken ct);
}
