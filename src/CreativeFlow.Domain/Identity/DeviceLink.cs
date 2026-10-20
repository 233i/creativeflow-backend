namespace CreativeFlow.Domain.Identity;

/// <summary>
/// 将设备与访问者关联起来的一条边。
/// </summary>
/// <remarks>
/// 关联以可撤销的边存储，绝不通过破坏性的数据行合并实现。错误的概率性推测应只需一次删除即可撤销，
/// 而不应演变为数据恢复；这一点很重要，因为共享 NAT 出口下，无关用户的粗粒度指纹可能发生冲突。
/// </remarks>
/// <param name="DeviceId">资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</param>
/// <param name="VisitorId">概率性关联的访客标识，不作为账号资源归属依据。</param>
/// <param name="Confidence">设备关联的置信度，不作为账号资源授权凭据。</param>
/// <param name="Method">设备关联、协议请求或通知采用的方法。</param>
/// <param name="CreatedAt">记录创建时间，沿用当前 UTC 与序列化约定。</param>
public sealed record DeviceLink(
    DeviceId DeviceId,
    VisitorId VisitorId,
    LinkConfidence Confidence,
    LinkMethod Method,
    DateTimeOffset CreatedAt);
