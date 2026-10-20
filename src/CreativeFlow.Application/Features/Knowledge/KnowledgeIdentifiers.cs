using System.Text;
using System.Text.RegularExpressions;

namespace CreativeFlow.Application.Features.Knowledge;

/// <summary>保护字段名、编码和引号中的精确词，始终保留完整标识而不是破坏性的标点分词。</summary>
public static partial class KnowledgeIdentifiers
{
    /// <summary>使用相同规范化规则生成入库和查询标识；不修改原文或来源 offset。</summary>
    public static string[] Extract(string text) => IdentifierPattern().Matches(text).Select(match => match.Value)
        .Where(term => term.Contains('_') || term.Contains('-') || term.Contains('.') || term.Contains(':')
            || term.Any(char.IsDigit) && term.Any(char.IsLetter))
        .Concat(QuotedPattern().Matches(text).Select(match => match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value))
        .Select(term => term.Normalize(NormalizationForm.FormKC).Trim().ToLowerInvariant())
        .Where(term => term.Length is > 0 and <= 128).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    /// <summary>识别完整字段名和字母数字业务编码，避免使用子串作为精确标识。</summary>
    [GeneratedRegex(@"(?<![A-Za-z0-9_])[A-Za-z_][A-Za-z0-9_]*(?:[-.:][A-Za-z0-9_]+)*(?![A-Za-z0-9_])", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();
    /// <summary>识别显式引号或反引号中的名称，用于保护改写前的原始标识。</summary>
    [GeneratedRegex("`([^`\\r\\n]{1,128})`|\"([^\"\\r\\n]{1,128})\"", RegexOptions.CultureInvariant)]
    private static partial Regex QuotedPattern();
}
