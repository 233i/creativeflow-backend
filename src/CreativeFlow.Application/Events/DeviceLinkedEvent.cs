namespace CreativeFlow.Application.Events;

/// <summary>
/// 设备与访问者建立关联时触发。使用方执行操作前应读取 <paramref name="Method"/>：
/// 概率性关联只是线索，不是事实。
/// </summary>
/// <param name="DeviceId">资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</param>
/// <param name="VisitorId">概率性关联的访客标识，不作为账号资源归属依据。</param>
/// <param name="Confidence">设备关联的置信度，不作为账号资源授权凭据。</param>
/// <param name="Method">设备关联、协议请求或通知采用的方法。</param>
/// <param name="OccurredAt">集成事件实际发生的时间。</param>
public sealed record DeviceLinkedEvent(
    string DeviceId,
    string VisitorId,
    double Confidence,
    string Method,
    DateTimeOffset OccurredAt)
{
    /// <summary>集成事件发布与消费使用的固定路由键。</summary>
    public const string RoutingKey = "device.linked";
}
