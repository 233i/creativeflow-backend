namespace CreativeFlow.Application.Features.Identity;

/// <summary>设备身份解析的调优配置。</summary>
public sealed class DeviceIdentityOptions
{
    /// <summary>读取该配置对象时使用的现有配置节名称。</summary>
    public const string SectionName = "DeviceIdentity";

    /// <summary>
    /// 混入每个指纹哈希的服务器端秘密，使已存储指纹无法根据公开信号值重新计算，
    /// 也无法与其他系统的数据库匹配。应通过用户秘密或环境变量提供，绝不能写入 appsettings.json。
    /// </summary>
    public string Pepper { get; set; } = string.Empty;

    /// <summary>
    /// 指定哈希所用的规范化方案。信号集变化时提升版本；旧记录保留其原始版本，
    /// 使两种方案可在发布期间共存。
    /// </summary>
    public int SignalVersion { get; set; } = 1;

    /// <summary>候选项必须在多近的时间内出现，才会被视为同一设备。</summary>
    public int CoarseWindowHours { get; set; } = 24;

    /// <summary>
    /// 在匹配被视为无意义之前，可共享同一粗粒度指纹的最大设备数。此上限至关重要：
    /// 不依赖浏览器的信号仅携带约 10～15 位相关熵，因此同一 NAT 后的办公室可能把数十名无关用户
    /// 汇聚到同一（指纹、IP 前缀）组合。若无上限，他们会全部被合并为一个访问者。
    /// 弱指纹只有在足够少见时才允许建立关联。
    /// </summary>
    public int MaxCoarseFanout { get; set; } = 5;

    /// <summary>承载确定性设备标识的 Cookie 名称。</summary>
    public string CookieName { get; set; } = "creativeflow_did";

    /// <summary>设备 Cookie 有效期，单位为天。</summary>
    public int CookieMaxAgeDays { get; set; } = 730;
}
