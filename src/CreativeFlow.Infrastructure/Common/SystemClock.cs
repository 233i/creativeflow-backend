using CreativeFlow.Application.Abstractions.Common;

namespace CreativeFlow.Infrastructure.Common;

public sealed class SystemClock : IClock
{
    /// <summary>返回系统当前 UTC 时间，供业务通过统一时钟端口读取。</summary>
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
