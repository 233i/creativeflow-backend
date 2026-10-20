using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Infrastructure.Ai.Embeddings;
using CreativeFlow.Infrastructure.Options;
using CreativeFlow.Infrastructure.Persistence;

namespace CreativeFlow.Infrastructure;

/// <summary>向量接入独立装配，保留原媒体、文本、MCP 和 MAF 注册行为。</summary>
public static partial class DependencyInjection
{
    /// <summary>注册版本配置仓储、受限 HTTP 客户端及唯一向量分派器。</summary>
    private static void AddEmbedding(IServiceCollection services, IConfiguration configuration)
    {
        var options = new EmbeddingOptions
        {
            AllowedEndpoints = configuration.GetSection("Embedding:AllowedEndpoints").GetChildren()
                .Select(child => child.Value).Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!).ToArray(),
        };
        services.AddSingleton(options);
        services.AddSingleton(sp => new EmbeddingEndpointPolicy(options,
            sp.GetService<IHostEnvironment>()?.IsDevelopment() == true));
        services.AddHttpClient("embedding", client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(sp => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false,
                UseCookies = false,
                ConnectTimeout = TimeSpan.FromSeconds(10),
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                ConnectCallback = sp.GetRequiredService<EmbeddingEndpointPolicy>().ConnectAsync,
            });
        services.AddScoped<IEmbeddingSettingsRepository, EmbeddingSettingsRepository>();
        services.AddSingleton<IEmbeddingClient, OpenAiEmbeddingClient>();
        services.AddSingleton<IEmbeddingDispatcher, EmbeddingDispatcher>();
    }
}
