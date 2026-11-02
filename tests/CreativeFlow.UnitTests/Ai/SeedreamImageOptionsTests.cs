using CreativeFlow.Domain.Ai;

namespace CreativeFlow.UnitTests.Ai;

/// <summary>各型号档位都保留所选比例，并在官方像素边界内；无效参数不能变成收费请求。</summary>
public sealed class SeedreamImageOptionsTests
{
    /// <summary>官方型号、带日期 ID 与端点别名分别使用可确认的能力。</summary>
    [Theory]
    [InlineData("doubao-seedream-5.0-pro", "1K,1.5K,2K", "standard,fast")]
    [InlineData("doubao-seedream-5-0-flash-260915", "1K,1.5K,2K", "standard")]
    [InlineData("doubao-seedream-5.0-lite", "2K,3K,4K", "standard")]
    [InlineData("doubao-seedream-5-0-260128", "2K,3K,4K", "standard")]
    [InlineData("doubao-seedream-4.5", "2K,4K", "standard")]
    [InlineData("doubao-seedream-4.0", "1K,2K,4K", "standard,fast")]
    [InlineData("ep-custom-endpoint", "2K", "standard")]
    public void EveryExposedCombinationIsValid(string model, string resolutions, string modes)
    {
        Assert.Equal(resolutions.Split(','), SeedreamImageOptions.Resolutions(model));
        Assert.Equal(modes.Split(','), SeedreamImageOptions.PromptModes(model));
        foreach (var resolution in SeedreamImageOptions.Resolutions(model))
        foreach (var mode in SeedreamImageOptions.PromptModes(model))
        foreach (var aspect in new[] { "1:1", "16:9", "9:16", "4:3", "3:4", "3:2", "2:3" })
        {
            var size = SeedreamImageOptions.SizeForAspect(aspect, resolution);
            Assert.Null(SeedreamImageOptions.Validate(model, size, mode));
            var dimensions = size.Split('x').Select(int.Parse).ToArray();
            var ratio = aspect.Split(':').Select(double.Parse).ToArray();
            Assert.Equal(ratio[0] / ratio[1], (double)dimensions[0] / dimensions[1], 6);
        }
        Assert.Equal("2816x1584", SeedreamImageOptions.SizeForAspect("16:9"));
        Assert.Equal("2112x1188", SeedreamImageOptions.SizeForAspect("16:9", "1.5K"));
    }

    /// <summary>型号不支持的模式、档位及过大的像素都明确拒绝，不退回其他档位。</summary>
    [Theory]
    [InlineData("doubao-seedream-5.0-flash", "2K", "fast")]
    [InlineData("doubao-seedream-5.0-pro", "4K", "standard")]
    [InlineData("doubao-seedream-5.0-pro", "4096x4096", "standard")]
    [InlineData("doubao-seedream-4.5", "1024x1024", "standard")]
    [InlineData("doubao-seedream-5.0-pro", "bad-size", "standard")]
    [InlineData("doubao-seedream-5.0-pro", "2048x2048", "turbo")]
    public void UnsupportedOptionsFail(string model, string size, string mode) =>
        Assert.NotNull(SeedreamImageOptions.Validate(model, size, mode));
}
