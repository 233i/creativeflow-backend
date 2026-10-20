using System.Net;
using System.Net.Sockets;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Infrastructure.Ai.Embeddings;
using CreativeFlow.Infrastructure.Options;

namespace CreativeFlow.Infrastructure.Ai.HttpRequests;

/// <summary>HTTP 查询使用独立白名单，仅动态构建查询值；复用 IP 安全规则而不放宽模型网络策略。</summary>
internal sealed class WorkflowHttpNetwork(string[] allowedEndpoints, bool development)
{
    /// <summary>精确路径白名单和 DNS 地址校验与业务参数分离。</summary>
    private readonly EmbeddingEndpointPolicy policy = new(new EmbeddingOptions { AllowedEndpoints = allowedEndpoints }, development, allowPublicHttpsWildcard: true);

    /// <summary>查询串由受限模板添加，许可比较仍针对管理员固定的无查询端点。</summary>
    internal async Task<IPAddress[]> ResolveAsync(Uri uri, CancellationToken ct)
    {
        if (!uri.IsAbsoluteUri || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0) throw Denied();
        var endpoint = new UriBuilder(uri) { Query = "" }.Uri;
        try { return await policy.ResolveAsync(endpoint, ct); }
        catch (Exception exception) when (exception is InvalidOperationException or SocketException or ArgumentException)
        { throw Denied(); }
    }

    /// <summary>连接本次已验证的 IP，防止 DNS 重绑定；禁用代理、重定向和 Cookie 由注册统一完成。</summary>
    internal async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var addresses = await ResolveAsync(context.InitialRequestMessage.RequestUri ?? throw Denied(), ct);
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
        throw new HttpRequestException("The configured external operation could not be reached.");
    }

    /// <summary>不暴露 DNS、私网地址或管理员的完整白名单。</summary>
    private static WorkflowHttpException Denied() => new("HTTP_ENDPOINT_DENIED", "接口不在服务器访问许可范围内，或解析到了不允许的网络地址。");
}
