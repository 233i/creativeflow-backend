using CreativeFlow.Domain.Identity;

namespace CreativeFlow.Application.Abstractions.Caching;

/// <summary>跟踪当前持有实时连接的设备。</summary>
public interface IPresenceTracker
{
    /// <summary>登记指定设备的浏览器连接在线状态。</summary>
    Task MarkOnlineAsync(DeviceId deviceId, string connectionId, CancellationToken ct);

    /// <summary>移除已经断开的浏览器连接在线记录。</summary>
    Task MarkOfflineAsync(DeviceId deviceId, string connectionId, CancellationToken ct);

    /// <summary>判断指定设备当前是否仍有活跃连接。</summary>
    Task<bool> IsOnlineAsync(DeviceId deviceId, CancellationToken ct);

    /// <summary>读取指定设备当前在线连接数。</summary>
    Task<long> ConnectionCountAsync(DeviceId deviceId, CancellationToken ct);
}
