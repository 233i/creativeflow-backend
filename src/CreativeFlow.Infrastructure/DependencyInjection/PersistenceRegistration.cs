using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using CreativeFlow.Application.Abstractions.Identity;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Infrastructure.Identity;
using CreativeFlow.Infrastructure.Options;
using CreativeFlow.Infrastructure.Persistence;
using CreativeFlow.Infrastructure.Persistence.Migrations;

namespace CreativeFlow.Infrastructure;

/// <summary>按功能组织基础设施注册，保留同一公共装配入口。</summary>
public static partial class DependencyInjection
{
    /// <summary>注册带正确作用域的数据库连接、仓储与迁移运行器。</summary>
    private static void AddPersistence(IServiceCollection services)
    {
        // 仓储和连接工厂保持原有作用域；数据源由统一配置构造，数据库迁移仍使用既有嵌入脚本。
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<PostgresOptions>>().Value;
            if (string.IsNullOrWhiteSpace(options.ConnectionString))
            {
                throw new InvalidOperationException(
                    $"{PostgresOptions.SectionName}:ConnectionString is not configured.");
            }

            return NpgsqlDataSourceFactory.Create(options.ConnectionString);
        });

        services.AddScoped<IAccountContext, AccountContext>();
        services.AddScoped<CreativeFlowConnectionFactory>();
        services.AddScoped<IAccountRepository, AccountRepository>();
        services.AddScoped<IAdminRepository, AdminRepository>();
        services.AddScoped<IRequestHistoryRepository, RequestHistoryRepository>();
        services.AddScoped<IDeviceRepository, DeviceRepository>();
        services.AddScoped<IVisitorRepository, VisitorRepository>();
        services.AddScoped<IProviderRepository, ProviderRepository>();
        services.AddScoped<IModelSlotRepository, ModelSlotRepository>();
        services.AddScoped<IImageGenerationSettingsRepository, ImageGenerationSettingsRepository>();
        services.AddScoped<IMcpServerRepository, McpServerRepository>();
        services.AddScoped<ICanvasAssetRepository, CanvasAssetRepository>();
        services.AddScoped<IAssetCenterRepository, AssetCenterRepository>();
        services.AddScoped<ICanvasRepository, CanvasRepository>();
        services.AddScoped<ICreativeAppRepository, CreativeAppRepository>();
        services.AddScoped<CreativeFlow.Application.Features.CreativeApps.CreativeAppService>();
        services.AddScoped<IWorkflowRepository, WorkflowRepository>();
        services.AddScoped<IWorkflowCenterRepository, WorkflowCenterRepository>();
        services.AddScoped<IWorkflowTemplateRepository, WorkflowTemplateRepository>();
        services.AddScoped<IWorkflowRunRepository, WorkflowRunRepository>();
        services.AddScoped<IWorkflowRunEventRepository, WorkflowRunEventRepository>();
        services.AddScoped<IWorkflowRunJournal, WorkflowRunJournal>();
        services.AddScoped<IWorkflowExternalApiRepository, WorkflowExternalApiRepository>();
        services.AddScoped<IVideoJobRepository, VideoJobRepository>();
        services.AddScoped<IImageJobRepository, ImageJobRepository>();
        services.AddScoped<IAgentRepository, AgentRepository>();
        services.AddScoped<ISkillRepository, SkillRepository>();
        services.AddSingleton<MigrationRunner>();
    }
}
