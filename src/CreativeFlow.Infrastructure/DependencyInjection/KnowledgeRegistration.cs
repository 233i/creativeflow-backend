using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Features.Knowledge;
using CreativeFlow.Infrastructure.Ai.Embeddings;
using CreativeFlow.Infrastructure.Ai.Knowledge;
using CreativeFlow.Infrastructure.Persistence;
using CreativeFlow.Infrastructure.Options;

namespace CreativeFlow.Infrastructure;

public static partial class DependencyInjection
{
    /// <summary>注册受配置开关控制的知识库能力；生产仍默认关闭，启用时只能连接受控回环 Qdrant。</summary>
    private static void AddKnowledge(IServiceCollection services, IConfiguration configuration)
    {
        var enabled = bool.TryParse(configuration["Knowledge:Enabled"], out var configured) && configured;
        var quotas = new KnowledgeQuotas
        {
            MaxLibraries = ReadKnowledgeQuota(configuration, "MaxLibraries", 10),
            MaxVersions = ReadKnowledgeQuota(configuration, "MaxVersions", 10),
            MaxDocuments = ReadKnowledgeQuota(configuration, "MaxDocuments", 8),
            MaxDocumentBytes = ReadKnowledgeQuota(configuration, "MaxDocumentBytes", 65536),
            MaxVersionBytes = ReadKnowledgeQuota(configuration, "MaxVersionBytes", 65536),
            MaxChunks = ReadKnowledgeQuota(configuration, "MaxChunks", 128),
            MaxLibraryReservedBytes = ReadKnowledgeLongQuota(configuration, "MaxLibraryReservedBytes", 67108864),
        };
        services.AddSingleton(quotas.Validate());
        services.AddSingleton(sp => new KnowledgeStorageOptions
        {
            Enabled = enabled,
            Development = sp.GetService<IHostEnvironment>()?.IsDevelopment() == true,
            Endpoint = configuration["Knowledge:Qdrant:Endpoint"] ?? "",
            ApiKey = configuration["Knowledge:Qdrant:ApiKey"] ?? "",
            Namespace = configuration["Knowledge:Qdrant:Namespace"] ?? "",
        });
        services.AddHttpClient("knowledge-qdrant", client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false, UseProxy = false, UseCookies = false,
                ConnectTimeout = TimeSpan.FromSeconds(5), PooledConnectionLifetime = TimeSpan.FromMinutes(1),
            });
        services.AddScoped<IKnowledgeRepository, KnowledgeRepository>();
        services.AddScoped<IKnowledgeManagementRepository, KnowledgeRepository>();
        services.AddScoped<KnowledgeManagementService>();
        services.AddScoped<IKnowledgeAccessRepository, KnowledgeAccessRepository>();
        services.AddScoped<IRerankSettingsRepository, RerankSettingsRepository>();
        services.AddSingleton(sp => new RerankNetwork(new EmbeddingOptions
        {
            AllowedEndpoints = configuration.GetSection("Rerank:AllowedEndpoints").GetChildren()
                .Select(child => child.Value).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).ToArray(),
        }, sp.GetService<IHostEnvironment>()?.IsDevelopment() == true));
        services.AddHttpClient("knowledge-rerank", client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(sp => new SocketsHttpHandler
            {
                AllowAutoRedirect = false, UseProxy = false, UseCookies = false,
                ConnectTimeout = TimeSpan.FromSeconds(10), PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                ConnectCallback = sp.GetRequiredService<RerankNetwork>().Policy.ConnectAsync,
            });
        services.AddSingleton<IRerankClient, RerankClient>();
        services.AddScoped<IKnowledgeReranker, KnowledgeReranker>();
        services.AddScoped<KnowledgeRetrievalService>();
        services.AddScoped<KnowledgeEvaluationService>();
        services.AddScoped<KnowledgeWorkflowService>();
        services.AddSingleton(new KnowledgeWorkflowOptions(enabled));
        services.AddScoped<IKnowledgeEmbedding, KnowledgeEmbedding>();
        services.AddSingleton<IKnowledgeIndexStore, QdrantKnowledgeStore>();
        services.AddScoped<KnowledgeLibraryService>();
        services.AddScoped<KnowledgeIndexProcessor>();
        services.AddHostedService<KnowledgeIndexWorker>();
    }

    /// <summary>按固定整数解析部署配额，不使用反射配置绑定；非法值导致启动失败。</summary>
    private static int ReadKnowledgeQuota(IConfiguration configuration, string key, int fallback) =>
        configuration["Knowledge:Quotas:" + key] is not { } value ? fallback
            : int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                ? parsed : throw new KnowledgeException("知识配额配置不是有效整数：" + key);

    /// <summary>读取允许超过 32 位整数范围的逻辑存储配额，并在配置错误时拒绝启动。</summary>
    private static long ReadKnowledgeLongQuota(IConfiguration configuration, string key, long fallback) =>
        configuration["Knowledge:Quotas:" + key] is not { } value ? fallback
            : long.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                ? parsed : throw new KnowledgeException("知识配额配置不是有效整数：" + key);
}
