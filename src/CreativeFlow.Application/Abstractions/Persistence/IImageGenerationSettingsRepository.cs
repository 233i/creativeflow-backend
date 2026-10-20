namespace CreativeFlow.Application.Abstractions.Persistence;

/// <summary>
/// 存储管理员选择的应用级图片生成策略。
/// </summary>
public interface IImageGenerationSettingsRepository
{
    /// <summary>读取管理员图片生成策略，保留缺失资源的既有处理。</summary>
    Task<ConfiguredImageGenerationSettings> GetAsync(CancellationToken ct);

    /// <summary>保存管理员设置的应用级图片响应格式。</summary>
    Task SetResponseFormatAsync(
        string responseFormat,
        DateTimeOffset now,
        CancellationToken ct);
}

/// <param name="ResponseFormat">管理员选择的图片响应格式，沿用 url/base64 词汇。</param>
/// <param name="UpdatedAt">记录最近一次修改的 UTC 时间。</param>
public sealed record ConfiguredImageGenerationSettings(
    string ResponseFormat,
    DateTimeOffset UpdatedAt);
