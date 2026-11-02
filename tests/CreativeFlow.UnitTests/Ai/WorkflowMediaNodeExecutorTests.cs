using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Abstractions.Security;
using CreativeFlow.Application.Features.Workflow;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;
using CreativeFlow.Infrastructure.Ai.Workflows;
using CreativeFlow.UnitTests.Support;
using static CreativeFlow.UnitTests.Api.ExternalWorkflowHandlersTests.RepositoryProxy;

namespace CreativeFlow.UnitTests.Ai;

/// <summary>直接构造生产执行器冻结媒体节点的模型参数、资产校验、恢复和取消语义；外部端口全部为计数替身。</summary>
public sealed class WorkflowMediaNodeExecutorTests
{
    /// <summary>图片输入保留账号、类型及回收状态校验。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImageInputHonorsRecycledAssets(bool recycled)
    {
        var fixture = new Fixture();
        var asset = fixture.AddAsset("image") with { IsRecycled = recycled };
        fixture.Assets[asset.AssetId] = asset;
        var context = fixture.Context("image");
        context.Node.Data["assetUrl"] = Url(asset.AssetId);
        if (recycled)
            Assert.Equal("The input image is no longer available.",
                (await Assert.ThrowsAsync<WorkflowExecutionException>(() => fixture.ExecuteAsync(context))).Message);
        else Assert.Equal([Url(asset.AssetId)], (await fixture.ExecuteAsync(context)).Images);
        Assert.Equal(0, fixture.ImageCalls);
    }

    /// <summary>媒体图片节点仍使用角色模型、旧尺寸映射和四张上限，并保存来源信息。</summary>
    [Fact]
    public async Task ImageGenerationPreservesRequestAndStoredOutput()
    {
        var fixture = new Fixture();
        var reference = fixture.AddAsset("image");
        var context = fixture.Inputs("imageGenerate", new(["original", "second"], [Url(reference.AssetId)], []));
        context.Node.Data["count"] = 99;
        context.Node.Data["aspect"] = "16:9";
        context.Node.Data["quality"] = "high";
        context.Node.Data["outputFormat"] = "webp";
        context.Node.Data["seed"] = 123;
        var value = await fixture.ExecuteAsync(context);
        var request = Assert.IsType<ImageGenerationRequest>(fixture.ImageRequest);
        Assert.Equal("image-model", request.ModelKey);
        Assert.Equal("original\nsecond", request.Prompt);
        Assert.Equal("1536x1024", request.Size);
        Assert.Equal(4, request.Count);
        Assert.Equal("high", request.Quality);
        Assert.Equal("webp", request.OutputFormat);
        Assert.Equal(123, request.Seed);
        Assert.Single(request.ReferenceImages);
        Assert.Null(request.Mask);
        Assert.True(request.DeferDownloads);
        Assert.Equal(1, fixture.ImageCalls);
        var saved = Assert.Single(fixture.WrittenAssets);
        Assert.Equal("https://synthetic.invalid/result", saved.SourceUrl);
        Assert.Equal(request.Prompt, saved.Prompt);
        Assert.Equal([Url(saved.AssetId)], value.Images);
    }

    /// <summary>图片创作节点显式选择已启用模型时不再强制使用 image 默认槽位。</summary>
    [Fact]
    public async Task ImageGenerationUsesExplicitEnabledModelSelection()
    {
        var fixture = new Fixture();
        var context = fixture.Inputs("imageGenerate", WorkflowNodeValue.FromText("prompt"));
        context.Node.Data["providerId"] = fixture.ProviderId.ToString();
        context.Node.Data["modelId"] = fixture.AlternateImageModelId.ToString();

        await fixture.ExecuteAsync(context);

        Assert.Equal("image-model-alternate", Assert.IsType<ImageGenerationRequest>(fixture.ImageRequest).ModelKey);
    }

    /// <summary>媒体工作流把发布图上的 Seedream 档位、比例与优化模式传给图片适配器。</summary>
    [Fact]
    public async Task SeedreamUsesSelectedResolutionAndMode()
    {
        var fixture = new Fixture("doubao-seedream-5.0-pro", ProviderType.SeedreamImages);
        var context = fixture.Inputs("imageGenerate", WorkflowNodeValue.FromText("prompt"));
        context.Node.Data["aspect"] = "9:16";
        context.Node.Data["resolution"] = "1.5K";
        context.Node.Data["promptOptimizeMode"] = "fast";
        await fixture.ExecuteAsync(context);
        Assert.Equal("1188x2112", fixture.ImageRequest!.Size);
        Assert.Equal("fast", fixture.ImageRequest.PromptOptimizeMode);
    }

    /// <summary>外部输入和旧图不能向不支持的型号发送档位或模式，也不应创建输出素材。</summary>
    [Theory]
    [InlineData("doubao-seedream-5.0-pro", "4K", "standard")]
    [InlineData("doubao-seedream-5.0-flash", "2K", "fast")]
    public async Task InvalidSeedreamSettingsNeverDispatch(string model, string resolution, string mode)
    {
        var fixture = new Fixture(model, ProviderType.SeedreamImages);
        var context = fixture.Inputs("imageGenerate", WorkflowNodeValue.FromText("prompt"));
        context.Node.Data["resolution"] = resolution;
        context.Node.Data["promptOptimizeMode"] = mode;
        await Assert.ThrowsAsync<WorkflowExecutionException>(() => fixture.ExecuteAsync(context));
        Assert.Equal(0, fixture.ImageCalls); Assert.Empty(fixture.WrittenAssets);
    }

    /// <summary>生成后取消不再保存素材；上游失败同样不能写入结果或自动重试。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ImageFailureOrCancellationDoesNotStoreOrRetry(bool cancelled)
    {
        var fixture = new Fixture { Cancelled = cancelled, ImageFailure = cancelled ? null : "synthetic failure" };
        var context = fixture.Inputs("imageGenerate", WorkflowNodeValue.FromText("prompt"));
        if (cancelled) await Assert.ThrowsAsync<WorkflowCancelledException>(() => fixture.ExecuteAsync(context));
        else Assert.Equal("synthetic failure", (await Assert.ThrowsAsync<WorkflowExecutionException>(() => fixture.ExecuteAsync(context))).Message);
        Assert.Equal(1, fixture.ImageCalls);
        Assert.Equal(0, fixture.BlobWrites);
        Assert.Empty(fixture.WrittenAssets);
    }

    /// <summary>两种视频节点恢复已成功任务时不提交、查询上游或重复登记素材。</summary>
    [Theory]
    [InlineData("videoGenerate")]
    [InlineData("videoExtend")]
    public async Task CompletedVideoTasksAreReusedWithoutSubmission(string type)
    {
        var fixture = new Fixture();
        fixture.Inherited = fixture.Job(VideoJobStatus.Succeeded);
        var context = fixture.Context(type);
        context = context with { Run = context.Run with { ResumedFromRunId = WorkflowRunId.New() } };
        var result = await fixture.ExecuteAsync(context);
        Assert.Equal([Url(fixture.Inherited.AssetId!.Value)], result.Videos);
        Assert.Equal(context.Run.ResumedFromRunId, fixture.LookedUpAncestor);
        Assert.Equal(0, fixture.VideoCalls);
        Assert.Empty(fixture.InsertedJobs);
    }

    /// <summary>已受理视频恢复时继续等待原本地任务，通过注入端口报告进度。</summary>
    [Fact]
    public async Task ActiveVideoTaskIsAwaitedWithoutResubmission()
    {
        var fixture = new Fixture();
        fixture.Inherited = fixture.Job(VideoJobStatus.Running);
        fixture.CurrentJob = fixture.Inherited;
        var context = fixture.Context("videoGenerate");
        context = context with { Run = context.Run with { ResumedFromRunId = WorkflowRunId.New() } };
        Assert.Single((await fixture.ExecuteAsync(context)).Videos);
        Assert.Equal([1d], fixture.Progress);
        Assert.Equal(0, fixture.VideoCalls);
        Assert.Empty(fixture.InsertedJobs);
    }

    /// <summary>新视频保持十五秒上限、首尾帧反转和运行关联信息，不借重构切换提交协议。</summary>
    [Fact]
    public async Task VideoGenerationPreservesFramesDurationAndJobMetadata()
    {
        var fixture = new Fixture();
        var first = fixture.AddAsset("image");
        var last = fixture.AddAsset("image");
        var context = fixture.Inputs("videoGenerate", new(["prompt"], [Url(first.AssetId), Url(last.AssetId)], []));
        context.Node.Data["duration"] = 99;
        context.Node.Data["inputMode"] = "first-last";
        context.Node.Data["reverseFrames"] = true;
        context.Node.Data["generateAudio"] = true;
        var result = await fixture.ExecuteAsync(context);
        var request = Assert.IsType<VideoGenerationRequest>(fixture.VideoRequest);
        Assert.Equal(15, request.DurationSeconds);
        Assert.Equal("first-last", request.InputMode);
        Assert.Equal($"https://synthetic.invalid/{last.AssetId}", request.ReferenceImages[0].Url);
        Assert.Equal($"https://synthetic.invalid/{first.AssetId}", request.ReferenceImages[1].Url);
        Assert.True(request.GenerateAudio);
        var job = Assert.Single(fixture.InsertedJobs);
        var metadata = JsonNode.Parse(job.RequestJson)!;
        Assert.Equal(context.Run.Id.ToString(), metadata["workflowRunId"]!.GetValue<string>());
        Assert.Equal(context.Node.Id, metadata["nodeId"]!.GetValue<string>());
        Assert.True(metadata["reverseFrames"]!.GetValue<bool>());
        Assert.Equal(1, fixture.VideoCalls);
        Assert.Single(result.Videos);
    }

    /// <summary>首尾帧素材缺失时不能降级成无图生成。</summary>
    [Fact]
    public async Task MissingFirstLastFramePreventsProviderCall()
    {
        var fixture = new Fixture();
        var context = fixture.Inputs("videoGenerate", new(["prompt"], [Url(Guid.NewGuid())], []));
        context.Node.Data["inputMode"] = "first-last";
        var error = await Assert.ThrowsAsync<WorkflowExecutionException>(() => fixture.ExecuteAsync(context));
        Assert.Contains("first/last frame image is no longer available", error.Message);
        Assert.Equal(0, fixture.VideoCalls);
    }

    /// <summary>续接仅使用原 xAI 模型及已授权源视频地址。</summary>
    [Fact]
    public async Task VideoExtensionPreservesSourceAndProtocol()
    {
        var fixture = new Fixture();
        var source = fixture.AddAsset("video");
        var context = fixture.Inputs("videoExtend", new([" continue "], [], [Url(source.AssetId)]));
        var result = await fixture.ExecuteAsync(context);
        var request = Assert.IsType<VideoExtensionRequest>(fixture.ExtensionRequest);
        Assert.Equal("grok-imagine-video", request.ModelKey);
        Assert.Equal("continue", request.Prompt);
        Assert.Equal($"https://synthetic.invalid/{source.AssetId}", request.SourceVideo.Url);
        Assert.Equal(ProviderType.XaiVideo, fixture.VideoProtocol);
        Assert.Single(result.Videos);
        Assert.Equal("extension", JsonNode.Parse(Assert.Single(fixture.InsertedJobs).RequestJson)!["operation"]!.GetValue<string>());
    }

    /// <summary>等待视频期间取消只更新原任务，不创建新任务或调用模型。</summary>
    [Fact]
    public async Task VideoWaitPropagatesCancellationToExistingJob()
    {
        var fixture = new Fixture { Cancelled = true };
        fixture.CurrentJob = fixture.Inherited = fixture.Job(VideoJobStatus.Running);
        var context = fixture.Context("videoGenerate");
        context = context with { Run = context.Run with { ResumedFromRunId = WorkflowRunId.New() } };
        await Assert.ThrowsAsync<WorkflowCancelledException>(() => fixture.ExecuteAsync(context));
        Assert.Equal(VideoJobStatus.Cancelled, fixture.CurrentJob.Status);
        Assert.Equal(0, fixture.VideoCalls);
        Assert.Empty(fixture.Progress);
    }

    /// <summary>多个视频尾帧输出维持原输入顺序，并携带原设备作用域。</summary>
    [Fact]
    public async Task VideoFramesPreserveInputOrder()
    {
        var fixture = new Fixture();
        var first = fixture.AddAsset("video");
        var second = fixture.AddAsset("video");
        var value = await fixture.ExecuteAsync(fixture.Inputs("videoFrame", new([], [], [Url(first.AssetId), Url(second.AssetId)])));
        Assert.Equal([first.AssetId, second.AssetId], fixture.FrameInputs);
        Assert.Equal(fixture.FrameOutputs.Select(Url), value.Images);
    }

    /// <summary>使用稳定站内素材路径，不生成可访问真实系统的 URL。</summary>
    private static string Url(Guid id) => $"/api/v1/canvas/assets/{id}";

    /// <summary>为生产执行器提供受控的仓储、模型和存储端口，所有副作用只记录在内存中。</summary>
    private sealed class Fixture : IWorkflowNodeProgress
    {
        /// <summary>所有测试素材的唯一所属设备。</summary>
        private readonly DeviceId device = new(Guid.NewGuid());
        /// <summary>由明确构造的真实执行器组成的媒体分派表，不需要服务容器。</summary>
        private readonly WorkflowNodeExecutorRegistry registry;
        /// <summary>素材读取目录，可模拟回收和缺失。</summary>
        public Dictionary<Guid, CanvasAsset> Assets { get; } = [];
        /// <summary>生成过程中新增的素材记录。</summary>
        public List<CanvasAsset> WrittenAssets { get; } = [];
        /// <summary>本次执行新建的视频任务。</summary>
        public List<VideoJob> InsertedJobs { get; } = [];
        /// <summary>上一次运行可复用的视频任务。</summary>
        public VideoJob? Inherited { get; set; }
        /// <summary>本地视频轮询返回的任务。</summary>
        public VideoJob? CurrentJob { get; set; }
        /// <summary>恢复时查询的祖先运行 ID。</summary>
        public WorkflowRunId? LookedUpAncestor { get; private set; }
        /// <summary>通过注入进度端口交付的进度。</summary>
        public List<double?> Progress { get; } = [];
        /// <summary>尾帧提取接收到的视频 ID。</summary>
        public List<Guid> FrameInputs { get; } = [];
        /// <summary>尾帧提取返回的图片 ID。</summary>
        public List<Guid> FrameOutputs { get; } = [];
        /// <summary>本次生成的图片请求。</summary>
        public ImageGenerationRequest? ImageRequest { get; private set; }
        /// <summary>本次生成的视频请求。</summary>
        public VideoGenerationRequest? VideoRequest { get; private set; }
        /// <summary>本次续接的视频请求。</summary>
        public VideoExtensionRequest? ExtensionRequest { get; private set; }
        /// <summary>执行器选定的视频协议。</summary>
        public ProviderType? VideoProtocol { get; private set; }
        /// <summary>图片模型实际调用次数。</summary>
        public int ImageCalls { get; private set; }
        /// <summary>视频提交或续接实际调用次数。</summary>
        public int VideoCalls { get; private set; }
        /// <summary>实际保存的媒体字节次数。</summary>
        public int BlobWrites { get; private set; }
        /// <summary>模拟持久化取消已完成。</summary>
        public bool Cancelled { get; init; }
        /// <summary>模拟服务商明确拒绝图片生成。</summary>
        public string? ImageFailure { get; init; }
        /// <summary>合成模型所属的渠道标识，供显式节点选择测试使用。</summary>
        public ProviderId ProviderId { get; }
        /// <summary>第二个已启用图片模型的标识。</summary>
        public ModelId AlternateImageModelId { get; }

        /// <summary>显式构造严格替身；任何未声明调用均使测试失败，不使用本机配置或网络。</summary>
        public Fixture(string imageModelKey = "image-model", ProviderType imageProtocol = ProviderType.OpenAiImages)
        {
            // 所有外部端口只响应明确列出的操作。恢复测试若误提交上游、或执行器多做一次查询，会立即暴露。
            var id = ProviderId.New();
            ProviderId = id;
            var imageModel = Model(id, imageModelKey, ModelCategory.Image, imageProtocol);
            var alternateImage = Model(id, "image-model-alternate", ModelCategory.Image, ProviderType.OpenAiImages);
            AlternateImageModelId = alternateImage.Id;
            var videoModel = Model(id, "video-model", ModelCategory.Video, ProviderType.SeedanceVideo);
            var grok = Model(id, "grok-imagine-video", ModelCategory.Video, ProviderType.XaiVideo);
            var models = new[] { imageModel, alternateImage, videoModel, grok };
            var configured = new AiProvider(id, null, "Synthetic", null, null, ProviderType.SeedanceVideo, null,
                "https://synthetic.invalid", true, null, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)
            { Models = models };
            var slots = Create<IModelSlotRepository>((name, _) => name == "ListAsync"
                ? Task.FromResult<IReadOnlyList<ConfiguredModelSlot>>([new("image", id, imageModel.Id, DateTimeOffset.UnixEpoch), new("video", id, videoModel.Id, DateTimeOffset.UnixEpoch)]) : throw Unexpected(name));
            var providers = Create<IProviderRepository>((name, _) => name == "FindAsync" ? Task.FromResult<AiProvider?>(configured) : throw Unexpected(name));
            var protector = Create<IApiKeyProtector>((name, _) => throw Unexpected(name));
            var assets = Create<ICanvasAssetRepository>((name, args) =>
            {
                if (name == "FindAsync") { Assert.Equal(device, args![0]); return Task.FromResult(Assets.GetValueOrDefault((Guid)args[1]!)); }
                if (name == "InsertAsync") { WrittenAssets.Add((CanvasAsset)args![0]!); return Task.CompletedTask; }
                throw Unexpected(name);
            });
            var assetStore = Create<IAssetStore>((name, args) =>
            {
                if (name == "OpenReadAsync") return Task.FromResult<Stream?>(new MemoryStream([1, 2, 3]));
                if (name == "SaveAsync") { BlobWrites++; return Task.FromResult(new StoredAsset("synthetic-result", (string)args![1]!, ((byte[])args[0]!).Length)); }
                throw Unexpected(name);
            });
            var signedUrls = Create<ISignedMediaUrlService>((name, args) => name == "CreateInputUrls"
                ? new MediaInputUrls($"https://synthetic.invalid/{((CanvasAsset)args![0]!).AssetId}", null) : throw Unexpected(name));
            var settings = Create<IImageGenerationSettingsRepository>((name, _) => name == "GetAsync"
                ? Task.FromResult(new ConfiguredImageGenerationSettings("url", DateTimeOffset.UnixEpoch)) : throw Unexpected(name));
            var imageDispatcher = Create<IImageGenerationDispatcher>((name, args) =>
            {
                if (name != "GenerateAsync") throw Unexpected(name);
                ImageCalls++; ImageRequest = (ImageGenerationRequest)args![1]!;
                return Task.FromResult(ImageFailure is not null ? ImageGenerationResult.Failure(400, ImageFailure)
                    : ImageGenerationResult.Success([new([9, 8, 7], "image/webp", "https://synthetic.invalid/result")]));
            });
            var runs = Create<IWorkflowRunRepository>((name, _) => name == "IsCancellationRequestedAsync" ? Task.FromResult(Cancelled) : throw Unexpected(name));
            var videoDispatcher = Create<IVideoGenerationDispatcher>((name, args) =>
            {
                VideoProtocol = (ProviderType)args![0]!;
                if (name == "SubmitAsync") VideoRequest = (VideoGenerationRequest)args[1]!;
                else if (name == "ExtendAsync") ExtensionRequest = (VideoExtensionRequest)args[1]!;
                else throw Unexpected(name);
                VideoCalls++; return Task.FromResult(VideoSubmitResult.Success("synthetic-upstream"));
            });
            var videoJobs = Create<IVideoJobRepository>((name, args) =>
            {
                if (name == "FindLatestForWorkflowNodeAsync") { LookedUpAncestor = (WorkflowRunId)args![1]!; return Task.FromResult(Inherited); }
                if (name == "FindByAssetAsync") return Task.FromResult<VideoJob?>(null);
                if (name == "InsertAsync") { CurrentJob = (VideoJob)args![0]!; InsertedJobs.Add(CurrentJob); return Task.CompletedTask; }
                if (name == "FindByIdAsync") return Task.FromResult(Cancelled ? CurrentJob : CurrentJob! with { Status = VideoJobStatus.Succeeded, AssetId = Guid.NewGuid(), Progress = 1 });
                if (name == "UpdateAsync") { CurrentJob = (VideoJob)args![0]!; return Task.CompletedTask; }
                throw Unexpected(name);
            });
            var frames = Create<IVideoFrameService>((name, args) =>
            {
                if (name != "ExtractLastFrameAsync") throw Unexpected(name);
                Assert.Equal(device, args![0]); FrameInputs.Add((Guid)args[1]!);
                var frame = Guid.NewGuid(); FrameOutputs.Add(frame); return Task.FromResult(VideoFrameAssetResult.Success(frame, "image/png"));
            });
            var resolver = new WorkflowMediaModelResolver(slots, providers, protector);
            var reader = new WorkflowMediaAssetReader(assets, assetStore, signedUrls, videoJobs, providers, protector, videoDispatcher);
            var awaiter = new WorkflowVideoJobAwaiter(videoJobs, runs, this);
            var imageInput = new ImageInputNodeExecutor(assets);
            var downloader = Create<IImageOutputDownloader>((_, _) => throw new InvalidOperationException("Unexpected download"));
            var image = new ImageGenerationNodeExecutor(resolver, reader, settings, imageDispatcher, assetStore, assets, runs, downloader);
            var video = new VideoGenerationNodeExecutor(videoJobs, resolver, reader, videoDispatcher, awaiter);
            var extension = new VideoExtensionNodeExecutor(videoJobs, resolver, reader, videoDispatcher, awaiter);
            var frame = new VideoFrameNodeExecutor(frames);
            registry = new WorkflowNodeExecutorRegistry([
                new(WorkflowNodeFamily.Media, "image", () => imageInput),
                new(WorkflowNodeFamily.Media, "imageGenerate", () => image),
                new(WorkflowNodeFamily.Media, "videoGenerate", () => video),
                new(WorkflowNodeFamily.Media, "videoExtend", () => extension),
                new(WorkflowNodeFamily.Media, "videoFrame", () => frame),
            ]);
        }

        /// <summary>创建合成角色模型，不指向真实服务商。</summary>
        private static AiModel Model(ProviderId providerId, string key, ModelCategory category, ProviderType protocol) =>
            new(ModelId.New(), providerId, key, key, true, protocol, category, null, null, null, true, false, false, 0, DateTimeOffset.UnixEpoch);

        /// <summary>未声明的端口访问说明执行路径发生变化，立即使测试失败。</summary>
        private static Exception Unexpected(string method) => new InvalidOperationException($"Unexpected synthetic call: {method}");

        /// <summary>创建当前设备可访问的合成媒体元数据。</summary>
        public CanvasAsset AddAsset(string kind)
        {
            var id = Guid.NewGuid();
            var asset = new CanvasAsset(id, device, kind, kind == "image" ? "image/png" : "video/mp4", "synthetic-input", 3, null, DateTimeOffset.UnixEpoch);
            Assets.Add(id, asset); return asset;
        }

        /// <summary>创建有上游标识的合成本地视频任务。</summary>
        public VideoJob Job(VideoJobStatus status) => new(Guid.NewGuid(), device, null, "video-model", status,
            "synthetic-upstream", null, status == VideoJobStatus.Succeeded ? Guid.NewGuid() : null, null, "{}", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

        /// <summary>构造当前设备下的纯数据上下文，运行与节点状态使用相同身份。</summary>
        public WorkflowNodeExecutionContext Context(string type)
        {
            var context = WorkflowNodeExecutorRegistryTests.Context(type);
            var run = WorkflowNodeTestHost.Run(device);
            return context with { Run = run, NodeRun = WorkflowNodeTestHost.NodeRun(run, context.Node) };
        }

        /// <summary>把合成上游输出按三个原媒体端口连接到当前节点。</summary>
        public WorkflowNodeExecutionContext Inputs(string type, WorkflowNodeValue input)
        {
            var context = Context(type);
            return context with
            {
                Values = new Dictionary<string, WorkflowNodeValue> { ["upstream"] = input },
                Node = context.Node with
                {
                    Inputs = [new("t", "upstream", "text", "node", "prompt", WorkflowValueType.Text),
                    new("i", "upstream", "image", "node", "image", WorkflowValueType.Image),
                    new("v", "upstream", "video", "node", "video", WorkflowValueType.Video)]
                }
            };
        }

        /// <summary>通过生产分派器执行，未知节点不能绕过注册校验。</summary>
        public Task<WorkflowNodeValue> ExecuteAsync(WorkflowNodeExecutionContext context) =>
            registry.ExecuteAsync(context, TestContext.Current.CancellationToken);

        /// <summary>记录显式传入的运行和节点进度，不执行持久化或推送。</summary>
        public Task ReportAsync(WorkflowRun run, WorkflowNodeRun nodeRun, double? progress, CancellationToken ct)
        {
            Assert.Equal(run.Id, nodeRun.RunId);
            Assert.Equal("node", nodeRun.NodeId);
            Progress.Add(progress);
            return Task.CompletedTask;
        }
    }
}
