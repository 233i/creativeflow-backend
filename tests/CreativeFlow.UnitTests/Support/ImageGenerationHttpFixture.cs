using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using CreativeFlow.Api.Controllers;
using CreativeFlow.Api.Hubs;
using CreativeFlow.Api.Http;
using CreativeFlow.Api.Middleware;
using CreativeFlow.Api.Serialization;
using CreativeFlow.Application;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Common;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Abstractions.Security;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;
using CreativeFlow.Infrastructure.Ai;

namespace CreativeFlow.UnitTests.Support;

/// <summary>
/// 使用真实 Controller、MVC 绑定和生产 JSON 元数据测试图片端点；外部生成及数据库使用可观察的替身。
/// </summary>
/// <remarks>身份在边界处注入，故该夹具不代替 Cookie 中间件或 PostgreSQL 权限集成测试。</remarks>
internal sealed class ImageGenerationHttpFixture : IAsyncDisposable
{
    /// <summary>当前测试创建并负责释放的 Web 宿主。</summary>
    private WebApplication? _application;

    /// <summary>本次请求已验证的设备，供归属断言使用。</summary>
    public DeviceId DeviceId { get; private set; } = DeviceId.New();
    /// <summary>可切换的会话账号，供跨账号隔离断言使用。</summary>
    public AccountId AccountId { get; set; } = AccountId.New();
    /// <summary>测试宿主容器，供组合真实后台执行器使用。</summary>
    public IServiceProvider Services => _application!.Services;

    /// <summary>控制渠道查找结果和记录查询次数的替身。</summary>
    public RecordingProviders Providers { get; } = new();

    /// <summary>记录上游调用次数、解析后协议和完整参数。</summary>
    public RecordingImages Images { get; } = new();

    /// <summary>当前设备可见的素材以及按顺序记录的元数据写入。</summary>
    public RecordingAssets Assets { get; } = new();

    /// <summary>为读取提供独立流，并记录生成内容的保存。</summary>
    public MemoryAssets Store { get; } = new();

    /// <summary>可模拟密钥损坏的加解密适配器。</summary>
    public TestProtector Protector { get; } = new();

    /// <summary>管理员设置的图片响应格式。</summary>
    public TestImageSettings Settings { get; } = new();

    /// <summary>已经启动的内存 HTTP 客户端。</summary>
    public HttpClient Client { get; private set; } = null!;

    /// <summary>启动内存 Web 宿主，不连接真实数据库、供应商或后台任务。</summary>
    public static async Task<ImageGenerationHttpFixture> CreateAsync(bool authenticated = true, ImageJobDatabase? database = null)
    {
        var fixture = new ImageGenerationHttpFixture();
        if (database is not null)
        {
            fixture.DeviceId = new(database.Device);
            fixture.AccountId = new(database.Account);
        }
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing",
        });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IRequestHistoryRepository, RecordingRequestHistory>();
        builder.Services.AddControllers(options => options.Filters.Add<GenerationHistoryFilter>())
            .AddApplicationPart(typeof(AiGenerationController).Assembly)
            .AddJsonOptions(options => options.JsonSerializerOptions.TypeInfoResolverChain
                .Insert(0, ApiJsonContext.Default));
        builder.Services.AddSingleton<IProviderRepository>(fixture.Providers);
        builder.Services.AddSingleton<IImageGenerationDispatcher>(fixture.Images);
        builder.Services.AddSingleton<ITextGenerationDispatcher, UnusedTextDispatcher>();
        builder.Services.AddSingleton<ICanvasAssetRepository>(fixture.Assets);
        builder.Services.AddSingleton<IAssetStore>(database is null ? fixture.Store : database.Store);
        builder.Services.AddHttpClient("image-output");
        builder.Services.AddSingleton<IImageOutputDownloader, ImageOutputDownloader>();
        builder.Services.AddSingleton<IApiKeyProtector>(fixture.Protector);
        builder.Services.AddSingleton<IImageGenerationSettingsRepository>(fixture.Settings);
        builder.Services.AddSingleton<IClock, TestClock>();
        builder.Services.AddApplication();
        if (database is not null)
        {
            builder.Services.AddSingleton<IImageJobRepository>(database.Jobs);
            builder.Services.AddSingleton(new ImageQueueLimits());
        }

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            // 只模拟中间件已经验证的身份，未认证请求不能仅凭设备标识访问生成端点。
            if (authenticated)
            {
                context.Items[DeviceContextItems.DeviceId] = fixture.DeviceId;
                context.Items[DeviceContextItems.AccountId] = fixture.AccountId;
            }
            await next(context);
        });
        app.MapControllers();
        await app.StartAsync();
        fixture._application = app;
        fixture.Client = app.GetTestClient();
        return fixture;
    }

    /// <summary>以原有请求 DTO 构造有效的图片生成输入，方便各测试只改变一个条件。</summary>
    public GenerateImageRequest Request() => new(
        Providers.Provider!.Id.ToString(), "image-model", "draw a tree",
        null, null, "1024x1024", "high", "webp", null, 42);

    /// <summary>添加本设备可见的参考素材，返回端点实际接受的相对 URL。</summary>
    public string AddReference(string mediaType = "image/png")
    {
        var id = Guid.CreateVersion7();
        var path = $"cloud/reference-{id:N}";
        Assets.Available[id] = new CanvasAsset(
            id, DeviceId, "image", mediaType, path, 3, null, DateTimeOffset.UtcNow);
        Store.Available[path] = [1, 2, 3];
        return $"/api/v1/canvas/assets/{id}";
    }

    /// <summary>通过实际 MVC 请求管线发送生成请求。</summary>
    public Task<HttpResponseMessage> PostAsync(GenerateImageRequest request) =>
        Client.PostAsJsonAsync("/api/v1/ai/images", request, TestContext.Current.CancellationToken);

    /// <summary>释放客户端和内存宿主，避免测试间共享管线或资源。</summary>
    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        if (_application is not null) await _application.DisposeAsync();
    }

    /// <summary>提供一个有效的全局图片渠道，其余仓储操作保持禁止。</summary>
    internal sealed class RecordingProviders : StubProviderRepository
    {
        /// <summary>查询返回的渠道；空值模拟渠道不存在。</summary>
        public AiProvider? Provider { get; set; } = CreateProvider();

        /// <summary>渠道查找次数，用于验证提前拒绝和调用顺序。</summary>
        public int Finds { get; private set; }

        /// <summary>累计渠道查找次数并返回夹具配置的渠道，不访问真实仓储。</summary>
        public override Task<AiProvider?> FindAsync(ProviderId providerId, CancellationToken ct)
        {
            Finds++;
            return Task.FromResult(Provider);
        }

        /// <summary>创建固定模型能力的测试渠道，不包含真实凭据。</summary>
        private static AiProvider CreateProvider()
        {
            var id = ProviderId.New();
            var now = DateTimeOffset.UtcNow;
            return new AiProvider(
                id, null, "Test provider", null, null, ProviderType.OpenAiImages,
                null, "https://provider.invalid", true, null, 0, now, now)
            {
                Models = [new AiModel(
                    ModelId.New(), id, "image-model", "Image", true, null, ModelCategory.Image,
                    null, null, null, false, false, false, 0, now)],
            };
        }
    }

    /// <summary>记录生成调用，不发送网络请求。</summary>
    internal sealed class RecordingImages : IImageGenerationDispatcher
    {
        /// <summary>最后一次传给供应商适配器的参数。</summary>
        public ImageGenerationRequest? Request { get; private set; }

        /// <summary>最后一次解析出的协议。</summary>
        public ProviderType Protocol { get; private set; }

        /// <summary>提交次数，防止重构引入额外生成。</summary>
        public int Calls { get; private set; }

        /// <summary>上游固定结果，可由用例改为错误或多张图片。</summary>
        public ImageGenerationResult Result { get; set; } = ImageGenerationResult.Success(
            [new GeneratedImage([4, 5, 6], "image/webp", "https://provider.invalid/result.webp",
                DateTimeOffset.Parse("2026-09-06T00:00:00Z"))]);

        /// <summary>记录图片分发次数、协议和请求，返回测试预设的生成结果。</summary>
        public Task<ImageGenerationResult> GenerateAsync(
            ProviderType protocol, ImageGenerationRequest request, CancellationToken ct)
        {
            Calls++;
            Protocol = protocol;
            Request = request;
            return Task.FromResult(Result);
        }
    }

    /// <summary>模拟带设备范围的素材元数据访问。</summary>
    internal sealed class RecordingAssets : StubCanvasAssetRepository
    {
        /// <summary>查询可用素材，键为公开素材标识。</summary>
        public Dictionary<Guid, CanvasAsset> Available { get; } = [];

        /// <summary>按发生顺序保存的元数据。</summary>
        public List<CanvasAsset> Inserted { get; } = [];

        /// <summary>是否在元数据写入时模拟数据库异常。</summary>
        public bool FailInsert { get; set; }

        /// <summary>从内存素材表读取属于请求设备的指定素材，设备不匹配时返回空。</summary>
        public override Task<CanvasAsset?> FindAsync(DeviceId deviceId, Guid assetId, CancellationToken ct) =>
            Task.FromResult(Available.TryGetValue(assetId, out var asset) && asset.DeviceId == deviceId
                ? asset : null);

        /// <summary>按夹具开关模拟元数据写入失败，否则将素材记入已插入列表。</summary>
        public override Task InsertAsync(CanvasAsset asset, CancellationToken ct)
        {
            if (FailInsert) throw new InvalidOperationException("Simulated metadata write failure.");
            Inserted.Add(asset);
            return Task.CompletedTask;
        }
    }

    /// <summary>仅在内存保存字节，避免生成测试依赖文件系统。</summary>
    internal sealed class MemoryAssets : TestAssetStore
    {
        /// <summary>可以读取的素材内容，按不透明存储键索引。</summary>
        public Dictionary<string, byte[]> Available { get; } = [];

        /// <summary>保存时使用的扩展名，用于锁定现有映射。</summary>
        public List<string> Extensions { get; } = [];

        /// <summary>将生成字节保存到内存的合成云端键，记录扩展名并返回存储信息。</summary>
        public override Task<StoredAsset> SaveAsync(
            byte[] bytes, string mediaType, string extensionHint, CancellationToken ct)
        {
            var path = $"cloud/generated-{Extensions.Count}";
            Available[path] = bytes;
            Extensions.Add(extensionHint);
            return Task.FromResult(new StoredAsset(path, mediaType, bytes.Length));
        }

        /// <summary>从内存字节表创建只读流，未找到存储键时返回空。</summary>
        public override Task<Stream?> OpenReadAsync(string storagePath, CancellationToken ct) =>
            Task.FromResult<Stream?>(Available.TryGetValue(storagePath, out var bytes)
                ? new MemoryStream(bytes, writable: false) : null);

        /// <summary>拒绝图片生成过程中意外删除素材。</summary>
        public override Task DeleteAsync(string storagePath, CancellationToken ct) =>
            throw new NotSupportedException("Generation must not delete assets.");
    }

    /// <summary>模拟解密失败并保证测试不依赖真实密钥。</summary>
    internal sealed class TestProtector : IApiKeyProtector
    {
        /// <summary>是否将已保存的密钥视为损坏。</summary>
        public bool Fail { get; set; }

        /// <summary>返回占位密钥材料，不执行真实加密。</summary>
        public ApiKeySecret Protect(string apiKey) => new([], [], [], "test");

        /// <summary>按夹具开关抛出解密错误或返回固定测试密钥。</summary>
        public string Unprotect(ApiKeySecret secret) =>
            Fail ? throw new CryptographicException("test") : "test-key";
    }

    /// <summary>模拟管理员选择的图片格式策略。</summary>
    internal sealed class TestImageSettings : IImageGenerationSettingsRepository
    {
        /// <summary>每次生成在服务端读取的响应格式。</summary>
        public string ResponseFormat { get; set; } = "base64";

        /// <summary>返回测试配置的图片响应格式和当前时间，不读取管理员设置表。</summary>
        public Task<ConfiguredImageGenerationSettings> GetAsync(CancellationToken ct) =>
            Task.FromResult(new ConfiguredImageGenerationSettings(ResponseFormat, DateTimeOffset.UtcNow));

        /// <summary>拒绝图片生成过程中修改管理员响应格式配置。</summary>
        public Task SetResponseFormatAsync(string responseFormat, DateTimeOffset now, CancellationToken ct) =>
            throw new NotSupportedException("Generation must not change administrator settings.");
    }

    /// <summary>创建 Controller 所需的文本端口，但禁止图片测试调用文本生成。</summary>
    private sealed class UnusedTextDispatcher : ITextGenerationDispatcher
    {
        /// <summary>拒绝图片生成场景意外调用文本生成端口。</summary>
        public Task<TextGenerationResult> GenerateAsync(
            ProviderType protocol, TextGenerationRequest request, CancellationToken ct) =>
            throw new NotSupportedException("Image generation must not call text generation.");
    }

    /// <summary>提供可预测的生成时间，不依赖机器当前时钟。</summary>
    private sealed class TestClock : IClock
    {
        /// <summary>返回固定测试 UTC 时间，使生成结果时间断言保持可重复。</summary>
        public DateTimeOffset UtcNow => DateTimeOffset.Parse("2026-09-05T00:00:00Z");
    }
}
