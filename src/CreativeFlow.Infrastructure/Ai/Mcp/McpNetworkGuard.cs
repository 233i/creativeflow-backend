using System.Net;
using System.Net.Sockets;
using CreativeFlow.Infrastructure.Options;

namespace CreativeFlow.Infrastructure.Ai.Mcp;

internal interface IMcpNetworkGuard
{
    /// <summary>在建立 MCP 传输前验证目标地址是否满足既有网络访问规则。</summary>
    ValueTask<IReadOnlyList<IPAddress>> ValidateAsync(Uri uri, CancellationToken ct);
}

/// <summary>拒绝可访问本地基础设施或在验证后改变地址的 MCP URL。</summary>
internal sealed class McpNetworkGuard(McpOptions options) : IMcpNetworkGuard
{
    /// <summary>校验 MCP URL 与全部 DNS 结果，拒绝非允许的内网、保留地址和明文 HTTP，并返回经检查的地址列表。</summary>
    public async ValueTask<IReadOnlyList<IPAddress>> ValidateAsync(Uri uri, CancellationToken ct)
    {
        // 校验全部 DNS 结果，避免同一主机混入内网地址；开发期回环 HTTP 仅受既有显式配置放行。
        if (!uri.IsAbsoluteUri || uri.UserInfo.Length > 0)
        {
            throw new InvalidOperationException("MCP URL must be absolute and cannot contain user info.");
        }
        if (uri.Scheme is not ("https" or "http"))
        {
            throw new InvalidOperationException("MCP URL must use HTTPS.");
        }

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, ct);
        }
        catch (SocketException exception)
        {
            throw new InvalidOperationException("The MCP host could not be resolved.", exception);
        }
        if (addresses.Length == 0)
        {
            throw new InvalidOperationException("The MCP host resolved to no addresses.");
        }

        var allLoopback = addresses.All(IPAddress.IsLoopback);
        if (uri.Scheme == "http" && (!options.AllowLoopbackHttp || !allLoopback))
        {
            throw new InvalidOperationException(
                "Plain HTTP MCP is allowed only for loopback hosts in development.");
        }

        if (!allLoopback && addresses.Any(IsForbidden))
        {
            throw new InvalidOperationException("The MCP host resolves to a private or reserved address.");
        }
        if (allLoopback && !options.AllowLoopbackHttp)
        {
            throw new InvalidOperationException("Loopback MCP is disabled outside development.");
        }

        return addresses;
    }

    /// <summary>判断目标 IP 是否落在既有禁止访问的网络范围。</summary>
    internal static bool IsForbidden(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) return IsForbidden(address.MapToIPv4());
        if (IPAddress.IsLoopback(address)) return true;
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.None) || address.Equals(IPAddress.IPv6None)) return true;

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] switch
            {
                0 or 10 or 127 => true,
                100 when bytes[1] is >= 64 and <= 127 => true, // 运营商级 NAT（CGNAT）
                169 when bytes[1] == 254 => true,
                172 when bytes[1] is >= 16 and <= 31 => true,
                192 when bytes[1] == 168 => true,
                >= 224 => true, // 组播及保留地址
                _ => false,
            };
        }

        // fc00::/7 为唯一本地地址，fe80::/10 为链路本地地址，ff00::/8 为多播地址。
        return (bytes[0] & 0xfe) == 0xfc
            || (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80)
            || bytes[0] == 0xff;
    }
}
