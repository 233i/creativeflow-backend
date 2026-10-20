using System.Net;
using System.Net.Sockets;
using CreativeFlow.Application.Abstractions.Security;

namespace CreativeFlow.Infrastructure.Security;

/// <summary>提取用于设备关联的脱敏 IP 网段，避免保存完整来源地址。</summary>
public sealed class IpPrefixExtractor : IIpPrefixExtractor
{
    /// <summary>缺失或不支持的 IP 地址使用的固定前缀占位值。</summary>
    private const string Unknown = "0.0.0.0";

    /// <summary>将 IPv4 或映射 IPv4 缩为 /24、IPv6 缩为 /48 网段；缺失地址使用未知标记。</summary>
    public string Extract(IPAddress? address)
    {
        if (address is null)
        {
            return Unknown;
        }

        // 映射为 IPv6 的 IPv4 地址（::ffff:192.0.2.1）本质上仍是 IPv4；
        // 若按 /48 掩码会保留完整地址，失去缩减意义。
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => MaskIPv4(address),
            AddressFamily.InterNetworkV6 => MaskIPv6(address),
            _ => Unknown,
        };
    }

    /// <summary>/24——保留“同一本地网络”信号，同时能适应 DHCP 租约变化。</summary>
    private static string MaskIPv4(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        bytes[3] = 0;
        return new IPAddress(bytes).ToString();
    }

    /// <summary>
    /// /48——常见的站点分配边界。若范围更窄，会随着 SLAAC 和隐私扩展轮换主机部分而漂移。
    /// </summary>
    private static string MaskIPv6(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        for (var i = 6; i < bytes.Length; i++)
        {
            bytes[i] = 0;
        }

        return new IPAddress(bytes).ToString();
    }
}
