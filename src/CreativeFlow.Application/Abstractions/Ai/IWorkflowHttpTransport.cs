using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Persistence;

namespace CreativeFlow.Application.Abstractions.Ai;

/// <summary>按节点配置发送一次有界 HTTP 请求，凭据仅在服务端解密。</summary>
public interface IWorkflowHttpTransport
{
    /// <summary>校验网络目标，不发送业务请求。</summary>
    Task ValidateEndpointAsync(string endpoint, CancellationToken ct);
    /// <summary>发送一次请求并返回脱敏后的响应端口；不自动重试。</summary>
    Task<JsonObject> SendAsync(JsonObject data, JsonObject inputs, HttpCredentialRevision? credential, CancellationToken ct);
}
