using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CreativeFlow.Application.Contracts.Ai;

namespace CreativeFlow.Application.Features.Knowledge;

/// <summary>有界的语义文本分段器；不解析 HTML、不抓取 Markdown 链接、不执行文档指令。</summary>
public static class KnowledgeTextParser
{
    /// <summary>语义切片版本；改变切片边界时通过新版本摘要隔离旧索引。</summary>
    public const string ParserVersion = "semantic-v3";

    private static readonly Regex Heading = new(@"^\s{0,3}(#{1,6})\s+(.+?)\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SetextHeading = new(@"^\s*(=+|-+)\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ListItem = new(@"^\s*(?:[-*+]\s+|\d+[.)]\s+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Fence = new(@"^\s{0,3}(```|~~~)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TableSeparator = new(@"^\s*\|?\s*:?-{3,}:?\s*(?:\|\s*:?-{3,}:?\s*)*\|?\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly char[] SentenceTerminators = ['。', '！', '？', '；', '!', '?', ';'];

    /// <summary>校验全量文档并生成稳定预览；不写数据库或调用向量模型。</summary>
    public static KnowledgePreview Preview(KnowledgeImportRequest request, KnowledgeQuotas? quotas = null)
    {
        quotas = (quotas ?? new()).Validate();
        if (request.Documents is not { Length: >= 1 } || request.Documents.Length > quotas.MaxDocuments) throw new KnowledgeException($"请提供 1–{quotas.MaxDocuments} 份 TXT 或 Markdown 文档。");
        if (request.ChunkSize is < 200 or > 1600 || request.Overlap < 0 || request.Overlap > 400 || request.Overlap >= request.ChunkSize / 2)
            throw new KnowledgeException("分段长度应为 200–1600，重叠不超过 400 且小于分段长度的一半。");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var total = 0;
        var chunks = new List<KnowledgeChunk>();
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(digest, $"{ParserVersion}:{request.ChunkSize}:{request.Overlap}");
        foreach (var document in request.Documents)
        {
            // 扩展名只是格式入口；正文仍严格检查 UTF-8、二进制控制符及大小，不按文件名信任内容。
            if (document is null || string.IsNullOrWhiteSpace(document.Name) || document.Name.Length > 120
                || document.Name.IndexOfAny(['/', '\\', '\0']) >= 0 || document.Name.Any(char.IsControl)
                || !names.Add(document.Name) || Path.GetExtension(document.Name).ToLowerInvariant() is not (".txt" or ".md"))
                throw new KnowledgeException("文档名称必须唯一且为 TXT／MD 文件名，不允许路径。");
            var text = NormalizeText(document.Text);
            if (string.IsNullOrWhiteSpace(text)) throw new KnowledgeException("文档正文不能为空；扫描文件和 PDF／DOCX 尚不支持。");
            try
            {
                var bytes = new UTF8Encoding(false, true).GetByteCount(text);
                if (bytes > quotas.MaxDocumentBytes) throw new KnowledgeException("单篇文档超过配置的字节配额。");
                total += bytes;
            }
            catch (EncoderFallbackException) { throw new KnowledgeException("正文不是有效的 UTF-8 文本。"); }
            if (total > quotas.MaxVersionBytes) throw new KnowledgeException("完整版本的文档总大小超过配置配额。");
            if (text.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t'))
                || text.StartsWith("%PDF-", StringComparison.Ordinal) || text.StartsWith("PK\u0003\u0004", StringComparison.Ordinal))
                throw new KnowledgeException("检测到非纯文本内容；请使用 UTF-8 TXT／Markdown。");
            Append(digest, document.Name); Append(digest, text);
            var documentDigest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
            foreach (var part in ChunkDocument(text, request.ChunkSize, request.Overlap))
            {
                var body = text[part.StartOffset..part.EndOffset];
                if (string.IsNullOrWhiteSpace(body)) continue;
                var identity = JsonSerializer.Serialize(new[] { ParserVersion, document.Name, documentDigest,
                    part.StartOffset.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    part.EndOffset.ToString(System.Globalization.CultureInfo.InvariantCulture), part.SectionPath, body }, KnowledgeJsonContext.Default.StringArray);
                var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(identity)).AsSpan(0, 16));
                chunks.Add(new(id, document.Name, part.StartLine, part.EndLine, body)
                {
                    StartOffset = part.StartOffset, EndOffset = part.EndOffset, SectionPath = part.SectionPath,
                    BlockType = part.BlockType, Continuation = part.Continuation,
                });
                if (chunks.Count > quotas.MaxChunks) throw new KnowledgeException($"完整版本最多 {quotas.MaxChunks} 个片段，请减少文档或增大分段长度。");
            }
        }
        if (chunks.Count == 0) throw new KnowledgeException("没有可入库的文本片段。");
        return new(Convert.ToHexStringLower(digest.GetHashAndReset()), chunks.ToArray(), request.Documents.Length);
    }

    /// <summary>统一 BOM/换行后再做字节预算、摘要和 offset，避免同一正文产生不同切片。</summary>
    public static string NormalizeText(string text) => text.TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private sealed record Line(int Start, int End, string Text, int Number);
    private sealed record Block(int Start, int End, int StartLine, int EndLine, string SectionPath, string Type);
    private sealed record Piece(int Start, int End, string SectionPath, string Type, bool Continuation);
    private sealed record Part(int StartOffset, int EndOffset, int StartLine, int EndLine, string SectionPath, string BlockType, bool Continuation);

    /// <summary>扫描结构块并将其按语义单元打包；每个输出区间共同覆盖规范化正文。</summary>
    private static IReadOnlyList<Part> ChunkDocument(string text, int target, int overlap)
    {
        var blocks = ScanBlocks(text, ReadLines(text));
        var pieces = new List<Piece>();
        foreach (var block in blocks)
        {
            if (block.End - block.Start <= target)
            {
                pieces.Add(new(block.Start, block.End, block.SectionPath, block.Type, false));
                continue;
            }
            var ranges = block.Type == "paragraph" ? SplitSentences(text, block.Start, block.End) : SplitLines(text, block.Start, block.End, target);
            foreach (var range in ranges)
            {
                if (range.End - range.Start <= target)
                    pieces.Add(new(range.Start, range.End, block.SectionPath, block.Type, false));
                else
                    pieces.AddRange(HardSplit(text, range.Start, range.End, target, block.SectionPath, block.Type));
            }
        }
        if (pieces.Count == 0) return [];
        var result = new List<Part>();
        var current = new List<Piece>();
        var index = 0;
        while (index < pieces.Count)
        {
            var piece = pieces[index];
            var currentLength = current.Count == 0 ? 0 : current[^1].End - current[0].Start;
            // 标题路径变化就是语义边界；即使一侧是文档开头的无标题前言，也不能把两个章节揉成一个混合块。
            var sectionBreak = current.Count > 0
                && !string.Equals(current[^1].SectionPath, piece.SectionPath, StringComparison.Ordinal);
            if (current.Count > 0 && (sectionBreak || currentLength + (piece.End - piece.Start) > target))
            {
                result.Add(ToPart(text, current));
                var tail = new List<Piece>(); var tailLength = 0;
                for (var i = current.Count - 1; !sectionBreak && i >= 0; i--)
                {
                    var candidateLength = current[i].End - current[i].Start;
                    if (tailLength + candidateLength > overlap) break;
                    tail.Insert(0, current[i]); tailLength += candidateLength;
                }
                current = tail;
                if (current.Count > 0 && current[^1].End - current[0].Start + (piece.End - piece.Start) > target)
                    current.Clear();
                continue;
            }
            current.Add(piece); index++;
        }
        if (current.Count > 0) result.Add(ToPart(text, current));
        return result;
    }

    /// <summary>按 Unicode 标点切句，保留区间内所有空白；无标点时返回一个待硬切区间。</summary>
    private static IEnumerable<(int Start, int End)> SplitSentences(string text, int start, int end)
    {
        var cursor = start;
        for (var i = start; i < end; i++)
        {
            if (!SentenceTerminators.Contains(text[i])) continue;
            var next = i + 1;
            while (next < end && char.IsWhiteSpace(text[next])) next++;
            yield return (cursor, next); cursor = next; i = next - 1;
        }
        if (cursor < end) yield return (cursor, end);
    }

    /// <summary>按完整行打包代码、列表和表格，避免先按句号破坏结构。</summary>
    private static IEnumerable<(int Start, int End)> SplitLines(string text, int start, int end, int target)
    {
        var currentStart = start; var currentEnd = start; var cursor = start;
        while (cursor < end)
        {
            var lineEnd = text.IndexOf('\n', cursor, end - cursor);
            lineEnd = lineEnd < 0 ? end : lineEnd + 1;
            if (lineEnd - cursor > target)
            {
                if (currentEnd > currentStart) { yield return (currentStart, currentEnd); currentStart = cursor; }
                yield return (cursor, lineEnd); cursor = lineEnd; currentStart = cursor; currentEnd = cursor; continue;
            }
            if (currentEnd > currentStart && lineEnd - currentStart > target)
            { yield return (currentStart, currentEnd); currentStart = cursor; }
            currentEnd = lineEnd; cursor = lineEnd;
        }
        if (currentEnd > currentStart) yield return (currentStart, currentEnd);
    }

    /// <summary>最终兜底按代理对安全硬切，并把续片段显式标记出来。</summary>
    private static IEnumerable<Piece> HardSplit(string text, int start, int end, int target, string section, string type)
    {
        var offset = start;
        while (offset < end)
        {
            var next = Math.Min(offset + target, end);
            if (next < end && char.IsLowSurrogate(text[next])) next--;
            if (next <= offset) next = Math.Min(offset + 2, end);
            yield return new(offset, next, section, type, true); offset = next;
        }
    }

    /// <summary>把相邻语义片段转成最终 chunk，并从规范化正文计算行号。</summary>
    private static Part ToPart(string text, IReadOnlyList<Piece> pieces)
    {
        var start = pieces[0].Start; var end = pieces[^1].End;
        var section = pieces.Select(p => p.SectionPath).FirstOrDefault(p => p.Length > 0) ?? "";
        var type = pieces.Select(p => p.Type).Distinct(StringComparer.Ordinal).Count() == 1 ? pieces[0].Type : "mixed";
        var continuation = pieces.Any(p => p.Continuation);
        var startLine = 1 + text.AsSpan(0, start).Count('\n');
        var endLine = startLine + text.AsSpan(start, end - start).Count('\n');
        return new(start, end, startLine, endLine, section, type, continuation);
    }

    /// <summary>识别标题、代码、表格、列表和普通段落；标题路径只影响后续语义块，不丢弃标题文本。</summary>
    private static IReadOnlyList<Block> ScanBlocks(string text, IReadOnlyList<Line> lines)
    {
        var raw = new List<Block>(); var headingStack = new List<string>(); var i = 0;
        while (i < lines.Count)
        {
            if (string.IsNullOrWhiteSpace(lines[i].Text)) { i++; continue; }
            var line = lines[i]; var heading = Heading.Match(line.Text);
            if (heading.Success)
            {
                var level = heading.Groups[1].Length; while (headingStack.Count >= level) headingStack.RemoveAt(headingStack.Count - 1);
                headingStack.Add(heading.Groups[2].Value.Trim());
                raw.Add(new(line.Start, line.End, line.Number, line.Number, string.Join(" > ", headingStack), "heading")); i++; continue;
            }
            // Markdown 的 setext 标题由正文行和下一行的 ===/--- 组成，按标题块整体保留。
            if (IsSetextStart(lines, i) && !Heading.IsMatch(line.Text) && !Fence.IsMatch(line.Text) && !ListItem.IsMatch(line.Text))
            {
                var level = lines[i + 1].Text.TrimStart().StartsWith('=') ? 1 : 2;
                while (headingStack.Count >= level) headingStack.RemoveAt(headingStack.Count - 1);
                headingStack.Add(line.Text.Trim());
                raw.Add(new(line.Start, lines[i + 1].End, line.Number, lines[i + 1].Number, string.Join(" > ", headingStack), "heading")); i += 2; continue;
            }
            var section = string.Join(" > ", headingStack);
            var fence = Fence.Match(line.Text);
            if (fence.Success)
            {
                var marker = fence.Groups[1].Value; var end = i + 1;
                while (end < lines.Count && !Regex.IsMatch(lines[end].Text, $@"^\s{{0,3}}{Regex.Escape(marker)}\s*$")) end++;
                if (end < lines.Count) end++;
                var last = lines[Math.Min(end, lines.Count) - 1]; raw.Add(new(line.Start, last.End, line.Number, last.Number, section, "code")); i = end; continue;
            }
            if (IsTableStart(lines, i))
            {
                var end = i + 2; while (end < lines.Count && !string.IsNullOrWhiteSpace(lines[end].Text) && lines[end].Text.Contains('|')) end++;
                raw.Add(new(line.Start, lines[end - 1].End, line.Number, lines[end - 1].Number, section, "table")); i = end; continue;
            }
            if (ListItem.IsMatch(line.Text))
            {
                var end = i + 1; while (end < lines.Count && (ListItem.IsMatch(lines[end].Text) || (lines[end].Text.Length > 0 && char.IsWhiteSpace(lines[end].Text[0])))) end++;
                raw.Add(new(line.Start, lines[end - 1].End, line.Number, lines[end - 1].Number, section, "list")); i = end; continue;
            }
            var paragraphEnd = i + 1;
            while (paragraphEnd < lines.Count && !string.IsNullOrWhiteSpace(lines[paragraphEnd].Text)
                && !Heading.IsMatch(lines[paragraphEnd].Text) && !Fence.IsMatch(lines[paragraphEnd].Text)
                && !ListItem.IsMatch(lines[paragraphEnd].Text) && !IsTableStart(lines, paragraphEnd) && !IsSetextStart(lines, paragraphEnd)) paragraphEnd++;
            raw.Add(new(line.Start, lines[paragraphEnd - 1].End, line.Number, lines[paragraphEnd - 1].Number, section, "paragraph")); i = paragraphEnd;
        }
        if (raw.Count == 0) return [];
        var contiguous = new List<Block>(raw.Count);
        for (var n = 0; n < raw.Count; n++)
        {
            var start = n == 0 ? 0 : raw[n].Start;
            var end = n + 1 < raw.Count ? raw[n + 1].Start : text.Length;
            contiguous.Add(raw[n] with { Start = start, End = end });
        }
        return contiguous;
    }

    /// <summary>Markdown 表格必须拥有表头和分隔线，避免把普通竖线文本误识别。</summary>
    private static bool IsTableStart(IReadOnlyList<Line> lines, int index) => index + 1 < lines.Count && lines[index].Text.Contains('|') && TableSeparator.IsMatch(lines[index + 1].Text);

    /// <summary>识别 setext 标题起点；调用方负责排除代码、列表等优先级更高的块。</summary>
    private static bool IsSetextStart(IReadOnlyList<Line> lines, int index) => index + 1 < lines.Count
        && !string.IsNullOrWhiteSpace(lines[index + 1].Text) && SetextHeading.IsMatch(lines[index + 1].Text);

    /// <summary>读取行的 UTF-16 区间，换行符保留在行尾。</summary>
    private static IReadOnlyList<Line> ReadLines(string text)
    {
        var lines = new List<Line>(); var start = 0; var number = 1;
        for (var i = 0; i < text.Length; i++) if (text[i] == '\n') { lines.Add(new(start, i + 1, text[start..i], number++)); start = i + 1; }
        if (start < text.Length || lines.Count == 0) lines.Add(new(start, text.Length, text[start..], number));
        return lines;
    }

    /// <summary>使用长度前缀避免不同文档边界拼接出相同摘要。</summary>
    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        hash.AppendData(BitConverter.GetBytes(bytes.Length)); hash.AppendData(bytes);
    }
}

/// <summary>知识库用例只向交付层传递已脱敏、可操作的错误。</summary>
public sealed class KnowledgeException(string message) : Exception(message);
