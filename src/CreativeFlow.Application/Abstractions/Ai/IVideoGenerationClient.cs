using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Application.Abstractions.Ai;

/// <summary>
/// 启动并轮询视频生成任务。
/// </summary>
/// <remarks>
/// 各供应商的视频生成均为异步过程：接收请求、返回任务标识，然后渲染数分钟。
/// 此处直接体现该流程，而不隐藏在阻塞调用之后，因为持续时间超过 HTTP 超时的请求无法建模为单次阻塞调用。
/// </remarks>
public interface IVideoGenerationClient
{
    /// <summary>适配器实现的上游协议。</summary>
    ProviderType Protocol { get; }

    /// <summary>按选定协议向上游提交一次视频生成请求。</summary>
    Task<VideoSubmitResult> SubmitAsync(VideoGenerationRequest request, CancellationToken ct);

    /// <summary>查询上游视频任务状态并映射为统一的本地结果。</summary>
    Task<VideoPollResult> PollAsync(
        AiProvider provider,
        string apiKey,
        string upstreamJobId,
        CancellationToken ct);
}

/// <summary>
/// 可续接现有视频片段的协议所实现的可选能力。
/// </summary>
public interface IVideoExtensionClient
{
    /// <summary>按当前支持的协议提交视频续接请求。</summary>
    Task<VideoSubmitResult> ExtendAsync(VideoExtensionRequest request, CancellationToken ct);
}

/// <param name="Provider">当前使用的渠道或身份提供方配置。</param>
/// <param name="ApiKey">渠道调用密钥；ApiKeySecret 类型为受保护的持久化表示，字符串类型仅用于请求或执行。</param>
/// <param name="ModelKey">上游识别的模型名称，不等同于本地模型配置标识。</param>
/// <param name="Prompt">提交给生成模型的用户提示词。</param>
/// <param name="ReferenceImages">参与生成的参考图片集合。</param>
/// <param name="InputMode">视频生成采用的文本、首帧或首尾帧等输入模式。</param>
/// <param name="Aspect">生成媒体的宽高比选项。</param>
/// <param name="Resolution">请求视频的分辨率档位。</param>
/// <param name="DurationSeconds">以秒计的视频或操作时长。</param>
/// <param name="Fps">请求视频的帧率选项。</param>
/// <param name="Seed">上游生成使用的随机种子选项。</param>
/// <param name="Watermark">是否请求上游添加水印。</param>
/// <param name="GenerateAudio">是否请求上游同时生成音轨。</param>
public sealed record VideoGenerationRequest(
    AiProvider Provider,
    string ApiKey,
    string ModelKey,
    string Prompt,
    IReadOnlyList<ReferenceImage> ReferenceImages,
    string? InputMode,
    string? Aspect,
    string? Resolution,
    int? DurationSeconds,
    int? Fps,
    int? Seed,
    bool? Watermark,
    bool? GenerateAudio);

/// <summary>
/// 上游操作的视频输入。可复用的供应商 URL 避免下载刚生成的视频后再以 base64 上传；
/// CreativeFlow 签名 URL 用于已存储片段，字节内容则作为本地开发和上传场景的回退。
/// </summary>
/// <param name="Bytes">原始二进制内容。</param>
/// <param name="MediaType">内容的 MIME 类型，响应时沿用已验证的存储值。</param>
/// <param name="Url">资源的交付或访问地址。</param>
/// <param name="FallbackUrl">首选来源不可用时按既有规则尝试的备用地址。</param>
public sealed record ReferenceVideo(
    byte[] Bytes,
    string MediaType,
    string? Url = null,
    string? FallbackUrl = null);

/// <param name="Provider">当前使用的渠道或身份提供方配置。</param>
/// <param name="ApiKey">渠道调用密钥；ApiKeySecret 类型为受保护的持久化表示，字符串类型仅用于请求或执行。</param>
/// <param name="ModelKey">上游识别的模型名称，不等同于本地模型配置标识。</param>
/// <param name="Prompt">提交给生成模型的用户提示词。</param>
/// <param name="SourceVideo">续接或抽帧操作的原始视频引用。</param>
/// <param name="DurationSeconds">以秒计的视频或操作时长。</param>
public sealed record VideoExtensionRequest(
    AiProvider Provider,
    string ApiKey,
    string ModelKey,
    string Prompt,
    ReferenceVideo SourceVideo,
    int DurationSeconds);

/// <param name="Ok">当前调用是否成功。</param>
/// <param name="UpstreamJobId">供应商接受视频生成后返回的任务标识。</param>
/// <param name="StatusCode">上游或 HTTP 调用的状态码。</param>
/// <param name="Error">失败详情或错误分类，具体格式沿用所属契约。</param>
public sealed record VideoSubmitResult(bool Ok, string? UpstreamJobId, int? StatusCode, string? Error)
{
    /// <summary>构造成功结果，保留当前协议要求的输出字段。</summary>
    public static VideoSubmitResult Success(string jobId) => new(true, jobId, 200, null);

    /// <summary>构造失败结果并保留上游状态与错误详情。</summary>
    public static VideoSubmitResult Failure(int? statusCode, string error) =>
        new(false, null, statusCode, error);
}

public enum VideoJobState
{
    /// <summary>上游已受理，等待处理或后续状态。</summary>
    Pending,
    /// <summary>当前正在执行。</summary>
    Running,
    /// <summary>已经成功完成。</summary>
    Succeeded,
    /// <summary>已经失败并保存失败信息。</summary>
    Failed,
}

/// <param name="VideoUrl">
/// 仅在 <see cref="State"/> 为 <see cref="VideoJobState.Succeeded"/> 时设置。供应商返回短期下载 URL
/// 而非字节内容，因此调用方必须及时获取并存储。
/// </param>
/// <param name="State">认证事务或上游任务的当前状态。</param>
/// <param name="Progress">现有协议中的任务完成比例，可空时表示未知。</param>
/// <param name="Error">失败详情或错误分类，具体格式沿用所属契约。</param>
public sealed record VideoPollResult(
    VideoJobState State,
    double? Progress,
    string? VideoUrl,
    string? Error);

/// <summary>路由到支持解析后协议的客户端。</summary>
public interface IVideoGenerationDispatcher
{
    /// <summary>按选定协议向上游提交一次视频生成请求。</summary>
    Task<VideoSubmitResult> SubmitAsync(
        ProviderType protocol,
        VideoGenerationRequest request,
        CancellationToken ct);

    /// <summary>按当前支持的协议提交视频续接请求。</summary>
    Task<VideoSubmitResult> ExtendAsync(
        ProviderType protocol,
        VideoExtensionRequest request,
        CancellationToken ct);

    /// <summary>查询上游视频任务状态并映射为统一的本地结果。</summary>
    Task<VideoPollResult> PollAsync(
        ProviderType protocol,
        AiProvider provider,
        string apiKey,
        string upstreamJobId,
        CancellationToken ct);
}
