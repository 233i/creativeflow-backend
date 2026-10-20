using System.Net;
using System.Net.Sockets;
using CreativeFlow.Infrastructure.Options;

namespace CreativeFlow.Infrastructure.Ai.Embeddings;

/// <summary>默认使用精确端点许可；工作流可显式接受公网 HTTPS 通配，所有连接仍重新解析和核验全部 IP。</summary>
/// <param name="options">当前模块独立配置的端点白名单。</param>
/// <param name="isDevelopment">是否允许精确许可的开发回环服务。</param>
/// <param name="allowPublicHttpsWildcard">仅工作流启用，允许白名单中的 https://* 匹配 HTTPS 请求。</param>
/// <param name="allowGeminiSseQuery">仅工作流启用 Gemini 固定流式端点的 alt=sse；完整 URL 仍必须满足端点许可。</param>
internal sealed class EmbeddingEndpointPolicy(EmbeddingOptions options, bool isDevelopment,
    bool allowPublicHttpsWildcard = false, bool allowGeminiSseQuery = false)
{
    /// <summary>校验端点在部署白名单内；仅 Development 可访问白名单中的回环服务。</summary>
    public async Task<IPAddress[]> ResolveAsync(Uri uri, CancellationToken ct)
    {
        ValidateEndpoint(uri);
        var addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, ct);
        ValidateAddresses(uri, addresses);
        return addresses;
    }

    /// <summary>校验完整端点；查询默认禁止，只有工作流的 Gemini SSE 固定参数可例外。</summary>
    internal void ValidateEndpoint(Uri uri)
    {
        var fixedStreamingQuery = uri.IsAbsoluteUri && allowGeminiSseQuery && uri.Query == "?alt=sse"
            && uri.AbsolutePath.Contains("/models/", StringComparison.Ordinal) && uri.AbsolutePath.EndsWith(":streamGenerateContent", StringComparison.Ordinal);
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https")
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 && !fixedStreamingQuery || uri.Fragment.Length != 0
            || (!IsExactEndpointAllowed(uri)
                && !(allowPublicHttpsWildcard && uri.Scheme == "https" && options.AllowedEndpoints.Contains("https://*"))))
            throw new InvalidOperationException("Endpoint is not in the server's allowlist.");
    }

    /// <summary>比较规范化后的完整绝对 URL，通配条目不会被视作本地回环服务的精确授权。</summary>
    private bool IsExactEndpointAllowed(Uri uri) => options.AllowedEndpoints.Any(value =>
        Uri.TryCreate(value, UriKind.Absolute, out var allowed)
        && string.Equals(allowed.AbsoluteUri, uri.AbsoluteUri, StringComparison.Ordinal));

    /// <summary>所有解析结果必须安全，HTTP 与回环只在开发配置明确允许时可用。</summary>
    internal void ValidateAddresses(Uri uri, IReadOnlyList<IPAddress> addresses)
    {
        if (addresses.Count == 0) throw new InvalidOperationException("Embedding host resolved to no addresses.");
        var loopback = addresses.All(address => IPAddress.IsLoopback(
            address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address));
        if (loopback && isDevelopment && IsExactEndpointAllowed(uri)) return;
        if (uri.Scheme != "https" || addresses.Any(IsNonPublic))
            throw new InvalidOperationException("Embedding requests require public HTTPS, or an explicitly allowed development loopback endpoint.");
    }

    /// <summary>拒绝私网、链路本地、保留地址及 IPv6 非全球单播，不变更原 MCP 策略。</summary>
    private static bool IsNonPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) return IsNonPublic(address.MapToIPv4());
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return (bytes[0] & 0xe0) != 0x20 || (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8);
        return bytes[0] is 0 or 10 or 127 or >= 224
            || (bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
            || (bytes[0] == 169 && bytes[1] == 254)
            || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 192 && (bytes[1] == 168 || (bytes[1] == 0 && bytes[2] is 0 or 2)))
            || (bytes[0] == 198 && (bytes[1] is 18 or 19 || (bytes[1] == 51 && bytes[2] == 100)))
            || (bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113);
    }

    /// <summary>连接经过本次 DNS 校验的 IP，避免验证与连接之间再次解析导致 DNS 重绑定。</summary>
    public async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var uri = context.InitialRequestMessage.RequestUri
            ?? throw new InvalidOperationException("Embedding request URI is missing.");
        var addresses = await ResolveAsync(uri, ct);
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException) { socket.Dispose(); }
            catch { socket.Dispose(); throw; }
        }
        throw new HttpRequestException("The configured embedding service could not be reached.");
    }
}
