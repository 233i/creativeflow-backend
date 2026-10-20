using CreativeFlow.Domain.Identity;

namespace CreativeFlow.Application.Abstractions.Realtime;

/// <summary>
/// 向已连接客户端推送消息。
/// </summary>
/// <remarks>
/// 在组合根而非基础设施层实现，因为适配器需要具体的 Hub 类型；
/// 将 Hub 引入基础设施层会颠倒分层关系。
/// </remarks>
public interface IRealtimeNotifier
{
    /// <summary>向指定设备分组发送现有实时载荷。</summary>
    Task SendToDeviceAsync<T>(DeviceId deviceId, string method, T payload, CancellationToken ct);

    /// <summary>向指定访客分组发送现有实时载荷。</summary>
    Task SendToVisitorAsync<T>(VisitorId visitorId, string method, T payload, CancellationToken ct);

    /// <summary>向当前所有实时连接广播载荷。</summary>
    Task BroadcastAsync<T>(string method, T payload, CancellationToken ct);
}
