namespace CreativeFlow.Application.Contracts.Realtime;

/// <summary>所有服务器到客户端实时消息的信封结构。</summary>
/// <param name="Type">该数据项的协议、值或事件类型，沿用所属契约的取值。</param>
/// <param name="Payload">事件、缓存或存储项的有效载荷。</param>
/// <param name="SentAt">实时消息发送时间。</param>
public sealed record RealtimeEnvelope(
    string Type,
    string Payload,
    DateTimeOffset SentAt);
