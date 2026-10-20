using CreativeFlow.Domain.Identity;

namespace CreativeFlow.Application.Abstractions.Persistence;

public interface IVisitorRepository
{
    /// <summary>创建新的访客身份记录。</summary>
    Task CreateAsync(VisitorId id, DateTimeOffset createdAt, CancellationToken ct);

    /// <summary>添加设备到访问者的关联边，对（设备、访问者）保持幂等。</summary>
    Task LinkAsync(DeviceLink link, CancellationToken ct);

    /// <summary>
    /// 返回设备所属的访问者，并优先选择置信度最高的关联，使确定性关联始终优先于概率性关联。
    /// </summary>
    Task<VisitorId?> FindVisitorForDeviceAsync(DeviceId deviceId, CancellationToken ct);
}
