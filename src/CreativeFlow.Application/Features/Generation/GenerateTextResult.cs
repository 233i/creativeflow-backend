using CreativeFlow.Application.Contracts.Ai;

namespace CreativeFlow.Application.Features.Generation;

/// <summary>文本生成的业务结果分类，不依赖 HTTP 状态码或 MVC 类型。</summary>
public enum GenerateTextStatus
{
    /// <summary>上游成功且文本非 null，空串也是有效结果。</summary>
    Succeeded,
    /// <summary>请求指定的渠道不存在。</summary>
    NotFound,
    /// <summary>输入、模型、密钥或上游结果未满足既有生成规则。</summary>
    InvalidRequest,
}

/// <summary>承载原有文本响应或业务错误，由交付层映射为既有 HTTP 结果。</summary>
/// <param name="Status">业务结果分类，不直接使用上游状态码。</param>
/// <param name="Response">成功时的原有文本 DTO，失败时为空。</param>
/// <param name="Error">失败时的原有用户可见文本，成功或渠道不存在时为空。</param>
public sealed record GenerateTextResult(GenerateTextStatus Status, GenerateTextResponse? Response, string? Error)
{
    /// <summary>返回上游生成的文本，保留空串、空白和换行。</summary>
    public static GenerateTextResult Success(GenerateTextResponse response) =>
        new(GenerateTextStatus.Succeeded, response, null);

    /// <summary>表示渠道不存在，不为交付层添加新的业务错误体。</summary>
    public static GenerateTextResult NotFound() => new(GenerateTextStatus.NotFound, null, null);

    /// <summary>保留现有错误详情，供 HTTP 边界映射为 invalid_request。</summary>
    public static GenerateTextResult Invalid(string error) => new(GenerateTextStatus.InvalidRequest, null, error);
}
