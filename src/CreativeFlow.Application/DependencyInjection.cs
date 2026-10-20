using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using CreativeFlow.Application.Contracts.Device;
using CreativeFlow.Application.Features.Agent;
using CreativeFlow.Application.Features.Generation;
using CreativeFlow.Application.Features.Identity;

namespace CreativeFlow.Application;

/// <summary>应用层自行声明用例与验证器的生命周期，基础设施只负责实现其端口。</summary>
public static class DependencyInjection
{
    /// <summary>显式注册应用用例；重复调用不会创建重复注册或覆盖调用方提供的替身。</summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        // 这些用例依赖请求/运行的账号范围和仓储，必须与它们共享 scoped 生命周期。
        services.TryAddScoped<BackgroundAccountScope>();
        services.TryAddScoped<ResolveDeviceIdentity>();
        services.TryAddScoped<AccountAuthService>();
        services.TryAddScoped<PasswordAuthService>();
        services.TryAddScoped<AdminAuthService>();
        services.TryAddScoped<AgentTurnRunner>();
        services.TryAddScoped<AgentModelResolver>();
        services.TryAddScoped<AgentModelMessageConverter>();
        services.TryAddScoped<AgentRunEventRecorder>();
        services.TryAddScoped<AgentToolExecutionService>();
        services.TryAddScoped<AgentToolDiscovery>();
        services.TryAddScoped<AgentToolApprovalPolicy>();
        services.TryAddScoped<AgentSkillTools>();
        services.TryAddScoped<GenerateImagesUseCase>();
        services.TryAddScoped<ImageJobService>();
        services.TryAddScoped<GenerateTextUseCase>();
        services.TryAddScoped<CreativeFlow.Application.Features.Administration.RequestHistoryService>();
        services.TryAddScoped<CreativeFlow.Application.Features.Knowledge.EmbeddingModelService>();
        services.TryAddScoped<CreativeFlow.Application.Features.Knowledge.RerankModelService>();

        // 验证器没有请求状态；显式注册避免运行时扫描范围随目录调整漂移。
        services.TryAddSingleton<IValidator<DeviceSignalsPayload>, DeviceSignalsValidator>();
        return services;
    }
}
