namespace CreativeFlow.Domain.Identity;

/// <summary>
/// 系统对设备与访问者关联的信任程度。
/// </summary>
/// <remarks>
/// 概率性关联被刻意限制在 <see cref="MaxProbabilistic"/>。跨浏览器匹配依赖的信号
/// （时区、CPU 数量、屏幕、操作系统）仅携带约 10～15 位相关熵，因此匹配只能作为线索，不能作为证明。
/// 业务规则应将低于 <see cref="Certain"/> 的结果用于增加验证步骤（验证码、确认），
/// 而不应据此直接允许或拒绝。
/// </remarks>
public readonly record struct LinkConfidence
{
    /// <summary>所有概率性关联的置信度上限。</summary>
    public const double MaxProbabilistic = 0.6d;

    /// <summary>初始化 LinkConfidence 的依赖与实例状态。</summary>
    private LinkConfidence(double value) => Value = value;

    /// <summary>关联置信度，构造时按确定性或概率性关联规则限制。</summary>
    public double Value { get; }

    /// <summary>Cookie 或已存储标识匹配。唯一可用于决定性判断的等级。</summary>
    public static LinkConfidence Certain => new(1.0d);

    /// <summary>Cookie 被清除后恢复了客户端保存的标识，说明是同一浏览器。</summary>
    public static LinkConfidence StorageRestore => new(0.95d);

    /// <summary>粗粒度指纹与 IP 前缀匹配，仅作为线索。</summary>
    public static LinkConfidence Probabilistic => new(MaxProbabilistic);

    /// <summary>按关联方式限制置信度，概率性关联不能超过现有上限。</summary>
    public static LinkConfidence For(LinkMethod method) => method switch
    {
        LinkMethod.Deterministic => Certain,
        LinkMethod.ProbabilisticCoarse => Probabilistic,
        LinkMethod.Manual => Certain,
        _ => Probabilistic,
    };

    /// <summary>
    /// 将任意值限制在有效范围内，并将概率性关联限制在 <see cref="MaxProbabilistic"/>，
    /// 防止调用方把推测夸大为事实。
    /// </summary>
    public static LinkConfidence Create(double value, LinkMethod method)
    {
        var clamped = Math.Clamp(value, 0.01d, 1.0d);

        if (method == LinkMethod.ProbabilisticCoarse && clamped > MaxProbabilistic)
        {
            clamped = MaxProbabilistic;
        }

        return new LinkConfidence(clamped);
    }

    /// <summary>按当前区域格式将关联置信度显示为保留两位小数的字符串。</summary>
    public override string ToString() => Value.ToString("0.00");
}
