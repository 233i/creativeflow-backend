using CreativeFlow.Domain.Ai;

namespace CreativeFlow.UnitTests.Ai;

/// <summary>
/// 基础 URL 规范化。
/// </summary>
/// <remarks>
/// 此功能曾带缺陷发布：规范化只存在于浏览器中，因此直接通过 API 创建或从分享链接导入的供应商
/// 会产生 <c>…/v1/v1/messages</c>，导致所有 Anthropic 调用返回 404。
/// 该不变量应与追加路径的代码放在一起，这些测试用于固定该行为。
/// </remarks>
public class NormalizeBaseUrlTests
{
    /// <summary>回归场景：Strips Anthropic Suffixes。</summary>
    [Theory]
    // 客户端会追加 /v1/messages，因此粘贴地址末尾的 /v1 必须移除。
    [InlineData("https://api.anthropic.com/v1", "https://api.anthropic.com")]
    [InlineData("https://api.anthropic.com/v1/", "https://api.anthropic.com")]
    [InlineData("https://api.anthropic.com/v1/messages", "https://api.anthropic.com")]
    [InlineData("https://api.anthropic.com", "https://api.anthropic.com")]
    // 不区分大小写，因为手动输入的 URL 大小写可能不同。
    [InlineData("https://api.anthropic.com/V1", "https://api.anthropic.com")]
    public void StripsAnthropicSuffixes(string input, string expected)
    {
        Assert.Equal(expected, AiProvider.NormalizeBaseUrl(input, ProviderType.Anthropic));
    }

    /// <summary>回归场景：Strips Gemini Compat Suffix。</summary>
    [Theory]
    // Gemini 使用原生接口，因此必须移除兼容 OpenAI 的后缀。
    [InlineData("https://generativelanguage.googleapis.com/openai", "https://generativelanguage.googleapis.com")]
    [InlineData("https://generativelanguage.googleapis.com", "https://generativelanguage.googleapis.com")]
    public void StripsGeminiCompatSuffix(string input, string expected)
    {
        Assert.Equal(expected, AiProvider.NormalizeBaseUrl(input, ProviderType.Gemini));
    }

    /// <summary>回归场景：Keeps Version Segment For Open Ai Shapes。</summary>
    [Theory]
    // 其他协议保留 /v1；兼容 OpenAI 的客户端只追加具体路由。
    [InlineData(ProviderType.OpenAiChat)]
    [InlineData(ProviderType.OpenAiResponses)]
    [InlineData(ProviderType.OpenAiImages)]
    public void KeepsVersionSegmentForOpenAiShapes(ProviderType protocol)
    {
        Assert.Equal(
            "https://api.openai.com/v1",
            AiProvider.NormalizeBaseUrl("https://api.openai.com/v1", protocol));
    }

    /// <summary>回归场景：Trims Whitespace And Trailing Slashes。</summary>
    [Fact]
    public void TrimsWhitespaceAndTrailingSlashes()
    {
        Assert.Equal(
            "https://api.example.com/v1",
            AiProvider.NormalizeBaseUrl("  https://api.example.com/v1///  ", ProviderType.OpenAiChat));
    }

    /// <summary>回归场景：Is Idempotent。</summary>
    [Fact]
    public void IsIdempotent()
    {
        // 每次写入更新都会重新规范化，因此重复应用不能继续吞掉路径段。
        var once = AiProvider.NormalizeBaseUrl("https://api.anthropic.com/v1", ProviderType.Anthropic);
        var twice = AiProvider.NormalizeBaseUrl(once, ProviderType.Anthropic);

        Assert.Equal(once, twice);
    }

    /// <summary>回归场景：Does Not Strip A Version Segment That Is Part Of The Host。</summary>
    [Fact]
    public void DoesNotStripAVersionSegmentThatIsPartOfTheHost()
    {
        // 中继可合理部署在子路径下；只有末尾路径段属于后缀。
        Assert.Equal(
            "https://relay.example.com/v1/anthropic",
            AiProvider.NormalizeBaseUrl("https://relay.example.com/v1/anthropic", ProviderType.Anthropic));
    }

    /// <summary>回归场景：Accepts Absolute Http Urls Including Local Runtimes。</summary>
    [Theory]
    [InlineData("https://api.example.com/v1")]
    [InlineData("http://localhost:11434/v1")]
    [InlineData("http://127.0.0.1:8080")]
    public void AcceptsAbsoluteHttpUrlsIncludingLocalRuntimes(string input)
    {
        Assert.True(
            AiProvider.TryNormalizeBaseUrl(
                input,
                ProviderType.OpenAiChat,
                out var normalized,
                out _));
        Assert.NotEmpty(normalized);
    }

    /// <summary>回归场景：Rejects Urls That Cannot Be Safely Appended To。</summary>
    [Theory]
    [InlineData("/v1")]
    [InlineData("file:///tmp/provider")]
    [InlineData("https://user:password@example.com/v1")]
    [InlineData("https://api.example.com/v1?token=secret")]
    [InlineData("https://api.example.com/v1#fragment")]
    public void RejectsUrlsThatCannotBeSafelyAppendedTo(string input)
    {
        Assert.False(
            AiProvider.TryNormalizeBaseUrl(
                input,
                ProviderType.OpenAiChat,
                out _,
                out _));
    }
}
