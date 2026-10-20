using Microsoft.Extensions.Logging.Abstractions;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;
using CreativeFlow.Infrastructure.Ai;

namespace CreativeFlow.UnitTests.Ai;

public sealed class VideoFrameServiceTests
{
    /// <summary>回归场景：Extract Last Frame Creates One Cached Image Asset。</summary>
    [Fact]
    public async Task ExtractLastFrame_CreatesOneCachedImageAsset()
    {
        var deviceId = DeviceId.New();
        var source = VideoAsset(deviceId);
        var repository = new FrameAssetRepository(source);
        var store = new FrameAssetStore([1, 2, 3, 4]);
        var extractor = new StubFrameExtractor([0x89, 0x50, 0x4e, 0x47]);
        var service = new VideoFrameService(
            repository,
            store,
            extractor,
            NullLogger<VideoFrameService>.Instance);

        var first = await service.ExtractLastFrameAsync(
            deviceId, source.AssetId, CancellationToken.None);
        var second = await service.ExtractLastFrameAsync(
            deviceId, source.AssetId, CancellationToken.None);

        Assert.Equal(VideoFrameAssetStatus.Succeeded, first.Status);
        Assert.Equal(first.AssetId, second.AssetId);
        Assert.Equal("image/png", first.MediaType);
        Assert.Equal(1, extractor.CallCount);
        Assert.Equal(1, store.OpenCount);
        Assert.Equal(1, store.SaveCount);
        Assert.Equal(1, repository.InsertDerivativeCount);
        Assert.NotNull(repository.Derivative);
        Assert.Equal("image", repository.Derivative.Kind);
        Assert.Equal(source.DeviceId, repository.Derivative.DeviceId);
        Assert.Equal(source.Prompt, repository.Derivative.Prompt);
    }

    /// <summary>回归场景：Extract Last Frame Rejects Anything That Is Not A Stored Video。</summary>
    [Theory]
    [InlineData("image", "image/png")]
    [InlineData("video", "application/octet-stream")]
    public async Task ExtractLastFrame_RejectsAnythingThatIsNotAStoredVideo(
        string kind,
        string mediaType)
    {
        var deviceId = DeviceId.New();
        var source = VideoAsset(deviceId) with { Kind = kind, MediaType = mediaType };
        var repository = new FrameAssetRepository(source);
        var store = new FrameAssetStore([1]);
        var extractor = new StubFrameExtractor([1]);
        var service = new VideoFrameService(
            repository,
            store,
            extractor,
            NullLogger<VideoFrameService>.Instance);

        var result = await service.ExtractLastFrameAsync(
            deviceId, source.AssetId, CancellationToken.None);

        Assert.Equal(VideoFrameAssetStatus.InvalidSource, result.Status);
        Assert.Equal(0, store.OpenCount);
        Assert.Equal(0, extractor.CallCount);
    }

    /// <summary>构造具有固定存储路径和媒体类型的视频测试素材。</summary>
    private static CanvasAsset VideoAsset(DeviceId deviceId) => new(
        Guid.CreateVersion7(),
        deviceId,
        "video",
        "video/mp4",
        "blob/video",
        4,
        "continue the scene",
        DateTimeOffset.UtcNow);

    /// <summary>初始化 StubFrameExtractor，保留既有依赖、配置校验与资源生命周期。</summary>
    private sealed class StubFrameExtractor(byte[] frame) : IVideoFrameExtractor
    {
        /// <summary>测试替身被调用的累计次数。</summary>
        public int CallCount { get; private set; }

        /// <summary>累计抽帧调用次数并返回预设尾帧字节，不启动真实 FFmpeg。</summary>
        public Task<byte[]> ExtractLastFrameAsync(Stream source, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(frame);
        }
    }

    /// <summary>初始化 FrameAssetStore，保留既有依赖、配置校验与资源生命周期。</summary>
    private sealed class FrameAssetStore(byte[] video) : CreativeFlow.UnitTests.Support.TestAssetStore
    {
        /// <summary>测试中打开素材内容流的累计次数。</summary>
        public int OpenCount { get; private set; }
        /// <summary>测试中保存素材字节的累计次数。</summary>
        public int SaveCount { get; private set; }

        /// <summary>累计保存次数并返回固定尾帧存储键，不写入真实素材。</summary>
        public override Task<StoredAsset> SaveAsync(
            byte[] bytes,
            string mediaType,
            string extensionHint,
            CancellationToken ct)
        {
            SaveCount++;
            return Task.FromResult(new StoredAsset("blob/frame", mediaType, bytes.LongLength));
        }

        /// <summary>累计打开次数并返回预设视频字节的只读内存流。</summary>
        public override Task<Stream?> OpenReadAsync(string storagePath, CancellationToken ct)
        {
            OpenCount++;
            return Task.FromResult<Stream?>(new MemoryStream(video, writable: false));
        }

        /// <summary>接受测试中的存储清理请求但不执行任何删除。</summary>
        public override Task DeleteAsync(string storagePath, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>初始化 FrameAssetRepository，保留既有依赖、配置校验与资源生命周期。</summary>
    private sealed class FrameAssetRepository(CanvasAsset source) : ICanvasAssetRepository
    {
        /// <summary>测试中已经缓存的视频派生图片记录。</summary>
        public CanvasAsset? Derivative { get; private set; }
        /// <summary>测试中新建素材派生关系的累计次数。</summary>
        public int InsertDerivativeCount { get; private set; }

        /// <summary>仅按预设源素材标识返回源视频，不在此替身模拟设备权限查询。</summary>
        public Task<CanvasAsset?> FindAsync(
            DeviceId deviceId,
            Guid assetId,
            CancellationToken ct) =>
            Task.FromResult<CanvasAsset?>(assetId == source.AssetId ? source : null);

        /// <summary>直接返回预设或此前保存的派生素材，供尾帧缓存复用测试使用。</summary>
        public Task<CanvasAsset?> FindDerivativeAsync(
            DeviceId deviceId,
            Guid sourceAssetId,
            string derivation,
            CancellationToken ct) => Task.FromResult(Derivative);

        /// <summary>断言来源素材和尾帧派生标记正确，记录插入次数并缓存派生素材，固定返回成功。</summary>
        public Task<bool> TryInsertDerivativeAsync(
            CanvasAsset asset,
            Guid sourceAssetId,
            string derivation,
            CancellationToken ct)
        {
            Assert.Equal(source.AssetId, sourceAssetId);
            Assert.Equal("last-frame-v1", derivation);
            InsertDerivativeCount++;
            Derivative = asset;
            return Task.FromResult(true);
        }

        /// <summary>拒绝尾帧场景绕过派生素材入口直接插入普通素材。</summary>
        public Task InsertAsync(CanvasAsset asset, CancellationToken ct) =>
            throw new NotSupportedException();

        /// <summary>拒绝尾帧测试意外读取素材列表。</summary>
        public Task<IReadOnlyList<CanvasAsset>> ListAsync(
            DeviceId deviceId,
            string? kind,
            int limit,
            CancellationToken ct) => throw new NotSupportedException();

        /// <summary>拒绝尾帧测试意外删除素材元数据。</summary>
        public Task<string?> DeleteAsync(
            DeviceId deviceId,
            Guid assetId,
            CancellationToken ct) => throw new NotSupportedException();

        /// <summary>拒绝尾帧测试读取素材分组列表。</summary>
        public Task<IReadOnlyList<CanvasAssetGroup>> ListGroupsAsync(
            DeviceId deviceId,
            CancellationToken ct) => throw new NotSupportedException();

        /// <summary>拒绝尾帧测试查询指定素材分组。</summary>
        public Task<CanvasAssetGroup?> FindGroupAsync(
            DeviceId deviceId,
            Guid groupId,
            CancellationToken ct) => throw new NotSupportedException();

        /// <summary>拒绝尾帧测试创建素材分组。</summary>
        public Task<CanvasAssetGroup?> InsertGroupAsync(
            CanvasAssetGroup group,
            CancellationToken ct) => throw new NotSupportedException();

        /// <summary>拒绝尾帧测试重命名素材分组。</summary>
        public Task<CanvasAssetGroup?> RenameGroupAsync(
            DeviceId deviceId,
            Guid groupId,
            string name,
            DateTimeOffset updatedAt,
            CancellationToken ct) => throw new NotSupportedException();

        /// <summary>拒绝尾帧测试删除素材分组。</summary>
        public Task<bool> DeleteGroupAsync(
            DeviceId deviceId,
            Guid groupId,
            CancellationToken ct) => throw new NotSupportedException();

        /// <summary>拒绝尾帧测试批量调整素材分组。</summary>
        public Task<int> AssignGroupAsync(
            DeviceId deviceId,
            IReadOnlyList<Guid> assetIds,
            Guid? groupId,
            CancellationToken ct) => throw new NotSupportedException();
    }
}
