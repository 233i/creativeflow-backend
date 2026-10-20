using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using CreativeFlow.Api.Controllers;
using CreativeFlow.Api.Hubs;
using CreativeFlow.Api.Http;
using CreativeFlow.Api.Serialization;
using CreativeFlow.Application;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Common;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Abstractions.Security;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;

namespace CreativeFlow.UnitTests.Support;

/// <summary>通过真实 MVC 与生产 JSON 元数据观察文本生成的参数、调用顺序、异常和取消。</summary>
/// <remarks>只在 HTTP 边界注入身份；不启动后台宿主，不连接真实数据库或供应商。</remarks>
internal sealed class TextGenerationHttpFixture : IAsyncDisposable
{
    /// <summary>由本夹具创建并负责停止的内存 Web 宿主。</summary>
    private WebApplication? _application;

    /// <summary>测试请求携带的设备范围，供素材归属断言使用。</summary>
    public DeviceId DeviceId { get; } = DeviceId.New();

    /// <summary>生成入口的记录替身，兼容测试同时经过生产采集过滤器。</summary>
    public RecordingRequestHistory History { get; } = new();

    /// <summary>所有外部读取和生成调用的顺序及取消令牌。</summary>
    public CallLog Log { get; } = new();

    /// <summary>可控制查询结果的渠道仓储。</summary>
    public RecordingProviders Providers { get; }

    /// <summary>仅允许读取的素材仓储。</summary>
    public RecordingAssets Assets { get; }

    /// <summary>记录内容读取和流释放的内存存储。</summary>
    public MemoryAssets Store { get; }

    /// <summary>只解密测试密钥，可注入指定异常。</summary>
    public TestProtector Protector { get; }

    /// <summary>记录非流式文本上游请求并返回受控结果。</summary>
    public RecordingText Text { get; }

    /// <summary>已连接内存测试服务器的 HTTP 客户端。</summary>
    public HttpClient Client { get; private set; } = null!;

    /// <summary>请求管线退出的信号，用于在取消后等待 finally 中的资源释放。</summary>
    public TaskCompletionSource RequestFinished { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>让各端口共享调用记录，跨端口校验失败顺序而不借助真实 I/O。</summary>
    private TextGenerationHttpFixture()
    {
        Providers = new(Log);
        Assets = new(Log);
        Store = new(Log);
        Protector = new(Log);
        Text = new(Log);
    }

    /// <summary>启动原有文本路由，可分别省略账号或设备以验证身份边界。</summary>
    public static async Task<TextGenerationHttpFixture> CreateAsync(
        bool hasAccount = true, bool hasDevice = true)
    {
        var fixture = new TextGenerationHttpFixture();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IRequestHistoryRepository>(fixture.History);
        builder.Services.AddControllers(options => options.Filters.Add<GenerationHistoryFilter>())
            .AddApplicationPart(typeof(AiGenerationController).Assembly)
            .AddJsonOptions(options => options.JsonSerializerOptions.TypeInfoResolverChain
                .Insert(0, ApiJsonContext.Default));
        builder.Services.AddSingleton<IProviderRepository>(fixture.Providers);
        builder.Services.AddSingleton<ICanvasAssetRepository>(fixture.Assets);
        builder.Services.AddSingleton<IAssetStore>(fixture.Store);
        builder.Services.AddHttpClient("image-output");
        builder.Services.AddSingleton<IImageOutputDownloader, CreativeFlow.Infrastructure.Ai.ImageOutputDownloader>();
        builder.Services.AddSingleton<IApiKeyProtector>(fixture.Protector);
        builder.Services.AddSingleton<ITextGenerationDispatcher>(fixture.Text);
        builder.Services.AddSingleton<IImageGenerationDispatcher, UnusedImageServices>();
        builder.Services.AddSingleton<IImageGenerationSettingsRepository, UnusedImageServices>();
        builder.Services.AddSingleton<IClock, UnusedImageServices>();
        builder.Services.AddApplication();

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            // 设备 Cookie 线索不能单独授权；账号和设备分别注入才能锁定这个边界。
            if (hasDevice) context.Items[DeviceContextItems.DeviceId] = fixture.DeviceId;
            if (hasAccount) context.Items[DeviceContextItems.AccountId] = AccountId.New();
            try { await next(context); }
            finally { fixture.RequestFinished.TrySetResult(); }
        });
        app.MapControllers();
        await app.StartAsync();
        fixture._application = app;
        fixture.Client = app.GetTestClient();
        return fixture;
    }

    /// <summary>构造原有文本请求 DTO，其他测试只覆盖各自关心的参数。</summary>
    public GenerateTextRequest Request() => new(
        Providers.Provider!.Id.ToString(), "chat-model", "  rewrite this\n",
        "keep the original meaning", null, null, 0.7);

    /// <summary>建立带可控归属、媒体类型和字节内容的参考素材，返回站内 URL。</summary>
    public string AddReference(
        byte[]? bytes = null, DeviceId? owner = null,
        string mediaType = "image/png", string kind = "image")
    {
        var id = Guid.CreateVersion7();
        var path = $"cloud/reference-{id:N}";
        var content = bytes ?? [1, 2, 3];
        Assets.Available[id] = new CanvasAsset(
            id, owner ?? DeviceId, kind, mediaType, path, content.Length, null, DateTimeOffset.UnixEpoch);
        Store.Available[path] = content;
        return $"/api/v1/canvas/assets/{id}";
    }

    /// <summary>向真实文本 HTTP 入口提交请求，可使用调用方令牌模拟请求中断。</summary>
    public Task<HttpResponseMessage> PostAsync(GenerateTextRequest request, CancellationToken? ct = null) =>
        Client.PostAsJsonAsync("/api/v1/ai/text", request, ct ?? TestContext.Current.CancellationToken);

    /// <summary>停止测试客户端和宿主，避免状态或请求跨测试存续。</summary>
    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        if (_application is not null) await _application.DisposeAsync();
    }

    /// <summary>记录端口顺序，并在指定 I/O 阶段确定性地模拟异常或等待取消。</summary>
    internal sealed class CallLog
    {
        /// <summary>按执行顺序记录的渠道、密钥、素材、流和上游阶段。</summary>
        public List<string> Entries { get; } = [];

        /// <summary>每个异步端口收到的取消令牌，不包含同步密钥解密。</summary>
        public List<CancellationToken> Tokens { get; } = [];

        /// <summary>应抛出 Failure 的阶段，未指定时不注入异常。</summary>
        public string? FailureStage { get; set; }

        /// <summary>模拟的基础设施异常实例，用于验证不吞掉或改写异常。</summary>
        public IOException Failure { get; } = new("Simulated text generation I/O failure.");

        /// <summary>等待请求取消的阶段，未指定时各替身立即完成。</summary>
        public string? BlockedStage { get; set; }

        /// <summary>阻塞阶段开始的信号，使测试只在 I/O 正在执行时取消请求。</summary>
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>端口收到取消的信号，区分客户端中止与服务端真实取消。</summary>
        public TaskCompletionSource CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>记录调用后注入受控故障，取消时继续传播原 OperationCanceledException。</summary>
        public async Task RecordAsync(string stage, CancellationToken ct)
        {
            Entries.Add(stage);
            Tokens.Add(ct);
            if (stage == FailureStage) throw Failure;
            if (stage != BlockedStage) return;

            // 使用请求令牌结束等待，不把客户端取消伪装成固定延时或上游失败结果。
            Entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException)
            {
                CancellationObserved.TrySetResult();
                throw;
            }
        }
    }

    /// <summary>提供受控聊天模型，禁止文本请求修改渠道配置。</summary>
    internal sealed class RecordingProviders(CallLog log) : StubProviderRepository
    {
        /// <summary>可返回的渠道；设为空可模拟不存在的渠道。</summary>
        public AiProvider? Provider { get; set; } = CreateProvider();

        /// <summary>最后一次查询使用的渠道标识。</summary>
        public ProviderId? RequestedId { get; private set; }

        /// <summary>记录请求的渠道标识及操作顺序，只返回标识匹配的预设渠道。</summary>
        public override async Task<AiProvider?> FindAsync(ProviderId providerId, CancellationToken ct)
        {
            RequestedId = providerId;
            await log.RecordAsync("provider", ct);
            return Provider?.Id == providerId ? Provider : null;
        }

        /// <summary>创建不含真实凭据的渠道，模型声明输出上限但不声明视觉支持。</summary>
        private static AiProvider CreateProvider()
        {
            var id = ProviderId.New();
            return new AiProvider(
                id, null, "Text provider", null, null, ProviderType.OpenAiChat,
                null, "https://text-provider.invalid", true, null, 0,
                DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)
            {
                Models = [new AiModel(
                    ModelId.New(), id, "chat-model", "Chat", true, null, ModelCategory.Chat,
                    null, 8192, 4096, false, false, false, 0, DateTimeOffset.UnixEpoch)],
            };
        }
    }

    /// <summary>模拟按传入设备读取素材；未覆盖的元数据写入会由基类直接拒绝。</summary>
    internal sealed class RecordingAssets(CallLog log) : StubCanvasAssetRepository
    {
        /// <summary>可查询的素材元数据，包含测试刻意放入的其他设备记录。</summary>
        public Dictionary<Guid, CanvasAsset> Available { get; } = [];

        /// <summary>每次素材查询收到的设备范围。</summary>
        public List<DeviceId> RequestedDevices { get; } = [];

        /// <summary>记录素材查询设备和操作顺序，仅返回属于请求设备的内存素材。</summary>
        public override async Task<CanvasAsset?> FindAsync(DeviceId deviceId, Guid assetId, CancellationToken ct)
        {
            RequestedDevices.Add(deviceId);
            await log.RecordAsync("asset", ct);
            return Available.TryGetValue(assetId, out var asset) && asset.DeviceId == deviceId ? asset : null;
        }
    }

    /// <summary>只提供内存读取，记录访问键并禁止文本生成保存或删除素材。</summary>
    internal sealed class MemoryAssets(CallLog log) : TestAssetStore
    {
        /// <summary>按不透明存储键保存的测试字节。</summary>
        public Dictionary<string, byte[]> Available { get; } = [];

        /// <summary>按调用顺序读取的存储键，包含缺失内容的读取尝试。</summary>
        public List<string> OpenedPaths { get; } = [];

        /// <summary>已打开的流，用于在成功、失败和取消后验证释放。</summary>
        public List<ObservedStream> Streams { get; } = [];

        /// <summary>记录打开顺序与存储键，为存在的内存素材创建可观察复制和释放行为的流。</summary>
        public override async Task<Stream?> OpenReadAsync(string storagePath, CancellationToken ct)
        {
            OpenedPaths.Add(storagePath);
            await log.RecordAsync("open", ct);
            if (!Available.TryGetValue(storagePath, out var bytes)) return null;
            var stream = new ObservedStream(bytes, log);
            Streams.Add(stream);
            return stream;
        }

        /// <summary>拒绝文本生成过程中保存素材字节。</summary>
        public override Task<StoredAsset> SaveAsync(
            byte[] bytes, string mediaType, string extensionHint, CancellationToken ct) =>
            throw new NotSupportedException("Text generation must not save assets.");

        /// <summary>拒绝文本生成过程中删除素材字节。</summary>
        public override Task DeleteAsync(string storagePath, CancellationToken ct) =>
            throw new NotSupportedException("Text generation must not delete assets.");
    }

    /// <summary>在真实 Stream.CopyToAsync 边界模拟读取故障，并观察 await using 是否释放资源。</summary>
    internal sealed class ObservedStream(byte[] bytes, CallLog log) : MemoryStream(bytes, writable: false)
    {
        /// <summary>流是否已经由生成路径释放。</summary>
        public bool WasDisposed { get; private set; }

        /// <summary>先记录素材复制步骤，再按原 MemoryStream 行为复制并透传取消。</summary>
        public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken ct)
        {
            await log.RecordAsync("copy", ct);
            await base.CopyToAsync(destination, bufferSize, ct);
        }

        /// <summary>标记测试流已经释放，再执行底层流的释放逻辑。</summary>
        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }

    /// <summary>返回固定测试密钥，允许区分可映射的密码学异常与其他异常。</summary>
    internal sealed class TestProtector(CallLog log) : IApiKeyProtector
    {
        /// <summary>解密时直接抛出的异常；为空时返回固定非生产密钥。</summary>
        public Exception? Error { get; set; }

        /// <summary>记录解密步骤，按配置抛出原测试异常或返回固定文本生成测试密钥。</summary>
        public string Unprotect(ApiKeySecret secret)
        {
            log.Entries.Add("key");
            if (Error is not null) throw Error;
            return "text-test-key";
        }

        /// <summary>拒绝文本生成过程中重新加密或替换渠道密钥。</summary>
        public ApiKeySecret Protect(string apiKey) =>
            throw new NotSupportedException("Text generation must not replace credentials.");
    }

    /// <summary>观察协议和完整上游输入，绝不发出供应商请求。</summary>
    internal sealed class RecordingText(CallLog log) : ITextGenerationDispatcher
    {
        /// <summary>最后一次提交的协议。</summary>
        public ProviderType Protocol { get; private set; }

        /// <summary>最后一次提交的模型、密钥、提示词和参数。</summary>
        public TextGenerationRequest? Request { get; private set; }

        /// <summary>受控上游结果，支持成功空串、异常状态码及缺失文本等边界。</summary>
        public TextGenerationResult Result { get; set; } = TextGenerationResult.Success("  generated text\n");

        /// <summary>记录文本生成协议、请求和操作顺序，再返回预设的文本结果。</summary>
        public async Task<TextGenerationResult> GenerateAsync(
            ProviderType protocol, TextGenerationRequest request, CancellationToken ct)
        {
            Protocol = protocol;
            Request = request;
            await log.RecordAsync("dispatch", ct);
            return Result;
        }
    }

    /// <summary>满足同一个 Controller 的图片用例装配要求，意外调用图片能力时立即失败。</summary>
    private sealed class UnusedImageServices : IImageGenerationDispatcher, IImageGenerationSettingsRepository, IClock
    {
        /// <summary>拒绝文本生成访问图片专用时钟，防止错误复用图片执行路径。</summary>
        public DateTimeOffset UtcNow => throw new NotSupportedException("Text generation must not read the image clock.");

        /// <summary>拒绝文本生成场景意外触发图片模型调用。</summary>
        public Task<ImageGenerationResult> GenerateAsync(
            ProviderType protocol, ImageGenerationRequest request, CancellationToken ct) =>
            throw new NotSupportedException("Text generation must not generate images.");

        /// <summary>拒绝文本生成读取图片响应格式设置。</summary>
        public Task<ConfiguredImageGenerationSettings> GetAsync(CancellationToken ct) =>
            throw new NotSupportedException("Text generation must not read image settings.");

        /// <summary>拒绝文本生成修改图片响应格式设置。</summary>
        public Task SetResponseFormatAsync(string responseFormat, DateTimeOffset now, CancellationToken ct) =>
            throw new NotSupportedException("Text generation must not change image settings.");
    }
}
