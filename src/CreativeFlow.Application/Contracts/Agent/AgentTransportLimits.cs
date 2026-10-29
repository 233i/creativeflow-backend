namespace CreativeFlow.Application.Contracts.Agent;

/// <summary>浏览器画布工具结果及其 SignalR 信封的有界传输额度。</summary>
public static class AgentTransportLimits
{
    /// <summary>与浏览器画布工具一致，结果 JSON 最多 256 KiB UTF-8 字节。</summary>
    public const int ToolResultBytes = 256 * 1024;

    /// <summary>为结果字符串在 Hub JSON 信封中的转义和关联标识预留空间。</summary>
    public const int HubMessageBytes = 1024 * 1024;
}
