using Dapper;
using Npgsql;
using CreativeFlow.Application.Abstractions.Persistence;

namespace CreativeFlow.Infrastructure.Persistence;

/// <summary>初始化 ImageGenerationSettingsRepository 的依赖与实例状态。</summary>
public sealed class ImageGenerationSettingsRepository(NpgsqlDataSource dataSource)
    : IImageGenerationSettingsRepository
{
    /// <summary>读取全局图片生成响应格式及其最后更新时间。</summary>
    public async Task<ConfiguredImageGenerationSettings> GetAsync(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var row = await connection.QuerySingleAsync<ImageGenerationSettingsRow>(
            """
            SELECT image_response_format, updated_at
            FROM ai_generation_settings
            WHERE settings_id = 1
            """);
        return row.ToDomain();
    }

    /// <summary>更新全局图片响应格式和修改时间，不更改模型或渠道配置。</summary>
    public async Task SetResponseFormatAsync(
        string responseFormat,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(
            """
            UPDATE ai_generation_settings
            SET image_response_format = @ResponseFormat,
                updated_at = @UpdatedAt
            WHERE settings_id = 1
            """,
            new ImageGenerationSettingsWriteArgs
            {
                ResponseFormat = responseFormat,
                UpdatedAt = now,
            });
    }
}

internal sealed class ImageGenerationSettingsRow
{
    /// <summary>服务端选择的图片响应格式。</summary>
    public string ImageResponseFormat { get; init; } = string.Empty;
    /// <summary>记录最近一次修改的 UTC 时间。</summary>
    public DateTime UpdatedAt { get; init; }

    /// <summary>把数据库静态行映射为业务记录，保留既有状态和时间语义。</summary>
    public ConfiguredImageGenerationSettings ToDomain() => new(
        ImageResponseFormat,
        new DateTimeOffset(DateTime.SpecifyKind(UpdatedAt, DateTimeKind.Utc)));
}

internal sealed class ImageGenerationSettingsWriteArgs
{
    /// <summary>管理员选择的图片响应格式，沿用 url/base64 词汇。</summary>
    public string ResponseFormat { get; init; } = string.Empty;
    /// <summary>记录最近一次修改的 UTC 时间。</summary>
    public DateTimeOffset UpdatedAt { get; init; }
}
