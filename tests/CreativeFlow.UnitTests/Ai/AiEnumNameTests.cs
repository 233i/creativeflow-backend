using CreativeFlow.Domain.Ai;

namespace CreativeFlow.UnitTests.Ai;

/// <summary>
/// 枚举与传输字符串的双向映射。
/// </summary>
/// <remarks>
/// 值以文本持久化并受数据库 CHECK 约束，因此两个方向必须保持同步。
/// 若协议只加入 <c>ToWire</c> 而未加入解析器，查询读回时会抛出异常；反之则会写入被 CHECK 拒绝的值。
/// </remarks>
public class AiEnumNameTests
{
    /// <summary>枚举全部渠道协议，确保每种协议都有稳定的字符串映射。</summary>
    public static TheoryData<ProviderType> AllProviderTypes()
    {
        var data = new TheoryData<ProviderType>();
        foreach (var value in Enum.GetValues<ProviderType>()) data.Add(value);
        return data;
    }

    /// <summary>枚举全部模型类别，验证过线名称可以往返转换。</summary>
    public static TheoryData<ModelCategory> AllCategories()
    {
        var data = new TheoryData<ModelCategory>();
        foreach (var value in Enum.GetValues<ModelCategory>()) data.Add(value);
        return data;
    }

    /// <summary>枚举全部 MCP 传输方式，验证名称映射覆盖完整。</summary>
    public static TheoryData<McpTransport> AllTransports()
    {
        var data = new TheoryData<McpTransport>();
        foreach (var value in Enum.GetValues<McpTransport>()) data.Add(value);
        return data;
    }

    /// <summary>回归场景：Every Provider Type Round Trips。</summary>
    [Theory]
    [MemberData(nameof(AllProviderTypes))]
    public void EveryProviderTypeRoundTrips(ProviderType value)
    {
        Assert.True(AiEnumNames.TryParseProviderType(value.ToWire(), out var parsed));
        Assert.Equal(value, parsed);
    }

    /// <summary>回归场景：Every Category Round Trips。</summary>
    [Theory]
    [MemberData(nameof(AllCategories))]
    public void EveryCategoryRoundTrips(ModelCategory value)
    {
        Assert.True(AiEnumNames.TryParseModelCategory(value.ToWire(), out var parsed));
        Assert.Equal(value, parsed);
    }

    /// <summary>回归场景：Every Transport Round Trips。</summary>
    [Theory]
    [MemberData(nameof(AllTransports))]
    public void EveryTransportRoundTrips(McpTransport value)
    {
        Assert.True(AiEnumNames.TryParseMcpTransport(value.ToWire(), out var parsed));
        Assert.Equal(value, parsed);
    }

    /// <summary>回归场景：Wire Names Are Kebab Case And Distinct。</summary>
    [Fact]
    public void WireNamesAreKebabCaseAndDistinct()
    {
        // 数据库 CHECK 约束列出了这些字面量；意外的大写或重复值会在插入时失败，而不是在构建时失败。
        var names = Enum.GetValues<ProviderType>().Select(value => value.ToWire()).ToList();

        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.All(names, name => Assert.Equal(name.ToLowerInvariant(), name));
    }

    /// <summary>回归场景：Unknown Values Are Rejected Rather Than Guessed。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("openai")]
    [InlineData("OpenAI-Chat")]
    [InlineData(null)]
    public void UnknownValuesAreRejectedRatherThanGuessed(string? text)
    {
        Assert.False(AiEnumNames.TryParseProviderType(text, out _));
    }
}
