using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CreativeFlow.Application.Abstractions.Common;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Abstractions.Security;
using CreativeFlow.Application.Contracts.Device;
using CreativeFlow.Domain.Identity;

namespace CreativeFlow.Application.Features.Identity;

/// <summary>
/// 将传入请求解析为设备和访问者，首次接触时创建二者。
/// </summary>
/// <remarks>
/// <para>两层解析，严格按以下优先顺序：</para>
/// <list type="number">
/// <item>
/// 确定性——HttpOnly Cookie，或客户端同步到 localStorage 的标识。对再次访问的浏览器准确率约为 99%，
/// 也是业务逻辑唯一应信任的层级。
/// </item>
/// <item>
/// 概率性——使用不依赖浏览器的信号和 IP 前缀，推测新浏览器是否来自同一物理设备。
/// 置信度上限为 0.6。跨浏览器识别无法可靠实现：高熵信号（画布、音频、字体）随引擎而异，
/// 能跨浏览器保留的信号仅携带约 10～15 位相关熵。
/// </item>
/// </list>
/// </remarks>
public sealed class ResolveDeviceIdentity(
    IDeviceRepository devices,
    IVisitorRepository visitors,
    IFingerprintHasher hasher,
    IIpPrefixExtractor ipPrefixes,
    IClock clock,
    IOptions<DeviceIdentityOptions> options,
    ILogger<ResolveDeviceIdentity> logger)
{
    /// <summary>当前组件在启动时取得的配置快照。</summary>
    private readonly DeviceIdentityOptions _options = options.Value;

    /// <summary>按确定性 Cookie 与受限指纹关联规则解析或创建设备身份。</summary>
    public async Task<IdentityResolution> HandleAsync(
        DeviceSignalsPayload payload,
        DeviceId? cookieDeviceId,
        IPAddress? remoteAddress,
        CancellationToken ct)
    {
        var now = clock.UtcNow;

        // --- 第 1a 层：Cookie ---
        if (cookieDeviceId is { } fromCookie)
        {
            var existing = await devices.FindByIdAsync(fromCookie, ct);
            if (existing is not null)
            {
                await devices.TouchAsync(fromCookie, now, ct);
                var visitorId = await visitors.FindVisitorForDeviceAsync(fromCookie, ct)
                                ?? await AttachToNewVisitorAsync(fromCookie, LinkMethod.Deterministic, now, ct);

                return new IdentityResolution(
                    fromCookie, visitorId, IsNewDevice: false, LinkConfidence.Certain, LinkMethod.Deterministic);
            }
        }

        // --- 第 1b 层：客户端保存在 localStorage 中的标识 ---
        if (DeviceId.TryParse(payload.ClientStoredId, out var storedId))
        {
            var restored = await devices.FindByIdAsync(storedId, ct);
            if (restored is not null)
            {
                await devices.TouchAsync(storedId, now, ct);
                var visitorId = await visitors.FindVisitorForDeviceAsync(storedId, ct)
                                ?? await AttachToNewVisitorAsync(storedId, LinkMethod.Deterministic, now, ct);

                return new IdentityResolution(
                    storedId, visitorId, IsNewDevice: false, LinkConfidence.StorageRestore, LinkMethod.Deterministic);
            }
        }

        // --- 未知浏览器：先记录，再尝试以弱关联方式归入已知设备 ---
        var ipPrefix = ipPrefixes.Extract(remoteAddress);
        var coarse = hasher.ComputeCoarse(payload.Stable, ipPrefix, _options.SignalVersion);
        var exact = hasher.ComputeExact(payload.Stable, payload.Volatile, _options.SignalVersion);

        var newDeviceId = DeviceId.New();
        await devices.InsertAsync(
            new Device(
                newDeviceId,
                exact,
                coarse,
                ipPrefix,
                hasher.CanonicalizeForStorage(payload),
                _options.SignalVersion,
                payload.Volatile?.UserAgent,
                now,
                now),
            ct);

        var (visitor, method) = await ResolveVisitorForNewDeviceAsync(coarse, exact, ipPrefix, now, ct);
        await visitors.LinkAsync(
            new DeviceLink(newDeviceId, visitor, LinkConfidence.For(method), method, now), ct);

        return new IdentityResolution(
            newDeviceId, visitor, IsNewDevice: true, LinkConfidence.For(method), method);
    }

    /// <summary>
    /// 判断未识别浏览器是否属于已见过的访问者。存在歧义时始终创建新访问者——
    /// 错误合并远比重复记录危害更大，因为它会悄然把一个人的活动归到另一个人名下。
    /// </summary>
    private async Task<(VisitorId Visitor, LinkMethod Method)> ResolveVisitorForNewDeviceAsync(
        byte[] coarse,
        byte[] exact,
        string ipPrefix,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var since = now.AddHours(-_options.CoarseWindowHours);

        // 比上限多获取一条，以便检测超出扇出上限的集合，而不是将其截断后漏报。
        var candidates = await devices.FindCoarseCandidatesAsync(
            coarse, exact, ipPrefix, _options.SignalVersion, since, _options.MaxCoarseFanout + 1, ct);

        if (candidates.Count == 0)
        {
            return (await CreateVisitorAsync(now, ct), LinkMethod.Deterministic);
        }

        if (candidates.Count > _options.MaxCoarseFanout)
        {
            logger.LogInformation(
                "Coarse fingerprint too common ({Count} devices within {Hours}h on {IpPrefix}); not linking.",
                candidates.Count, _options.CoarseWindowHours, ipPrefix);

            return (await CreateVisitorAsync(now, ct), LinkMethod.Deterministic);
        }

        var distinctVisitors = candidates.Select(c => c.VisitorId).Distinct().ToArray();
        if (distinctVisitors.Length != 1)
        {
            logger.LogInformation(
                "Coarse fingerprint spans {VisitorCount} visitors; ambiguous, not linking.",
                distinctVisitors.Length);

            return (await CreateVisitorAsync(now, ct), LinkMethod.Deterministic);
        }

        logger.LogDebug(
            "Linking new device to visitor {VisitorId} on coarse fingerprint ({Candidates} candidates).",
            distinctVisitors[0], candidates.Count);

        return (new VisitorId(distinctVisitors[0]), LinkMethod.ProbabilisticCoarse);
    }

    /// <summary>创建新的访客记录及其关联信息。</summary>
    private async Task<VisitorId> CreateVisitorAsync(DateTimeOffset now, CancellationToken ct)
    {
        var visitor = VisitorId.New();
        await visitors.CreateAsync(visitor, now, ct);
        return visitor;
    }

    /// <summary>将当前设备明确关联到新创建的访客记录。</summary>
    private async Task<VisitorId> AttachToNewVisitorAsync(
        DeviceId deviceId, LinkMethod method, DateTimeOffset now, CancellationToken ct)
    {
        var visitor = await CreateVisitorAsync(now, ct);
        await visitors.LinkAsync(
            new DeviceLink(deviceId, visitor, LinkConfidence.For(method), method, now), ct);
        return visitor;
    }
}
