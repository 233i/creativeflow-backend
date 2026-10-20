namespace CreativeFlow.Domain.Identity;

/// <summary>
/// 服务器见过的浏览器配置，以及用于识别它的指纹。
/// </summary>
/// <param name="FingerprintExact">
/// 对所有信号进行哈希，包括依赖浏览器的信号（画布、音频、字体、用户代理）。
/// 共享此值的两条记录几乎可以确定来自同一个浏览器。
/// </param>
/// <param name="FingerprintCoarse">
/// 对不依赖浏览器的信号和 IP 前缀进行哈希。这使跨浏览器推测成为可能，也决定了这种推测可信度较弱。
/// </param>
/// <param name="SignalVersion">
/// 表示生成哈希所使用的规范化方案。提升该版本会使旧的粗粒度匹配失效，
/// 这是浏览器信号可用性变化时预留的调节手段。
/// </param>
/// <param name="Id">当前实体或契约项目的唯一标识。</param>
/// <param name="IpPrefix">用于设备关联的截断 IP 前缀。</param>
/// <param name="SignalsJson">规范化后的设备信号 JSON。</param>
/// <param name="UserAgent">浏览器发送的 User-Agent 信息。</param>
/// <param name="FirstSeen">首次观察到该设备或访客的时间。</param>
/// <param name="LastSeen">最近一次观察到设备或访客的时间。</param>
public sealed record Device(
    DeviceId Id,
    byte[] FingerprintExact,
    byte[] FingerprintCoarse,
    string IpPrefix,
    string SignalsJson,
    int SignalVersion,
    string? UserAgent,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen);
