using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using CreativeFlow.Application.Features.Workflow;
using CreativeFlow.Infrastructure.Ai.Workflows;

namespace CreativeFlow.Infrastructure;

/// <summary>显式装配工作流编排与节点执行器；只有真正执行的节点才解析业务依赖。</summary>
internal static class WorkflowExecutionRegistration
{
    /// <summary>幂等注册同一套媒体、通用与调试执行路径，不注册额外调度引擎或后台宿主。</summary>
    internal static IServiceCollection AddWorkflowExecution(this IServiceCollection services)
    {
        if (services.Any(item => item.ServiceType == typeof(WorkflowRunOrchestrator))) return services;
        services.AddScoped<WorkflowRunOrchestrator>();
        services.AddScoped<WorkflowNodeExecutorRegistry>();
        services.AddScoped<WorkflowExecutionPolicy>();
        services.AddScoped<WorkflowProgressPublisher>();
        services.AddScoped<IWorkflowNodeProgress, WorkflowNodeProgressReporter>();
        services.AddScoped<WorkflowRunFinalizer>();
        services.AddScoped<WorkflowMediaModelResolver>();
        services.AddScoped<WorkflowMediaAssetReader>();
        services.AddScoped<WorkflowVideoJobAwaiter>();
        services.AddScoped<WorkflowNestedRunner>();

        Add<TextInputNodeExecutor>(services, WorkflowNodeFamily.Media, "text");
        Add<ImageInputNodeExecutor>(services, WorkflowNodeFamily.Media, "image");
        Add<ImageGenerationNodeExecutor>(services, WorkflowNodeFamily.Media, "imageGenerate");
        Add<VideoGenerationNodeExecutor>(services, WorkflowNodeFamily.Media, "videoGenerate");
        Add<VideoExtensionNodeExecutor>(services, WorkflowNodeFamily.Media, "videoExtend");
        Add<VideoFrameNodeExecutor>(services, WorkflowNodeFamily.Media, "videoFrame");
        Add<MediaOutputNodeExecutor>(services, WorkflowNodeFamily.Media, "imageOutput", "videoOutput", "output");

        Add<BoundaryNodeExecutor>(services, WorkflowNodeFamily.General, "start", "end", "merge");
        Add<ConditionNodeExecutor>(services, WorkflowNodeFamily.General, "condition");
        Add<TransformNodeExecutor>(services, WorkflowNodeFamily.General, "transform");
        Add<ScriptNodeExecutor>(services, WorkflowNodeFamily.General, "script");
        Add<LoopNodeExecutor>(services, WorkflowNodeFamily.General, "loop", "batch");
        Add<ModelNodeExecutor>(services, WorkflowNodeFamily.General, "llm", "parameterExtract", "intentClassify");
        Add<KnowledgeRetrievalNodeExecutor>(services, WorkflowNodeFamily.General, "knowledgeRetrieve");
        Add<KnowledgeAnswerNodeExecutor>(services, WorkflowNodeFamily.General, "knowledgeAnswer");
        Add<HttpRequestNodeExecutor>(services, WorkflowNodeFamily.General, "httpRequest");
        return services;
    }

    /// <summary>仅在装配边界捕获作用域；具体类型显式注册以支持启动校验，工厂仅负责延迟取得该执行器。</summary>
    private static void Add<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(
        IServiceCollection services, WorkflowNodeFamily family, params string[] types) where T : class, IWorkflowNodeExecutor
    {
        services.TryAddScoped<T>();
        foreach (var type in types)
            services.AddScoped(provider => new WorkflowNodeExecutorRegistration(family, type, provider.GetRequiredService<T>));
    }
}
