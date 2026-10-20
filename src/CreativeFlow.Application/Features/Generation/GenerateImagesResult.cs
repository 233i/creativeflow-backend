using CreativeFlow.Application.Contracts.Ai;

namespace CreativeFlow.Application.Features.Generation;

/// <summary>图片生成用例的业务结果，不携带 HTTP 状态码或框架类型。</summary>
public enum GenerateImagesStatus
{
    /// <summary>上游图片及素材元数据均已按原顺序保存。</summary>
    Succeeded,
    /// <summary>请求指定的渠道不存在。</summary>
    NotFound,
    /// <summary>请求、模型、密钥或上游生成结果不符合当前业务规则。</summary>
    InvalidRequest,
}

/// <summary>供交付层映射既有响应的图片生成结果。</summary>
/// <param name="Status">业务结果分类，由 HTTP 适配器映射为原有状态码。</param>
/// <param name="Response">成功时的原有素材响应，失败时为空。</param>
/// <param name="Error">失败详情，保持原有用户可见文本。</param>
public sealed record GenerateImagesResult(
    GenerateImagesStatus Status,
    GenerateImageResponse? Response,
    string? Error)
{
    /// <summary>返回已保存的生成素材。</summary>
    public static GenerateImagesResult Success(GenerateImageResponse response) =>
        new(GenerateImagesStatus.Succeeded, response, null);

    /// <summary>表示渠道不存在，不引入额外错误内容。</summary>
    public static GenerateImagesResult NotFound() => new(GenerateImagesStatus.NotFound, null, null);

    /// <summary>保存现有业务错误文本，供 HTTP 边界映射为 invalid_request。</summary>
    public static GenerateImagesResult Invalid(string error) =>
        new(GenerateImagesStatus.InvalidRequest, null, error);
}
