using CreativeFlow.Domain.Identity;

namespace CreativeFlow.Application.Abstractions.Ai;

public enum VideoFrameAssetStatus
{
    /// <summary>已经成功完成。</summary>
    Succeeded,
    /// <summary>指定资源不存在或不可访问。</summary>
    NotFound,
    /// <summary>源素材类型或内容不适合当前处理。</summary>
    InvalidSource,
    /// <summary>已经失败并保存失败信息。</summary>
    Failed,
}

/// <summary>从视频素材的一帧派生并存储的图片。</summary>
/// <param name="Status">当前对象的业务状态，存储和接口名称保持既有映射。</param>
/// <param name="AssetId">素材唯一标识。</param>
/// <param name="MediaType">内容的 MIME 类型，响应时沿用已验证的存储值。</param>
/// <param name="Error">失败详情或错误分类，具体格式沿用所属契约。</param>
public sealed record VideoFrameAssetResult(
    VideoFrameAssetStatus Status,
    Guid? AssetId,
    string? MediaType,
    string? Error)
{
    /// <summary>构造成功结果，保留当前协议要求的输出字段。</summary>
    public static VideoFrameAssetResult Success(Guid assetId, string mediaType) =>
        new(VideoFrameAssetStatus.Succeeded, assetId, mediaType, null);

    /// <summary>构造资源不存在的业务结果。</summary>
    public static VideoFrameAssetResult NotFound() =>
        new(VideoFrameAssetStatus.NotFound, null, null, null);

    /// <summary>将校验失败映射为当前功能既有的错误结果，保留详情和错误分类。</summary>
    public static VideoFrameAssetResult Invalid(string error) =>
        new(VideoFrameAssetStatus.InvalidSource, null, null, error);

    /// <summary>构造失败结果并保留上游状态与错误详情。</summary>
    public static VideoFrameAssetResult Failure(string error) =>
        new(VideoFrameAssetStatus.Failed, null, null, error);
}

/// <summary>
/// 从已存储的视频素材生成持久化图片素材。实现负责提取、缓存和持久化，
/// 使 HTTP 请求与工作流工作进程共享完全一致的语义。
/// </summary>
public interface IVideoFrameService
{
    /// <summary>提取并复用视频素材的最后可解码帧。</summary>
    Task<VideoFrameAssetResult> ExtractLastFrameAsync(
        DeviceId deviceId,
        Guid sourceAssetId,
        CancellationToken ct);
}

/// <summary>持久化帧服务使用的底层解码器。</summary>
public interface IVideoFrameExtractor
{
    /// <summary>提取并复用视频素材的最后可解码帧。</summary>
    Task<byte[]> ExtractLastFrameAsync(Stream source, CancellationToken ct);
}
