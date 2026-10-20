namespace CreativeFlow.Domain.Identity;

/// <summary>设备与访问者建立关联的方式。</summary>
public enum LinkMethod
{
    /// <summary>Cookie 或客户端保存的标识匹配到现有设备，结果可信。</summary>
    Deterministic = 0,

    /// <summary>
    /// 在时间窗口内匹配到不依赖浏览器的指纹和 IP 前缀。这只是线索而非身份判定——
    /// 相关信号仅携带约 10～15 位相关熵。
    /// </summary>
    ProbabilisticCoarse = 1,

    /// <summary>由操作人员建立。</summary>
    Manual = 2,
}
