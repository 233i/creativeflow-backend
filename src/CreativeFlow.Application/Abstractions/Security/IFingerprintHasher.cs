using CreativeFlow.Application.Contracts.Device;

namespace CreativeFlow.Application.Abstractions.Security;

/// <summary>
/// 将原始浏览器信号转换为身份解析器用于匹配的两种指纹。
/// </summary>
public interface IFingerprintHasher
{
    /// <summary>
    /// 对不依赖浏览器的信号及 IP 前缀进行哈希。这是唯一能跨浏览器匹配的指纹；
    /// 由于相关信号携带的熵很少，其可信度较弱。
    /// </summary>
    byte[] ComputeCoarse(StableSignals? stable, string ipPrefix, int signalVersion);

    /// <summary>
    /// 对所有信号进行哈希。值相同可有效识别为同一浏览器配置。
    /// </summary>
    byte[] ComputeExact(StableSignals? stable, VolatileSignals? volatileSignals, int signalVersion);

    /// <summary>规范化信号的标准 JSON，持久化后可在以后重新分析漂移。</summary>
    string CanonicalizeForStorage(DeviceSignalsPayload payload);
}
