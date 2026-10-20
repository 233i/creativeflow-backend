namespace CreativeFlow.Application.Contracts.Ai;

/// <param name="Images">
/// 参考图的素材 URL，来自之前的生成或上传。由服务器端解析：上游供应商无法访问需要设备 Cookie 的素材端点。
/// </param>
/// <param name="OutputFormat"><c>png</c>、<c>jpeg</c> 或 <c>webp</c>。</param>
/// <param name="ProviderId">关联的 AI 渠道标识。</param>
/// <param name="ModelKey">上游识别的模型名称，不等同于本地模型配置标识。</param>
/// <param name="Prompt">提交给生成模型的用户提示词。</param>
/// <param name="Size">图片尺寸选项，由具体协议解释。</param>
/// <param name="Quality">图片生成质量选项，由具体协议解释。</param>
/// <param name="Count">当前请求或结果的项目数量。</param>
/// <param name="Seed">上游生成使用的随机种子选项。</param>
public sealed record GenerateImageRequest(
    string ProviderId,
    string ModelKey,
    string Prompt,
    string[]? Images,
    /// <summary>
    /// 用于局部重绘的已存储 PNG 蒙版。透明像素由供应商替换；
    /// 其 URL 通过与参考图相同的设备自有素材路径解析。
    /// </summary>
    string? MaskUrl,
    string? Size,
    string? Quality,
    string? OutputFormat,
    int? Count,
    int? Seed)
{
    /// <summary>发起生成的画布，仅用于请求记录关联，服务端另行校验归属。</summary>
    public string? CanvasId { get; init; }
    /// <summary>发起生成的画布节点，仅用于记录定位。</summary>
    public string? NodeId { get; init; }
}

/// <summary>管理员控制的上游图片响应策略所接受的传输值。</summary>
public static class ImageResponseFormats
{
    /// <summary>资源的交付或访问地址。</summary>
    public const string Url = "url";
    /// <summary>管理员图片响应策略中 Base64 模式的稳定值。</summary>
    public const string Base64 = "base64";

    /// <summary>验证并规范化支持的图片响应格式名称。</summary>
    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized is Url or Base64;
    }
}

/// <param name="ResponseFormat">管理员选择的图片响应格式，沿用 url/base64 词汇。</param>
/// <param name="UpdatedAt">记录最近一次修改的 UTC 时间。</param>
public sealed record ImageGenerationSettingsResponse(
    string ResponseFormat,
    string UpdatedAt);

/// <param name="ResponseFormat">管理员选择的图片响应格式，沿用 url/base64 词汇。</param>
public sealed record UpdateImageGenerationSettingsRequest(string ResponseFormat);

/// <param name="Url">资源的交付或访问地址。</param>
/// <param name="MediaType">内容的 MIME 类型，响应时沿用已验证的存储值。</param>
/// <param name="AssetId">素材唯一标识。</param>
public sealed record GeneratedAsset(string Url, string MediaType, string AssetId);

/// <param name="Assets">返回或参与操作的素材集合。</param>
public sealed record GenerateImageResponse(GeneratedAsset[] Assets);

public static class VideoGenerationLimits
{
    /// <summary>视频时长的最小允许值，单位为秒。</summary>
    public const int MinimumDurationSeconds = 1;
    /// <summary>视频时长的最大允许值，单位为秒。</summary>
    public const int MaximumDurationSeconds = 15;
    /// <summary>未指定时使用的视频时长，单位为秒。</summary>
    public const int DefaultDurationSeconds = 5;
}

public static class VideoExtensionLimits
{
    /// <summary>视频时长的最小允许值，单位为秒。</summary>
    public const int MinimumDurationSeconds = 2;
    /// <summary>视频时长的最大允许值，单位为秒。</summary>
    public const int MaximumDurationSeconds = 10;
    /// <summary>未指定时使用的视频时长，单位为秒。</summary>
    public const int DefaultDurationSeconds = 6;
}

/// <param name="ProviderId">关联的 AI 渠道标识。</param>
/// <param name="ModelKey">上游识别的模型名称，不等同于本地模型配置标识。</param>
/// <param name="Prompt">提交给生成模型的用户提示词。</param>
/// <param name="Images">图片内容或引用地址集合，元素类型由所属契约定义。</param>
/// <param name="InputMode">视频生成采用的文本、首帧或首尾帧等输入模式。</param>
/// <param name="Aspect">生成媒体的宽高比选项。</param>
/// <param name="Resolution">请求视频的分辨率档位。</param>
/// <param name="Duration">请求的视频时长选项。</param>
/// <param name="Fps">请求视频的帧率选项。</param>
/// <param name="Seed">上游生成使用的随机种子选项。</param>
/// <param name="Watermark">是否请求上游添加水印。</param>
/// <param name="GenerateAudio">是否请求上游同时生成音轨。</param>
public sealed record StartVideoRequest(
    string ProviderId,
    string ModelKey,
    string Prompt,
    string[]? Images,
    string? InputMode,
    string? Aspect,
    string? Resolution,
    int? Duration,
    int? Fps,
    int? Seed,
    bool? Watermark,
    bool? GenerateAudio)
{
    /// <summary>发起生成的画布，仅用于请求记录关联，服务端另行校验归属。</summary>
    public string? CanvasId { get; init; }
    /// <summary>发起生成的画布节点，仅用于记录定位。</summary>
    public string? NodeId { get; init; }
}

/// <param name="JobId">视频任务的本地标识。</param>
public sealed record StartVideoResponse(string JobId);

/// <param name="Progress">供应商报告进度时为 0～1，否则为空值。</param>
/// <param name="JobId">视频任务的本地标识。</param>
/// <param name="Status">当前对象的业务状态，存储和接口名称保持既有映射。</param>
/// <param name="AssetUrl">客户端可读取的素材地址。</param>
/// <param name="Error">失败详情或错误分类，具体格式沿用所属契约。</param>
public sealed record VideoJobStatusResponse(
    string JobId,
    string Status,
    double? Progress,
    string? AssetUrl,
    string? Error);

/// <param name="SystemPrompt">
/// 不属于用户原文的引导内容。改写功能用它描述转换要求，而不会将该指令混入待转换文本。
/// </param>
/// <param name="Images">
/// 用于根据图片编写提示词的参考图素材 URL。由服务器端解析，协议不包含图片部分时会静默忽略。
/// </param>
/// <param name="ProviderId">关联的 AI 渠道标识。</param>
/// <param name="ModelKey">上游识别的模型名称，不等同于本地模型配置标识。</param>
/// <param name="Prompt">提交给生成模型的用户提示词。</param>
/// <param name="MaxOutputTokens">模型调用允许的最大输出 token 数量。</param>
/// <param name="Temperature">文本采样温度选项，由具体协议解释。</param>
public sealed record GenerateTextRequest(
    string ProviderId,
    string ModelKey,
    string Prompt,
    string? SystemPrompt,
    string[]? Images,
    int? MaxOutputTokens,
    double? Temperature)
{
    /// <summary>发起生成的画布，仅用于请求记录关联，服务端另行校验归属。</summary>
    public string? CanvasId { get; init; }
    /// <summary>发起生成的画布节点，仅用于记录定位。</summary>
    public string? NodeId { get; init; }
}

/// <param name="Text">文本内容或本次流式文本片段。</param>
public sealed record GenerateTextResponse(string Text);

/// <param name="Prompt">如果该素材是生成而非上传的，则为生成它所用的提示词。</param>
/// <param name="Id">当前实体或契约项目的唯一标识。</param>
/// <param name="Url">资源的交付或访问地址。</param>
/// <param name="Kind">当前资源或数据项的种类。</param>
/// <param name="MediaType">内容的 MIME 类型，响应时沿用已验证的存储值。</param>
/// <param name="ByteSize">素材或文件内容的字节数。</param>
/// <param name="CreatedAt">记录创建时间，沿用当前 UTC 与序列化约定。</param>
/// <param name="GroupId">素材分组标识；允许空值的场景表示未分组。</param>
public sealed record CanvasAssetSummary(
    string Id,
    string Url,
    string Kind,
    string MediaType,
    long ByteSize,
    string? Prompt,
    string CreatedAt,
    string? GroupId);

/// <param name="Assets">返回或参与操作的素材集合。</param>
public sealed record CanvasAssetListResponse(CanvasAssetSummary[] Assets);

/// <summary>用户在画布素材库中创建的分组。</summary>
/// <param name="Id">当前实体或契约项目的唯一标识。</param>
/// <param name="Name">实体或配置项的名称。</param>
/// <param name="AssetCount">该分组或结果中的素材数量。</param>
/// <param name="CreatedAt">记录创建时间，沿用当前 UTC 与序列化约定。</param>
/// <param name="UpdatedAt">记录最近一次修改的 UTC 时间。</param>
public sealed record CanvasAssetGroupSummary(
    string Id,
    string Name,
    int AssetCount,
    string CreatedAt,
    string UpdatedAt);

/// <param name="Groups">当前查询返回的素材分组集合。</param>
public sealed record CanvasAssetGroupListResponse(CanvasAssetGroupSummary[] Groups);

/// <param name="Name">实体或配置项的名称。</param>
public sealed record CreateCanvasAssetGroupRequest(string Name);

/// <param name="Name">实体或配置项的名称。</param>
public sealed record UpdateCanvasAssetGroupRequest(string Name);

/// <summary>将多个自有素材移入分组；分组标识为空表示取消分组。</summary>
/// <param name="AssetIds">参与当前操作的素材标识集合。</param>
/// <param name="GroupId">素材分组标识；允许空值的场景表示未分组。</param>
public sealed record AssignCanvasAssetsRequest(string[] AssetIds, string? GroupId);

/// <param name="Updated">本次更新成功的记录数量。</param>
public sealed record AssignCanvasAssetsResponse(int Updated);
