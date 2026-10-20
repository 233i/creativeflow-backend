using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Application.Abstractions.Ai;

/// <summary>
/// 查询供应商自身的模型目录。
/// </summary>
/// <remarks>
/// 此列表仅供参考。供应商发布模型的速度快于任何预设文件的更新速度，因此用户可借此使用应用尚未知晓的模型。
/// 大多数协议返回的数据不含分类元数据，所以实现会根据标识符推断分类。
/// </remarks>
public interface IUpstreamModelCatalog
{
    /// <summary>按渠道协议读取上游模型目录。</summary>
    Task<UpstreamCatalogResult> ListModelsAsync(
        AiProvider provider,
        string apiKey,
        CancellationToken ct);
}

/// <param name="Models">当 <paramref name="Ok"/> 为假时为空。</param>
/// <param name="Ok">当前调用是否成功。</param>
/// <param name="StatusCode">上游或 HTTP 调用的状态码。</param>
/// <param name="Error">失败详情或错误分类，具体格式沿用所属契约。</param>
public sealed record UpstreamCatalogResult(
    bool Ok,
    IReadOnlyList<UpstreamModel> Models,
    int? StatusCode,
    string? Error)
{
    /// <summary>构造成功结果，保留当前协议要求的输出字段。</summary>
    public static UpstreamCatalogResult Success(IReadOnlyList<UpstreamModel> models) =>
        new(true, models, 200, null);

    /// <summary>构造失败结果并保留上游状态与错误详情。</summary>
    public static UpstreamCatalogResult Failure(int? statusCode, string error) =>
        new(false, [], statusCode, error);
}

/// <param name="ModelKey">上游识别的模型名称，不等同于本地模型配置标识。</param>
/// <param name="Name">实体或配置项的名称。</param>
/// <param name="Category">所属的模型、模板或内容分类。</param>
public sealed record UpstreamModel(string ModelKey, string Name, ModelCategory Category);
