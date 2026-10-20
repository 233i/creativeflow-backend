using System.Net;

namespace CreativeFlow.Application.Abstractions.Security;

/// <summary>
/// 将客户端地址缩减为网络前缀——IPv4 使用 /24，IPv6 使用 /48。
/// </summary>
/// <remarks>
/// 存储前缀而非完整地址，可在 DHCP 租约变化时保持粗粒度指纹稳定，
/// 同时保留“同一网络”这一信号，并减少保留的个人数据量。
/// </remarks>
public interface IIpPrefixExtractor
{
    /// <summary>按现有隐私粒度截断 IP 地址前缀。</summary>
    string Extract(IPAddress? address);
}
