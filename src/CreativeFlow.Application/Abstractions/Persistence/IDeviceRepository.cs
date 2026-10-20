using CreativeFlow.Domain.Identity;

namespace CreativeFlow.Application.Abstractions.Persistence;

public interface IDeviceRepository
{
    /// <summary>按唯一标识读取对应记录，未找到时返回空值。</summary>
    Task<Device?> FindByIdAsync(DeviceId id, CancellationToken ct);

    /// <summary>按原有存储规则写入设备身份。</summary>
    Task InsertAsync(Device device, CancellationToken ct);

    /// <summary>更新设备最近活动与当前信号信息。</summary>
    Task TouchAsync(DeviceId id, DateTimeOffset seenAt, CancellationToken ct);

    /// <summary>
    /// 查找可能是同一物理设备但来自不同浏览器的设备。
    /// </summary>
    /// <remarks>
    /// 调用方必须检查完整结果，而非只看第一行。小候选集中只有一个不同访问者，是同一设备的弱线索；
    /// 候选集很大则说明粗粒度指纹十分常见（共享 NAT 出口、相同的公司电脑），此时绝不能建立关联。
    /// 排除 <c>fp_exact</c> 与调用方相同的记录——完全相同的精确指纹表示同一浏览器，
    /// 而该浏览器本应提供 Cookie。
    /// </remarks>
    Task<IReadOnlyList<CoarseCandidate>> FindCoarseCandidatesAsync(
        byte[] fingerprintCoarse,
        byte[] excludeFingerprintExact,
        string ipPrefix,
        int signalVersion,
        DateTimeOffset seenSince,
        int limit,
        CancellationToken ct);
}

/// <summary>共享粗粒度指纹的设备，以及它当前所属的访问者。</summary>
/// <param name="DeviceId">资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</param>
/// <param name="VisitorId">概率性关联的访客标识，不作为账号资源归属依据。</param>
public sealed record CoarseCandidate(Guid DeviceId, Guid VisitorId);
