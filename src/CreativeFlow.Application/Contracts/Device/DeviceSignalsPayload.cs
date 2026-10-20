namespace CreativeFlow.Application.Contracts.Device;

/// <summary>
/// 浏览器收集并提交到握手端点的信号。
/// </summary>
/// <remarks>
/// 所有字段均可选。强化隐私保护的浏览器（Safari 26 默认保护屏幕参数、hardwareConcurrency、
/// 画布和 WebAudio）仍应生成稳定但熵较低的指纹，而不是报错；因此缺失值会规范化为哨兵值，
/// 而不会导致验证失败。
/// </remarks>
public sealed record DeviceSignalsPayload
{
    /// <summary>客户端采集信号时使用的信号集版本。</summary>
    public int V { get; init; } = 1;

    /// <summary>相对稳定的浏览器设备信号。</summary>
    public StableSignals? Stable { get; init; }

    /// <summary>容易随环境变化的浏览器设备信号。</summary>
    public VolatileSignals? Volatile { get; init; }

    /// <summary>
    /// 之前同步到 localStorage 的设备标识（如有）。使丢失 Cookie 的浏览器可以恢复相同身份。
    /// 真正首次访问时为空值。
    /// </summary>
    public string? ClientStoredId { get; init; }
}

/// <summary>
/// 属于硬件或操作系统而非浏览器的信号，因此可能在同一设备的不同浏览器之间匹配。
/// 这些信号的组合熵较低。
/// </summary>
public sealed record StableSignals
{
    /// <summary>浏览器报告的时区信号。</summary>
    public string? TimeZone { get; init; }
    /// <summary>浏览器报告的平台信号。</summary>
    public string? Platform { get; init; }
    /// <summary>浏览器报告的逻辑处理器数量。</summary>
    public int? HardwareConcurrency { get; init; }
    /// <summary>浏览器报告的设备内存档位，单位为 GB。</summary>
    public double? DeviceMemoryGb { get; init; }
    /// <summary>浏览器报告的屏幕信号。</summary>
    public ScreenSignals? Screen { get; init; }
    /// <summary>浏览器报告的 GPU 信号。</summary>
    public GpuSignals? Gpu { get; init; }
    /// <summary>用于指纹规范化的首选语言。</summary>
    public string? PrimaryLanguage { get; init; }
}

public sealed record ScreenSignals
{
    /// <summary>屏幕宽度信号，单位为像素。</summary>
    public int? W { get; init; }
    /// <summary>屏幕高度信号，单位为像素。</summary>
    public int? H { get; init; }
    /// <summary>屏幕颜色深度信号。</summary>
    public int? ColorDepth { get; init; }
    /// <summary>浏览器报告的设备像素比。</summary>
    public double? Dpr { get; init; }
}

public sealed record GpuSignals
{
    /// <summary>浏览器报告的 GPU 厂商信号。</summary>
    public string? Vendor { get; init; }
    /// <summary>浏览器报告的 GPU 渲染器信号。</summary>
    public string? Renderer { get; init; }
}

/// <summary>
/// 依赖浏览器的信号。虽然熵较高，但不同引擎的字体栅格化和音频渲染方式不同，
/// 因此无法跨浏览器匹配——它们识别的是浏览器，而不是设备。
/// </summary>
public sealed record VolatileSignals
{
    /// <summary>浏览器发送的 User-Agent 信息。</summary>
    public string? UserAgent { get; init; }
    /// <summary>浏览器报告的语言列表。</summary>
    public IReadOnlyList<string>? Languages { get; init; }
    /// <summary>Canvas 指纹信号的摘要。</summary>
    public string? CanvasHash { get; init; }
    /// <summary>音频指纹信号的摘要。</summary>
    public string? AudioHash { get; init; }
    /// <summary>字体指纹信号的摘要。</summary>
    public string? FontsHash { get; init; }
}
