namespace CreativeFlow.Infrastructure.Options;

public sealed class PostgresOptions
{
    /// <summary>读取该配置对象时使用的现有配置节名称。</summary>
    public const string SectionName = "Postgres";

    /// <summary>PostgreSQL 连接配置，只在基础设施层使用。</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// 是否在启动时应用待处理迁移。开发环境中较为方便；生产环境应优先把同一二进制文件以
    /// <c>--migrate-only</c> 方式作为部署步骤运行，使架构变更成为显式操作而非发布副作用。
    /// </summary>
    public bool RunMigrationsOnStartup { get; set; }
}

public sealed class RedisOptions
{
    /// <summary>读取该配置对象时使用的现有配置节名称。</summary>
    public const string SectionName = "Redis";

    /// <summary>Redis 客户端连接配置。</summary>
    public string Configuration { get; set; } = string.Empty;

    /// <summary>Redis key 的实例命名空间前缀。</summary>
    public string InstanceName { get; set; } = "creativeflow:";
}

public sealed class RabbitMqOptions
{
    /// <summary>读取该配置对象时使用的现有配置节名称。</summary>
    public const string SectionName = "RabbitMq";

    /// <summary>RabbitMQ 服务主机名。</summary>
    public string HostName { get; set; } = "localhost";
    /// <summary>RabbitMQ 连接端口。</summary>
    public int Port { get; set; } = 5672;
    /// <summary>基础设施连接使用的用户名。</summary>
    public string UserName { get; set; } = "guest";
    /// <summary>当前认证输入的明文密码，仅用于即时处理。</summary>
    public string Password { get; set; } = "guest";
    /// <summary>RabbitMQ 的虚拟主机配置。</summary>
    public string VirtualHost { get; set; } = "/";
    /// <summary>RabbitMQ 集成事件交换机名称。</summary>
    public string Exchange { get; set; } = "creativeflow.events";
    /// <summary>RabbitMQ 消费队列名称。</summary>
    public string QueueName { get; set; } = "creativeflow.events.api";
    /// <summary>RabbitMQ 消费者允许的未确认消息数量。</summary>
    public ushort PrefetchCount { get; set; } = 16;

    /// <summary>消费者绑定的路由键。为空表示不绑定任何内容并保持空闲。</summary>
    public string[] RoutingKeys { get; set; } = ["device.#"];
}

public sealed class SkillsOptions
{
    /// <summary>读取该配置对象时使用的现有配置节名称。</summary>
    public const string SectionName = "Skills";

    /// <summary>
    /// 允许通过隔离运行器执行技能脚本。禁用后仍可读取技能说明和资源，
    /// 但绝不会提供或执行脚本。
    /// </summary>
    public bool SandboxEnabled { get; set; }

    /// <summary>Skill 包的唯一持久化目录，不保存数据库文件副本。</summary>
    public string Root { get; set; } = "./storage/skills";
    /// <summary>正式部署要求目录预先包含卷标记，防止挂载丢失后写入容器层。</summary>
    public bool RequireStorageMarker { get; set; }
    /// <summary>上传包或素材允许的最大字节数。</summary>
    public int MaxUploadBytes { get; set; } = 25 * 1024 * 1024;
    /// <summary>Skill 包解压后允许的总字节数。</summary>
    public int MaxExtractedBytes { get; set; } = 100 * 1024 * 1024;
    /// <summary>Skill 包允许的最大条目数。</summary>
    public int MaxEntries { get; set; } = 1_000;
    /// <summary>OpenSandbox API 的地址。</summary>
    public string OpenSandboxDomain { get; set; } = "localhost:8090";
    /// <summary>访问 OpenSandbox 的服务端密钥。</summary>
    public string OpenSandboxApiKey { get; set; } = string.Empty;
    /// <summary>每次隔离执行使用的沙箱镜像。</summary>
    public string OpenSandboxImage { get; set; } = "opensandbox/code-interpreter:v1.1.0";
    /// <summary>脚本执行的默认超时秒数。</summary>
    public int RunnerTimeoutSeconds { get; set; } = 30;
    /// <summary>脚本运行器允许返回的最大输出字节数。</summary>
    public int RunnerMaxOutputBytes { get; set; } = 256 * 1024;
    /// <summary>发送到脚本运行器的包大小上限。</summary>
    public int RunnerMaxPackageBytes { get; set; } = 25 * 1024 * 1024;
}

public sealed class McpOptions
{
    /// <summary>读取该配置对象时使用的现有配置节名称。</summary>
    public const string SectionName = "Mcp";

    /// <summary>仅当所有解析出的地址都是回环地址时允许明文 HTTP。</summary>
    public bool AllowLoopbackHttp { get; set; }

    /// <summary>上游网络请求的超时秒数。</summary>
    public int RequestTimeoutSeconds { get; set; } = 30;
    /// <summary>MCP 响应允许读取的最大字节数。</summary>
    public int MaxResponseBytes { get; set; } = 1_048_576;
}

public sealed class AiOptions
{
    /// <summary>读取该配置对象时使用的现有配置节名称。</summary>
    public const string SectionName = "Ai";

    /// <summary>
    /// 用于加密已存储供应商 API 密钥的 32 字节 Base64 编码密钥。
    /// </summary>
    /// <remarks>
    /// 轮换此值会使所有已存储密钥无法解密，用户必须逐一重新输入。与轮换后只会丢失概率性关联的
    /// <c>DeviceIdentity:Pepper</c> 不同，轮换此密钥会破坏数据。应将其视为持久秘密材料。
    /// </remarks>
    public string EncryptionKey { get; set; } = string.Empty;

    /// <summary>生成图片和视频的写入位置。数据库中只存储路径。</summary>
    public string AssetRoot { get; set; } = "./storage/assets";
    /// <summary>正式部署要求素材目录预先包含 .creativeflow-storage 标记。</summary>
    public bool RequireStorageMarker { get; set; }
    /// <summary>单个文件保存的硬上限；各上传/下载入口可以进一步收紧。</summary>
    public long MaxAssetBytes { get; set; } = 512L * 1024 * 1024;
    /// <summary>文件流写入最长时间，必须短于十五分钟的写入租约。</summary>
    public int AssetWriteTimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// 无论配置要求为何，应用都会遵守的最短上游超时时间。
    /// </summary>
    /// <remarks>
    /// 图片和视频模型通常需要渲染数分钟，中途切断请求并不等于重试——供应商仍会继续渲染，
    /// 用户仍会被计费，而结果会被丢弃。十分钟是最低值，低于此值后这种情况会从偶发变为常见。
    /// </remarks>
    public const int MinimumRequestTimeoutSeconds = 600;

    /// <summary>
    /// 等待上游生成调用的时长。绑定时，低于 <see cref="MinimumRequestTimeoutSeconds"/> 的值
    /// 会被提升到该下限。
    /// </summary>
    /// <remarks>
    /// 此值必须与 <c>web/next.config.ts</c> 中的 <c>experimental.proxyTimeout</c> 保持同步。
    /// 两者中较短的值会真正终止请求；若由代理终止，失败会表现为客户端断开而非超时，
    /// 看起来会像完全不同的问题。
    /// </remarks>
    public int RequestTimeoutSeconds { get; set; } = 1200;

    /// <summary>为上游模型签发的无 Cookie 媒体 URL 的有效期。</summary>
    public int SignedMediaUrlLifetimeMinutes { get; set; } = 60;
}

public sealed class ExternalWorkflowApiOptions
{
    /// <summary>读取该配置对象时使用的现有配置节名称。</summary>
    public const string SectionName = "ExternalWorkflowApi";

    /// <summary>当前配置或资源是否启用。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Optional base64-encoded secret used only for API-key HMACs. When omitted, the AI encryption
    /// key is used as input to a context-separated derivation so existing deployments keep working.
    /// </summary>
    public string ApiKeyHashSecret { get; set; } = string.Empty;

    /// <summary>外部图片允许的最大字节数。</summary>
    public int MaxImageBytes { get; set; } = 20 * 1024 * 1024;
    /// <summary>外部图片允许的总像素数上限。</summary>
    public int MaxImagePixels { get; set; } = 40_000_000;
    /// <summary>外部图片单边允许的最大像素数。</summary>
    public int MaxImageDimension { get; set; } = 16_384;
    /// <summary>同一工作流允许等待执行的外部运行数量上限。</summary>
    public int MaxQueuedRunsPerWorkflow { get; set; } = 100;
    /// <summary>单个工作流宿主创建的并发执行循环数量。</summary>
    public int GlobalConcurrency { get; set; } = 4;
    /// <summary>新发布工作流默认允许的并发运行数量。</summary>
    public int DefaultWorkflowConcurrency { get; set; } = 2;
    /// <summary>外部上传素材的有效期小时数。</summary>
    public int UploadedAssetLifetimeHours { get; set; } = 24;
    /// <summary>每个已验证 API Key 每分钟允许创建运行的次数。</summary>
    public int CreateRunPermitLimitPerMinute { get; set; } = 30;
    /// <summary>每个已验证 API Key 每分钟允许上传图片的次数。</summary>
    public int ImageUploadPermitLimitPerMinute { get; set; } = 30;
    /// <summary>每个已验证 API Key 每分钟允许查询状态的次数。</summary>
    public int StatusPermitLimitPerMinute { get; set; } = 300;
    /// <summary>每个 API 进程同时保留的 SSE 订阅上限。</summary>
    public int MaxConcurrentStreams { get; set; } = 128;
    /// <summary>同一 Key 在每个 API 进程的 SSE 订阅上限。</summary>
    public int MaxStreamsPerApiKey { get; set; } = 8;
}
