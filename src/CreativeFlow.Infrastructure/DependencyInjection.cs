using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using CreativeFlow.Application;
using CreativeFlow.Application.Abstractions.Common;
using CreativeFlow.Application.Features.Identity;
using CreativeFlow.Infrastructure.Common;
using CreativeFlow.Infrastructure.Health;
using CreativeFlow.Infrastructure.Options;

namespace CreativeFlow.Infrastructure;

public static partial class DependencyInjection
{
    /// <summary>绑定配置并装配应用用例、基础设施端口与后台宿主。</summary>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, IConfiguration configuration)
    {
        // 手动绑定而不使用 Configure<T>(IConfiguration)：基于反射的绑定器要求动态代码，
        // 在 AOT 下可能生成所有字段均为默认值的对象。
        services.AddSingleton<IOptions<PostgresOptions>>(
            Microsoft.Extensions.Options.Options.Create(OptionsBinder.BindPostgres(configuration)));
        services.AddSingleton<IOptions<RedisOptions>>(
            Microsoft.Extensions.Options.Options.Create(OptionsBinder.BindRedis(configuration)));
        services.AddSingleton<IOptions<RabbitMqOptions>>(
            Microsoft.Extensions.Options.Options.Create(OptionsBinder.BindRabbitMq(configuration)));
        services.AddSingleton<IOptions<DeviceIdentityOptions>>(
            Microsoft.Extensions.Options.Options.Create(OptionsBinder.BindDeviceIdentity(configuration)));
        services.AddSingleton<IOptions<AuthenticationOptions>>(
            Microsoft.Extensions.Options.Options.Create(OptionsBinder.BindAuthentication(configuration)));
        services.AddSingleton<IOptions<AdminOptions>>(
            Microsoft.Extensions.Options.Options.Create(OptionsBinder.BindAdmin(configuration)));

        var skillsOptions = OptionsBinder.BindSkills(configuration);
        services.AddSingleton<IOptions<SkillsOptions>>(
            Microsoft.Extensions.Options.Options.Create(skillsOptions));
        services.AddSingleton(skillsOptions);

        var mcpOptions = OptionsBinder.BindMcp(configuration);
        services.AddSingleton<IOptions<McpOptions>>(
            Microsoft.Extensions.Options.Options.Create(mcpOptions));
        services.AddSingleton(mcpOptions);

        var aiOptions = OptionsBinder.BindAi(configuration);
        services.AddSingleton<IOptions<AiOptions>>(
            Microsoft.Extensions.Options.Options.Create(aiOptions));
        services.AddSingleton(aiOptions);
        services.AddSingleton(OptionsBinder.BindImageQueue(configuration));

        var externalWorkflowOptions = OptionsBinder.BindExternalWorkflowApi(configuration);
        services.AddSingleton<IOptions<ExternalWorkflowApiOptions>>(
            Microsoft.Extensions.Options.Options.Create(externalWorkflowOptions));
        services.AddSingleton(externalWorkflowOptions);

        // 保留原公开入口的一站式行为；AddApplication 幂等，宿主也可单独显式调用。
        services.AddApplication();
        services.AddSingleton<IClock, SystemClock>();

        AddPersistence(services);
        AddCaching(services);
        AddMessaging(services);
        AddIdentity(services);
        AddAi(services, aiOptions, externalWorkflowOptions, skillsOptions);
        AddEmbedding(services, configuration);
        AddWorkflowV2(services, configuration);
        AddKnowledge(services, configuration);

        services.AddSingleton<FileStorageHealthProbe>();
        services.AddSingleton<DependencyHealthProbe>();

        return services;
    }
}
