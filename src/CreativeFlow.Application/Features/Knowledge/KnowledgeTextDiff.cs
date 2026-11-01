using CreativeFlow.Application.Contracts.Ai;

namespace CreativeFlow.Application.Features.Knowledge;

/// <summary>有界逐行比较；大块完全变化时使用明确标记的替换，不分配无界二维矩阵或全量差异行对象。</summary>
public static class KnowledgeTextDiff
{
    /// <summary>仅创建当前页的差异行，返回总行数及是否采用大块替换。</summary>
    public static (int Total, bool Simplified, KnowledgeDiffLine[] Lines) Page(string? before, string? after, int offset, int limit, CancellationToken ct)
    {
        if (offset < 0 || limit is < 1 or > 200) throw new KnowledgeException("差异分页每页 1–200 行。");
        var a = before?.Split('\n') ?? []; var b = after?.Split('\n') ?? [];
        var prefix = 0;
        while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix]) prefix++;
        var suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix && a[^(suffix + 1)] == b[^(suffix + 1)]) suffix++;
        // 首尾相同行先剥离；只对中间区域计算差异，并在超预算时显式标记简化。
        var segments = new List<Segment>();
        Add(segments, "equal", 0, 0, prefix);
        var n = a.Length - prefix - suffix; var m = b.Length - prefix - suffix;
        var simplified = n > 0 && m > 0 && (long)(n + 1) * (m + 1) > 1_000_000;
        if (n == 0 || m == 0 || simplified)
        {
            Add(segments, "removed", prefix, prefix, n);
            Add(segments, "added", prefix + n, prefix, m);
        }
        else CompareMiddle(a, b, prefix, n, m, segments, ct);
        Add(segments, "equal", a.Length - suffix, b.Length - suffix, suffix);
        var total = segments.Sum(segment => segment.Count); var rows = new List<KnowledgeDiffLine>(); var position = 0;
        foreach (var segment in segments)
        {
            ct.ThrowIfCancellationRequested();
            var start = Math.Max(0, offset - position); var end = Math.Min(segment.Count, offset + (long)limit - position);
            for (var i = start; i < end; i++) rows.Add(new(segment.Kind,
                segment.Kind == "added" ? null : segment.From + i + 1,
                segment.Kind == "removed" ? null : segment.To + i + 1,
                segment.Kind == "added" ? b[segment.To + i] : a[segment.From + i]));
            position += segment.Count;
            if (rows.Count >= limit) break;
        }
        return (total, simplified, rows.ToArray());
    }

    /// <summary>只对已通过矩阵预算检查的中间变化区计算最长公共子序列。</summary>
    private static void CompareMiddle(string[] a, string[] b, int start, int n, int m, List<Segment> output, CancellationToken ct)
    {
        var width = m + 1; var table = new int[(n + 1) * width];
        for (var i = n - 1; i >= 0; i--)
        {
            ct.ThrowIfCancellationRequested();
            for (var j = m - 1; j >= 0; j--)
                table[i * width + j] = a[start + i] == b[start + j]
                    ? table[(i + 1) * width + j + 1] + 1 : Math.Max(table[(i + 1) * width + j], table[i * width + j + 1]);
        }
        var x = 0; var y = 0;
        while (x < n || y < m)
        {
            if (x < n && y < m && a[start + x] == b[start + y]) { Add(output, "equal", start + x++, start + y++, 1); }
            else if (x < n && (y == m || table[(x + 1) * width + y] >= table[x * width + y + 1]))
                Add(output, "removed", start + x++, start + y, 1);
            else Add(output, "added", start + x, start + y++, 1);
        }
    }

    /// <summary>合并同种连续操作，避免按每行累积重复段。</summary>
    private static void Add(List<Segment> rows, string kind, int from, int to, int count)
    {
        if (count == 0) return;
        if (rows.LastOrDefault() is { } last && last.Kind == kind
            && (kind == "added" || last.From + last.Count == from) && (kind == "removed" || last.To + last.Count == to))
            rows[^1] = last with { Count = last.Count + count };
        else rows.Add(new(kind, from, to, count));
    }
    /// <summary>合并后的连续编辑段，不为未请求的页创建差异行对象。</summary>
    /// <param name="Kind">连续差异片段的操作种类。</param>
    /// <param name="From">旧正文零基起始行。</param>
    /// <param name="To">新正文零基起始行。</param>
    /// <param name="Count">片段覆盖的差异行数。</param>

    private sealed record Segment(string Kind, int From, int To, int Count);
}
