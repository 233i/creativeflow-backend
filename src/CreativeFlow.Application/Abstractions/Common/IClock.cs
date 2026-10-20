namespace CreativeFlow.Application.Abstractions.Common;

/// <summary>时间源，使身份解析时间窗口可以测试。</summary>
public interface IClock
{
    /// <summary>当前 UTC 时间，由调用方注入以便验证时间窗口。</summary>
    DateTimeOffset UtcNow { get; }
}
