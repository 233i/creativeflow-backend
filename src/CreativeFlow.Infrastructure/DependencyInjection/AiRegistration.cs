using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Common;
using CreativeFlow.Application.Abstractions.Realtime;
using CreativeFlow.Application.Abstractions.Security;
using CreativeFlow.Application.Features.Identity;
using CreativeFlow.Infrastructure.Ai;
using CreativeFlow.Infrastructure.Ai.Maf;
using CreativeFlow.Infrastructure.Ai.Mcp;
using CreativeFlow.Infrastructure.Ai.Protocols;
using CreativeFlow.Infrastructure.Options;
using CreativeFlow.Infrastructure.Security;
using CreativeFlow.Infrastructure.Skills;

namespace CreativeFlow.Infrastructure;

/// <summary>按功能组织基础设施注册，保留同一公共装配入口。</summary>
public static partial class DependencyInjection
{
    /// <summary>显式注册上游协议、媒体、MCP、Skill 和后台执行适配器。</summary>
    private static void AddAi(
        IServiceCollection services,
        AiOptions options,
        ExternalWorkflowApiOptions externalWorkflowOptions,
        SkillsOptions skillsOptions)
    {
        // 立即构造，使缺失或格式错误的加密密钥在启动时失败，
        // 而不是等到首次尝试保存密钥的请求才失败。
        services.AddSingleton<IApiKeyProtector>(new AesGcmApiKeyProtector(options));
        services.AddSingleton<IWorkflowApiKeyService>(
            new WorkflowApiKeyService(externalWorkflowOptions, options));
        services.AddSingleton<ISignedMediaUrlService>(sp => new SignedMediaUrlService(
            options,
            sp.GetRequiredService<IOptions<AuthenticationOptions>>().Value,
            sp.GetRequiredService<IClock>()));

        // 使用命名客户端而非类型化客户端：类型化客户端通过反射绑定，在 AOT 下存在裁剪风险。
        //
        // 较长的超时是有意设置的——图片生成经常超过一分钟，默认 100 秒会将正常模型误报为失败。
        services.AddHttpClient("upstream", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds);
        });
        services.AddHttpClient("image-output", client => client.Timeout = TimeSpan.FromSeconds(120))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddSingleton<IImageOutputDownloader, ImageOutputDownloader>();
        services.AddScoped<ImageJobRunner>();
        services.AddHostedService<ImageJobWorker>();

        services.AddSingleton<IUpstreamModelCatalog, UpstreamModelCatalog>();

        services.AddSingleton<AssetFileSystem>();
        services.AddSingleton<FileSystemAssetStore>();
        services.AddSingleton<IAssetStore>(sp => sp.GetRequiredService<FileSystemAssetStore>());
        services.AddHostedService<AssetStorageCleanupWorker>();
        services.AddSingleton<IAssetPreviewService, AssetPreviewService>();
        services.AddSingleton<IVideoFrameExtractor, FfmpegVideoFrameExtractor>();
        services.AddScoped<IVideoFrameService, VideoFrameService>();

        // 每种协议对应一个客户端。分派器按 ProviderType 建立索引，
        // 因此新增供应商只需在此注册，无需修改分支语句。
        services.AddSingleton<OpenAiImagesClient>();
        services.AddSingleton<IImageGenerationClient>(services => services.GetRequiredService<OpenAiImagesClient>());
        services.AddSingleton<IImageGenerationClient, SeedreamImagesClient>();
        services.AddSingleton<IImageGenerationClient, GeminiImagesClient>();
        services.AddSingleton<IImageGenerationDispatcher, ImageGenerationDispatcher>();

        services.AddSingleton<IVideoGenerationClient, SeedanceVideoClient>();
        services.AddSingleton<IVideoGenerationClient, OpenAiVideoClient>();
        services.AddSingleton<IVideoGenerationClient, MiniMaxVideoClient>();
        services.AddSingleton<IVideoGenerationClient, MiniMaxH3VideoClient>();
        services.AddSingleton<IVideoGenerationClient, XaiVideoClient>();
        services.AddSingleton<IVideoGenerationDispatcher, VideoGenerationDispatcher>();

        services.AddSingleton<ITextGenerationClient, OpenAiChatClient>();
        services.AddSingleton<ITextGenerationClient, OpenAiResponsesClient>();
        services.AddSingleton<ITextGenerationClient, AnthropicTextClient>();
        services.AddSingleton<ITextGenerationClient, GeminiTextClient>();
        services.AddSingleton<ITextGenerationDispatcher, TextGenerationDispatcher>();

        services.AddSingleton<IMcpNetworkGuard, McpNetworkGuard>();
        services.AddSingleton<IMcpRuntime, McpRuntime>();
        services.AddSingleton<ISkillPackageScanner, ZipSkillScanner>();
        services.AddSingleton<FileSystemSkillStore>();
        services.AddSingleton<ISkillStore>(sp => sp.GetRequiredService<FileSystemSkillStore>());
        if (skillsOptions.SandboxEnabled)
        {
            services.AddSingleton<ISkillScriptRunner, OpenSandboxSkillScriptRunner>();
        }
        else
        {
            services.AddSingleton<ISkillScriptRunner, DisabledSkillScriptRunner>();
        }
        // 通用编排脚本复用同一隔离边界；不在工作流路径中直接依赖 OpenSandbox SDK。
        services.AddSingleton<IWorkflowScriptRunner, OpenSandboxWorkflowScriptRunner>();

        services.AddSingleton<IAgentModelClient, AnthropicAgentClient>();
        services.AddSingleton<IAgentModelClient, OpenAiChatAgentClient>();
        services.AddSingleton<IAgentModelClient, OpenAiResponsesAgentClient>();
        services.AddSingleton<IAgentModelDispatcher, AgentModelDispatcher>();
        services.AddSingleton<IAgentCanvasBridge, AgentCanvasBridge>();
        // 单一 MAF 调度端口；协议、工具、审批和持久化仍由原业务组件拥有。
        services.AddScoped<IAgentRuntime, MafAgentRuntime>();
        services.AddScoped<IWorkflowRuntime, MafWorkflowRuntime>();

        // 只注册一次，并通过两个接口公开。若同时调用 AddHostedService<T>() 和
        // AddSingleton<IAgentRunControl,T>()，会创建两个宿主，导致取消操作命中不同于执行运行的实例。
        services.AddSingleton<AgentRunHost>();
        services.AddSingleton<IAgentRunControl>(sp => sp.GetRequiredService<AgentRunHost>());
        services.AddHostedService(sp => sp.GetRequiredService<AgentRunHost>());

        // 在服务器端推进视频任务，使其可在启动它们的标签页关闭后继续运行。
        services.AddHostedService<VideoJobPoller>();

        // 工作流运行从 PostgreSQL 认领，因此关闭浏览器不会停止它们。
        services.AddWorkflowExecution();
        services.AddHostedService<WorkflowRunWorker>();
    }
}
