using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using CreativeFlow.Application.Abstractions.Security;
using CreativeFlow.Application.Contracts.Device;
using CreativeFlow.Application.Features.Identity;

namespace CreativeFlow.Infrastructure.Security;

/// <summary>
/// 规范化浏览器信号，并将其哈希为粗粒度指纹和精确指纹。
/// </summary>
/// <remarks>
/// 哈希前先进行量化。若不量化，任何抖动——窗口缩放改变屏幕参数、小数形式的 devicePixelRatio、
/// GPU 字符串中的驱动版本——都会产生不同哈希，使设备每次访问都像全新设备。
/// 量化以少量熵换取稳定性，对识别目标而言是正确权衡。
/// </remarks>
public sealed class Sha256FingerprintHasher(IOptions<DeviceIdentityOptions> options) : IFingerprintHasher
{
    /// <summary>指纹规范化过程中表示信号缺失的固定占位值。</summary>
    private const string Missing = "?";

    /// <summary>设备内存信号的量化档位。</summary>
    private static readonly double[] MemoryBuckets = [0.25, 0.5, 1, 2, 4, 8, 16, 32, 64];
    /// <summary>设备逻辑处理器数量的量化档位。</summary>
    private static readonly int[] CoreBuckets = [1, 2, 4, 8, 12, 16, 24, 32, 64];

    /// <summary>当前组件在启动时取得的配置快照。</summary>
    private readonly DeviceIdentityOptions _options = options.Value;

    /// <summary>将稳定设备信号、信号版本与 IP 前缀规范化后计算粗粒度指纹。</summary>
    public byte[] ComputeCoarse(StableSignals? stable, string ipPrefix, int signalVersion)
    {
        var canonical = new StringBuilder();
        canonical.Append('v').Append(signalVersion).Append('|');
        AppendStable(canonical, stable);
        canonical.Append("|ip=").Append(ipPrefix);
        return Hash(canonical.ToString());
    }

    /// <summary>将稳定及易变设备信号与信号版本规范化后计算精确指纹。</summary>
    public byte[] ComputeExact(StableSignals? stable, VolatileSignals? volatileSignals, int signalVersion)
    {
        var canonical = new StringBuilder();
        canonical.Append('v').Append(signalVersion).Append('|');
        AppendStable(canonical, stable);
        canonical.Append('|');
        AppendVolatile(canonical, volatileSignals);
        return Hash(canonical.ToString());
    }

    /// <summary>将各类原始信号规范化为可存储的 JSON，保留未来按新版本重新派生指纹所需的信息。</summary>
    public string CanonicalizeForStorage(DeviceSignalsPayload payload)
    {
        // 各组成部分会单独存储，而不是只存哈希，使未来的信号集版本能根据历史重新派生指纹，
        // 而不必丢失所有现有设备。
        var stable = new StringBuilder();
        AppendStable(stable, payload.Stable);

        var vol = new StringBuilder();
        AppendVolatile(vol, payload.Volatile);

        return $$"""
                 {"v":{{payload.V}},"stable":"{{Escape(stable.ToString())}}","volatile":"{{Escape(vol.ToString())}}"}
                 """;
    }

    /// <summary>
    /// 按固定顺序排列并已量化的不依赖浏览器信号。
    /// </summary>
    private static void AppendStable(StringBuilder sb, StableSignals? s)
    {
        // 使用序号和固定键顺序形成标准形式，使相同输入始终得到相同哈希。
        sb.Append("tz=").Append(Normalize(s?.TimeZone));
        sb.Append("|pf=").Append(Normalize(s?.Platform));
        sb.Append("|hc=").Append(Bucket(s?.HardwareConcurrency, CoreBuckets));
        sb.Append("|dm=").Append(Bucket(s?.DeviceMemoryGb, MemoryBuckets));
        sb.Append("|sc=").Append(NormalizeScreen(s?.Screen));
        sb.Append("|gv=").Append(Normalize(s?.Gpu?.Vendor));
        sb.Append("|gr=").Append(NormalizeRenderer(s?.Gpu?.Renderer));
        sb.Append("|ln=").Append(PrimaryLanguage(s?.PrimaryLanguage));
    }

    /// <summary>按既有粒度向指纹输入追加容易变化的信号。</summary>
    private static void AppendVolatile(StringBuilder sb, VolatileSignals? v)
    {
        sb.Append("ua=").Append(Normalize(v?.UserAgent));
        sb.Append("|cv=").Append(Normalize(v?.CanvasHash));
        sb.Append("|au=").Append(Normalize(v?.AudioHash));
        sb.Append("|fn=").Append(Normalize(v?.FontsHash));
        sb.Append("|lg=").Append(v?.Languages is { Count: > 0 } langs
            ? string.Join(',', langs.Take(8).Select(l => l.Trim().ToLowerInvariant()))
            : Missing);
    }

    /// <summary>
    /// 与方向无关的屏幕签名：手机旋转不能被识别为新设备，
    /// 因此始终先输出较长边，并完全忽略旋转方向。
    /// </summary>
    private static string NormalizeScreen(ScreenSignals? screen)
    {
        if (screen?.W is not { } w || screen.H is not { } h)
        {
            return Missing;
        }

        var major = Math.Max(w, h);
        var minor = Math.Min(w, h);
        var depth = screen.ColorDepth ?? 0;
        // 保留一位小数：浏览器在不同缩放级别报告的 devicePixelRatio 带有浮点噪声。
        var dpr = screen.Dpr is { } d ? Math.Round(d, 1).ToString("0.0") : Missing;

        return $"{major}x{minor}x{depth}@{dpr}";
    }

    /// <summary>
    /// 从 GPU 渲染器字符串中移除驱动和版本号，保留型号标记。
    /// 同一显卡在不同浏览器和驱动更新后报告内容不同，因此原始字符串过于脆弱，不能直接哈希。
    /// </summary>
    private static string NormalizeRenderer(string? renderer)
    {
        if (string.IsNullOrWhiteSpace(renderer))
        {
            return Missing;
        }

        var tokens = renderer
            .ToLowerInvariant()
            .Split([' ', ',', '(', ')', '/', '-'], StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !t.Any(char.IsDigit) || t.Length <= 4)
            .Take(6);

        var result = string.Join('_', tokens);
        return result.Length == 0 ? Missing : result;
    }

    /// <summary>只保留主要子标签：zh-CN 和 zh-TW 不应把同一设备拆成两个。</summary>
    private static string PrimaryLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return Missing;
        }

        var trimmed = language.Trim().ToLowerInvariant();
        var dash = trimmed.IndexOf('-');
        return dash > 0 ? trimmed[..dash] : trimmed;
    }

    /// <summary>归入最近的分桶，使报告为 6 核和 8 核的结果不会分离。</summary>
    private static string Bucket(int? value, int[] buckets)
    {
        if (value is not { } v || v <= 0)
        {
            return Missing;
        }

        foreach (var bucket in buckets)
        {
            if (v <= bucket)
            {
                return bucket.ToString();
            }
        }

        return "max";
    }

    /// <summary>将数值归入原有设备信号档位。</summary>
    private static string Bucket(double? value, double[] buckets)
    {
        if (value is not { } v || v <= 0)
        {
            return Missing;
        }

        foreach (var bucket in buckets)
        {
            if (v <= bucket)
            {
                return bucket.ToString("0.##");
            }
        }

        return "max";
    }

    /// <summary>按既有空白与大小写规则规范化指纹文本。</summary>
    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? Missing : value.Trim().ToLowerInvariant();

    /// <summary>转义用于规范化存储的文本内容。</summary>
    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("\"", "\\\"", StringComparison.Ordinal);

    /// <summary>
    /// 追加服务器端胡椒值后再哈希，使已存储指纹无法根据公开可观察信号重新计算，
    /// 也无法与其他系统的数据库进行关联。
    /// </summary>
    private byte[] Hash(string canonical) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(canonical + "|" + _options.Pepper));
}
