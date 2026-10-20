using System.Runtime.CompilerServices;
using System.Text;

namespace CreativeFlow.Infrastructure.Ai.Protocols;

/// <param name="EventName">上游 SSE 帧的 event 字段，未提供时为空。</param>
/// <param name="Data">按帧内 data 行顺序拼接的原始文本，尚未按具体模型协议解析。</param>
internal sealed record SseFrame(string? EventName, string Data);

/// <summary>无需缓冲响应正文即可读取服务器发送事件。</summary>
internal static class SseReader
{
    /// <summary>按 SSE 帧边界读取事件与数据行，保留多行数据及流结束前的最后一帧。</summary>
    public static async IAsyncEnumerable<SseFrame> ReadAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(
            stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);

        string? eventName = null;
        var data = new StringBuilder();

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    // 每条数据行贡献一个换行符。最后的分隔符不属于负载，
                    // 因此只精确移除一个换行，而不是任意空白。
                    data.Length -= 1;
                    yield return new SseFrame(eventName, data.ToString());
                }

                eventName = null;
                data.Clear();
                continue;
            }

            if (line[0] == ':') continue; // 心跳/注释

            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? string.Empty : line[(colon + 1)..];
            if (value.StartsWith(' ')) value = value[1..];

            switch (field)
            {
                case "event":
                    eventName = value;
                    break;
                case "data":
                    data.Append(value).Append('\n');
                    break;
            }
        }

        // 符合规范的服务器会写入空行，但接受文件末尾可避免中继在 [DONE] 后立即关闭时
        // 丢失最后一个 JSON 对象。
        if (data.Length > 0)
        {
            data.Length -= 1;
            yield return new SseFrame(eventName, data.ToString());
        }
    }
}
