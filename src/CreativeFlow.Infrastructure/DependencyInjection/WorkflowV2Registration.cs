using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Features.Workflow;
using CreativeFlow.Infrastructure.Ai;
using CreativeFlow.Infrastructure.Ai.Embeddings;
using CreativeFlow.Infrastructure.Options;

namespace CreativeFlow.Infrastructure;

/// <summary>新版节点使用单独受限的模型网络连接，调度仍使用现有唯一 MAF 引擎。</summary>
public static partial class DependencyInjection
{
    /// <summary>绑定独立端点许可，注册无自动重试、无重定向的模型客户端。</summary>
    private static void AddWorkflowV2(IServiceCollection services, IConfiguration configuration)
    {
        AddWorkflowHttp(services, configuration);
        var connectTimeout = WorkflowV2ConnectTimeout(configuration["Workflow:ConnectTimeoutSeconds"]);
        services.AddSingleton(sp => CreateWorkflowV2Network(configuration,
            sp.GetService<IHostEnvironment>()?.IsDevelopment() == true));
        services.AddTransient<WorkflowV2RequestGuard>();
        services.AddHttpClient("workflow-model", client =>
            {
                client.Timeout = Timeout.InfiniteTimeSpan;
                client.MaxResponseContentBufferSize = 1024 * 1024;
            })
            .AddHttpMessageHandler<WorkflowV2RequestGuard>()
            .ConfigurePrimaryHttpMessageHandler(sp => new SocketsHttpHandler
            {
                AllowAutoRedirect = false, UseProxy = false, UseCookies = false,
                ConnectTimeout = connectTimeout, PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                ConnectCallback = sp.GetRequiredService<WorkflowV2Network>().Policy.ConnectAsync,
            });
        services.AddScoped<IWorkflowV2Models, WorkflowV2Models>();
        services.AddScoped<IWorkflowV2AssetResolver, WorkflowV2AssetResolver>();
        services.AddScoped<WorkflowModelCallTrace>();
        services.AddScoped<WorkflowPublicTextStream>();
        services.AddScoped<WorkflowV2PublicationService>();
    }

    /// <summary>仅工作流允许显式配置 https://*；默认仍为精确许可，不继承向量服务或外部操作的配置。</summary>
    internal static WorkflowV2Network CreateWorkflowV2Network(IConfiguration configuration, bool isDevelopment) =>
        new(bool.TryParse(configuration["Workflow:Enabled"], out var enabled) && enabled,
            new EmbeddingEndpointPolicy(new EmbeddingOptions
            {
                AllowedEndpoints = configuration.GetSection("Workflow:AllowedEndpoints").GetChildren()
                    .Select(child => child.Value).OfType<string>().ToArray(),
            }, isDevelopment, allowPublicHttpsWildcard: true, allowGeminiSseQuery: true));

    /// <summary>连接阶段包含受控 DNS 解析，允许部署在 1–60 秒内配置，未配置保持原 10 秒。</summary>
    internal static TimeSpan WorkflowV2ConnectTimeout(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return TimeSpan.FromSeconds(10);
        if (!int.TryParse(value, out var seconds) || seconds is < 1 or > 60)
            throw new InvalidOperationException("Workflow:ConnectTimeoutSeconds must be an integer from 1 to 60.");
        return TimeSpan.FromSeconds(seconds);
    }
}
