using CreativeFlow.Application.Abstractions.Security;

namespace CreativeFlow.Infrastructure.Identity;

/// <summary>初始化 ExternalLoginProviderRegistry 的依赖与实例状态。</summary>
internal sealed class ExternalLoginProviderRegistry(
    IEnumerable<IExternalLoginProvider> configuredProviders)
    : IExternalLoginProviderRegistry
{
    /// <summary>按名称索引的已启用外部身份提供方，查找时忽略名称大小写。</summary>
    private readonly IReadOnlyDictionary<string, IExternalLoginProvider> providers =
        configuredProviders
            .Where(provider => provider.IsEnabled)
            .ToDictionary(provider => provider.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>按渠道标识查找已注册的外部登录提供者；不存在时返回空。</summary>
    public IExternalLoginProvider? Find(string key) =>
        providers.TryGetValue(key, out var provider) ? provider : null;

    /// <summary>按标识排序返回已注册外部登录渠道的标识和展示名称。</summary>
    public IReadOnlyList<ExternalLoginProviderDescriptor> ListEnabled() =>
        providers.Values
            .OrderBy(provider => provider.Key, StringComparer.Ordinal)
            .Select(provider => new ExternalLoginProviderDescriptor(
                provider.Key,
                provider.DisplayName))
            .ToArray();
}
