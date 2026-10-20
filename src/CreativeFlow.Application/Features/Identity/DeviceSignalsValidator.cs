using FluentValidation;
using CreativeFlow.Application.Contracts.Device;

namespace CreativeFlow.Application.Features.Identity;

/// <summary>
/// 限制握手负载。
/// </summary>
/// <remarks>
/// 规则刻意允许字段缺失，但严格限制大小。任何信号都可能合理缺失——Safari 26 默认隐藏屏幕参数和
/// hardwareConcurrency——但超大值要么是缺陷，要么是试图膨胀已存储的信号数据。
/// <para>
/// 必须在依赖注入中显式注册。<c>AddValidatorsFromAssembly</c> 通过反射扫描，
/// 在 AOT 下会静默地不注册任何内容，却能通过所有 JIT 模式测试。
/// </para>
/// </remarks>
public sealed class DeviceSignalsValidator : AbstractValidator<DeviceSignalsPayload>
{
    /// <summary>设备文本信号允许的最多字符数。</summary>
    private const int MaxTextLength = 512;

    /// <summary>初始化 DeviceSignalsValidator 的依赖与实例状态。</summary>
    public DeviceSignalsValidator()
    {
        RuleFor(x => x.V).InclusiveBetween(1, 100);

        RuleFor(x => x.ClientStoredId)
            .Must(id => id is null || Guid.TryParse(id, out _))
            .WithMessage("clientStoredId must be a GUID when present.");

        When(x => x.Stable is not null, () =>
        {
            RuleFor(x => x.Stable!.TimeZone).MaximumLength(MaxTextLength);
            RuleFor(x => x.Stable!.Platform).MaximumLength(MaxTextLength);
            RuleFor(x => x.Stable!.PrimaryLanguage).MaximumLength(MaxTextLength);
            RuleFor(x => x.Stable!.HardwareConcurrency)
                .InclusiveBetween(1, 1024).When(x => x.Stable!.HardwareConcurrency.HasValue);
            RuleFor(x => x.Stable!.DeviceMemoryGb)
                .InclusiveBetween(0.1d, 1024d).When(x => x.Stable!.DeviceMemoryGb.HasValue);
        });

        When(x => x.Volatile is not null, () =>
        {
            RuleFor(x => x.Volatile!.UserAgent).MaximumLength(MaxTextLength);
            RuleFor(x => x.Volatile!.CanvasHash).MaximumLength(MaxTextLength);
            RuleFor(x => x.Volatile!.AudioHash).MaximumLength(MaxTextLength);
            RuleFor(x => x.Volatile!.FontsHash).MaximumLength(MaxTextLength);
            RuleFor(x => x.Volatile!.Languages)
                .Must(l => l is null || l.Count <= 32)
                .WithMessage("languages must contain at most 32 entries.");
        });
    }
}
