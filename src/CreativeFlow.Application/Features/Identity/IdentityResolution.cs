using CreativeFlow.Domain.Identity;

namespace CreativeFlow.Application.Features.Identity;

/// <summary>将请求解析为设备和访问者的结果。</summary>
/// <param name="DeviceId">资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</param>
/// <param name="VisitorId">概率性关联的访客标识，不作为账号资源归属依据。</param>
/// <param name="IsNewDevice">本次握手是否创建了新设备记录。</param>
/// <param name="Confidence">设备关联的置信度，不作为账号资源授权凭据。</param>
/// <param name="Method">设备关联、协议请求或通知采用的方法。</param>
public sealed record IdentityResolution(
    DeviceId DeviceId,
    VisitorId VisitorId,
    bool IsNewDevice,
    LinkConfidence Confidence,
    LinkMethod Method)
{
    /// <summary>
    /// 调用方能否使用此身份决定不可逆操作。概率性跨浏览器推测为假，
    /// 这类结果最多只能用于增加验证步骤。
    /// </summary>
    public bool IsTrustworthy => Method != LinkMethod.ProbabilisticCoarse;
}
