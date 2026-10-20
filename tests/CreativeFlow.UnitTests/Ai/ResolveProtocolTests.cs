using CreativeFlow.Domain.Ai;

namespace CreativeFlow.UnitTests.Ai;

/// <summary>
/// 协议解析：<c>model.Type ?? 分类默认值 ?? provider.Type</c>。
/// </summary>
/// <remarks>
/// 此规则在这里和 <c>web/features/ai/types.ts</c> 中分别实现，因为客户端用它决定渲染哪些参数控件，
/// 服务器则用它选择分派目标。若两端不一致，用户配置的内容与实际运行内容就会不同。
/// </remarks>
public class ResolveProtocolTests
{
    /// <summary>构造当前协议测试使用的渠道配置。</summary>
    private static AiProvider Provider(ProviderType type) => new(
        ProviderId.New(),
        BuiltinId: null,
        Name: "p",
        Description: null,
        Icon: null,
        type,
        ApiKey: null,
        BaseUrl: "https://example.com",
        Enabled: true,
        PresetVersion: null,
        SortOrder: 0,
        CreatedAt: DateTimeOffset.UnixEpoch,
        UpdatedAt: DateTimeOffset.UnixEpoch);

    /// <summary>构造带指定分类和协议覆盖的测试模型。</summary>
    private static AiModel Model(ProviderType? type, ModelCategory category) => new(
        ModelId.New(),
        ProviderId.New(),
        ModelKey: "m",
        Name: "M",
        Enabled: true,
        type,
        category,
        Icon: null,
        ContextLength: null,
        MaxOutputTokens: null,
        SupportsVision: false,
        SupportsFunctionCall: false,
        SupportsThinking: false,
        SortOrder: 0,
        CreatedAt: DateTimeOffset.UnixEpoch);

    /// <summary>回归场景：An Explicit Override Always Wins。</summary>
    [Fact]
    public void AnExplicitOverrideAlwaysWins()
    {
        // 这使同一聚合供应商可以提供使用多种协议的模型。
        var provider = Provider(ProviderType.OpenAiChat);

        Assert.Equal(
            ProviderType.Anthropic,
            provider.ResolveProtocol(Model(ProviderType.Anthropic, ModelCategory.Chat)));

        Assert.Equal(
            ProviderType.OpenAiResponses,
            provider.ResolveProtocol(Model(ProviderType.OpenAiResponses, ModelCategory.Chat)));
    }

    /// <summary>回归场景：An Override Wins Even Against The Category Default。</summary>
    [Fact]
    public void AnOverrideWinsEvenAgainstTheCategoryDefault()
    {
        // Gemini 从自身端点提供图片，而不是兼容 OpenAI 的图片端点。
        var provider = Provider(ProviderType.OpenAiChat);

        Assert.Equal(
            ProviderType.Gemini,
            provider.ResolveProtocol(Model(ProviderType.Gemini, ModelCategory.Image)));
    }

    /// <summary>回归场景：An Image Model Defaults To The Image Protocol Rather Than The Providers。</summary>
    [Fact]
    public void AnImageModelDefaultsToTheImageProtocolRatherThanTheProviders()
    {
        // 供应商按聊天协议声明，而其图片路由使用另一种协议。
        // 若在此回退到 provider.Type，会把图片请求发送到 /chat/completions。
        var provider = Provider(ProviderType.OpenAiChat);

        Assert.Equal(
            ProviderType.OpenAiImages,
            provider.ResolveProtocol(Model(null, ModelCategory.Image)));
    }

    /// <summary>回归场景：A Video Model Defaults To The Video Protocol。</summary>
    [Fact]
    public void AVideoModelDefaultsToTheVideoProtocol()
    {
        var provider = Provider(ProviderType.OpenAiChat);

        Assert.Equal(
            ProviderType.SeedanceVideo,
            provider.ResolveProtocol(Model(null, ModelCategory.Video)));
    }

    /// <summary>回归场景：A Video Model Inherits A Compatible Provider Protocol。</summary>
    [Theory]
    [InlineData(ProviderType.OpenAiVideo)]
    [InlineData(ProviderType.MiniMaxVideo)]
    [InlineData(ProviderType.MiniMaxH3)]
    [InlineData(ProviderType.SeedanceVideo)]
    [InlineData(ProviderType.XaiVideo)]
    public void AVideoModelInheritsACompatibleProviderProtocol(ProviderType providerType)
    {
        Assert.Equal(
            providerType,
            Provider(providerType).ResolveProtocol(Model(null, ModelCategory.Video)));
    }

    /// <summary>回归场景：An Image Model Inherits A Compatible Provider Protocol。</summary>
    [Theory]
    [InlineData(ProviderType.OpenAiImages)]
    [InlineData(ProviderType.Gemini)]
    public void AnImageModelInheritsACompatibleProviderProtocol(ProviderType providerType)
    {
        Assert.Equal(
            providerType,
            Provider(providerType).ResolveProtocol(Model(null, ModelCategory.Image)));
    }

    /// <summary>回归场景：A Chat Model Inherits The Provider。</summary>
    [Fact]
    public void AChatModelInheritsTheProvider()
    {
        Assert.Equal(
            ProviderType.Anthropic,
            Provider(ProviderType.Anthropic).ResolveProtocol(Model(null, ModelCategory.Chat)));
    }

    /// <summary>回归场景：No Model Means The Providers Own Protocol。</summary>
    [Fact]
    public void NoModelMeansTheProvidersOwnProtocol()
    {
        // 供未指定具体模型的连接检查使用。
        Assert.Equal(
            ProviderType.Gemini,
            Provider(ProviderType.Gemini).ResolveProtocol(null));
    }
}
