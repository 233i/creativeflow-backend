using Microsoft.Extensions.DependencyInjection;
using CreativeFlow.Application.Abstractions.Identity;
using CreativeFlow.Application.Abstractions.Security;
using CreativeFlow.Infrastructure.Identity;
using CreativeFlow.Infrastructure.Security;

namespace CreativeFlow.Infrastructure;

/// <summary>按功能组织基础设施注册，保留同一公共装配入口。</summary>
public static partial class DependencyInjection
{
    /// <summary>注册身份安全与 OAuth 适配器，应用用例由 AddApplication 负责。</summary>
    private static void AddIdentity(IServiceCollection services)
    {
        services.AddSingleton<IFingerprintHasher, Sha256FingerprintHasher>();
        services.AddSingleton<IIpPrefixExtractor, IpPrefixExtractor>();
        services.AddSingleton<IPasswordCredentialHasher, AspNetCorePasswordCredentialHasher>();

        services.AddHttpClient("github-oauth", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddSingleton<IExternalLoginProvider, GitHubExternalLoginProvider>();
        services.AddSingleton<IExternalLoginProviderRegistry, ExternalLoginProviderRegistry>();
    }
}
