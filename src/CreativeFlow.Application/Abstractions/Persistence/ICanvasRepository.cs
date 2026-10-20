using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;

namespace CreativeFlow.Application.Abstractions.Persistence;

/// <summary>
/// 画布存储。每个方法都按 <see cref="DeviceId"/> 限定作用域，因此所有权是查询的一部分，
/// 而不是调用方可能忘记的额外检查。
/// </summary>
public interface ICanvasRepository
{
    /// <summary>
    /// 列出设备的画布，最近更新的排在前面。省略图数据——切换器只需要名称，
    /// 而每个图可能有数兆字节。
    /// </summary>
    Task<IReadOnlyList<CanvasSummary>> ListAsync(DeviceId deviceId, CancellationToken ct);

    /// <summary>按当前查找键读取已有配置或目录项。</summary>
    Task<Canvas?> FindAsync(DeviceId deviceId, CanvasId canvasId, CancellationToken ct);

    /// <summary>按原有存储规则写入画布文档。</summary>
    Task InsertAsync(Canvas canvas, CancellationToken ct);

    /// <summary>
    /// 写入图和名称。没有匹配记录时返回假，表示画布不存在或属于其他设备。
    /// </summary>
    Task<bool> UpdateAsync(Canvas canvas, CancellationToken ct);

    /// <summary>删除画布文档，保留不存在或不可访问时的返回语义。</summary>
    Task<bool> DeleteAsync(DeviceId deviceId, CanvasId canvasId, CancellationToken ct);
}

/// <param name="NodeCount">
/// 在 SQL 中根据已存储的图计算，因此列表查询无需传输或解析图数据。
/// </param>
/// <param name="CanvasId">关联画布的唯一标识。</param>
/// <param name="Name">实体或配置项的名称。</param>
/// <param name="CreatedAt">记录创建时间，沿用当前 UTC 与序列化约定。</param>
/// <param name="UpdatedAt">记录最近一次修改的 UTC 时间。</param>
public sealed record CanvasSummary(
    Guid CanvasId,
    string Name,
    int NodeCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
