namespace CreativeFlow.Application.Contracts.Device;

/// <summary>
/// 握手返回的身份信息。
/// </summary>
/// <param name="DeviceId">
/// 同时通过 HttpOnly Cookie 下发。由于 JavaScript 无法读取 HttpOnly Cookie，响应正文会重复此值，
/// 客户端从这里取得并同步到 localStorage。
/// </param>
/// <param name="Confidence">
/// 身份建立方式：Cookie 匹配为 1.0，localStorage 恢复为 0.95，跨浏览器推测为 0.6。
/// 不得使用低于 1.0 的结果作出直接允许或拒绝的决定。
/// </param>
/// <param name="VisitorId">概率性关联的访客标识，不作为账号资源归属依据。</param>
/// <param name="IsNewDevice">本次握手是否创建了新设备记录。</param>
/// <param name="Method">设备关联、协议请求或通知采用的方法。</param>
public sealed record DeviceHandshakeResponse(
    string DeviceId,
    string VisitorId,
    bool IsNewDevice,
    double Confidence,
    string Method);
