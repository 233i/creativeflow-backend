using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Features.Workflow;
using CreativeFlow.Infrastructure.Ai.HttpRequests;
using CreativeFlow.Infrastructure.Persistence;

namespace CreativeFlow.Infrastructure;

public static partial class DependencyInjection
{
    /// <summary>注册独立 HTTP 节点传输；公网 HTTPS 为默认目标，显式空白名单禁止所有目标。</summary>
    private static void AddWorkflowHttp(IServiceCollection services, IConfiguration configuration)
    {
        // 显式配置必须可解析；空数组在配置提供程序中保留为 null 项，不能当作缺省值。
        var configured = configuration["Workflow:HttpRequests:Enabled"];
        var enabled = bool.TryParse(configuration["Workflow:Enabled"], out var workflow) && workflow
            && (configured is null || bool.TryParse(configured, out var http) && http);
        const string endpointKey = "Workflow:HttpRequests:AllowedEndpoints";
        var endpoints = configuration.GetSection(endpointKey).GetChildren().Select(child => child.Value).OfType<string>().ToArray();
        if (!configuration.AsEnumerable().Any(pair => pair.Key.Equals(endpointKey, StringComparison.OrdinalIgnoreCase))) endpoints = ["https://*"];
        services.AddSingleton(new WorkflowHttpOptions(enabled));
        services.AddSingleton(sp => new WorkflowHttpNetwork(endpoints, sp.GetService<IHostEnvironment>()?.IsDevelopment() == true));
        services.AddHttpClient("workflow-http-request", client => client.Timeout = Timeout.InfiniteTimeSpan).RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(sp => new SocketsHttpHandler
            {
                AllowAutoRedirect = false, UseProxy = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(10),
                PooledConnectionLifetime = TimeSpan.FromMinutes(1), ConnectCallback = sp.GetRequiredService<WorkflowHttpNetwork>().ConnectAsync,
            });
        services.AddScoped<IHttpCredentialRepository, HttpCredentialRepository>();
        services.AddScoped<IWorkflowHttpTransport, WorkflowHttpTransport>();
        services.AddScoped<HttpRequestService>();
    }
}
