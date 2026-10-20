using CreativeFlow.Application.Contracts.Ai;

namespace CreativeFlow.UnitTests.Ai;

public sealed class ImageResponseFormatsTests
{
    /// <summary>回归场景：Supported Values Are Normalized。</summary>
    [Theory]
    [InlineData("url", "url")]
    [InlineData(" URL ", "url")]
    [InlineData("base64", "base64")]
    [InlineData("BASE64", "base64")]
    public void SupportedValuesAreNormalized(string input, string expected)
    {
        Assert.True(ImageResponseFormats.TryNormalize(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    /// <summary>回归场景：Unsupported Values Are Rejected。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("b64_json")]
    [InlineData("data-url")]
    public void UnsupportedValuesAreRejected(string? input)
    {
        Assert.False(ImageResponseFormats.TryNormalize(input, out _));
    }
}
