using Dapper;
using Npgsql;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Domain.Identity;

namespace CreativeFlow.Infrastructure.Persistence;

/// <summary>初始化 VisitorRepository 的依赖与实例状态。</summary>
public sealed class VisitorRepository(NpgsqlDataSource dataSource) : IVisitorRepository
{
    /// <summary>创建访客标识及创建时间，访客已存在时不覆盖记录。</summary>
    public async Task CreateAsync(VisitorId id, DateTimeOffset createdAt, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);

        await connection.ExecuteAsync(
            """
            INSERT INTO visitor (visitor_id, created_at)
            VALUES (@VisitorId, @CreatedAt)
            ON CONFLICT (visitor_id) DO NOTHING
            """,
            new CreateVisitorArgs { VisitorId = id.Value, CreatedAt = createdAt });
    }

    /// <summary>保存设备与访客的关联边，冲突时只提升置信度，不合并设备或访客数据行。</summary>
    public async Task LinkAsync(DeviceLink link, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);

        // 使用关联边而绝不合并数据行：解除错误概率性推测必须只需一次删除。
        // 发生冲突时只提高置信度而绝不降低，因此后续确定性匹配会升级早期推测，而不会被其丢弃。
        //
        // 显式转换 @Method：参数以 int 传入，而 Npgsql 拒绝将 Int32 写入 smallint 列。
        await connection.ExecuteAsync(
            """
            INSERT INTO device_link (device_id, visitor_id, confidence, method, created_at)
            VALUES (@DeviceId, @VisitorId, @Confidence, @Method::smallint, @CreatedAt)
            ON CONFLICT (device_id, visitor_id) DO UPDATE
            SET confidence = GREATEST(device_link.confidence, EXCLUDED.confidence),
                method     = CASE WHEN EXCLUDED.confidence > device_link.confidence
                                  THEN EXCLUDED.method ELSE device_link.method END
            """,
            new LinkArgs
            {
                DeviceId = link.DeviceId.Value,
                VisitorId = link.VisitorId.Value,
                Confidence = (float)link.Confidence.Value,
                Method = (int)link.Method,
                CreatedAt = link.CreatedAt,
            });
    }

    /// <summary>读取设备置信度最高的访客关联，未关联时返回空。</summary>
    public async Task<VisitorId?> FindVisitorForDeviceAsync(DeviceId deviceId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);

        // 置信度最高的优先，使确定性关联始终高于概率性关联。
        var visitorId = await connection.QueryFirstOrDefaultAsync<Guid?>(
            """
            SELECT visitor_id
            FROM device_link
            WHERE device_id = @DeviceId
            ORDER BY confidence DESC, created_at ASC
            LIMIT 1
            """,
            new DeviceIdArg { DeviceId = deviceId.Value });

        return visitorId is { } value ? new VisitorId(value) : null;
    }
}

internal sealed class CreateVisitorArgs
{
    /// <summary>概率性关联的访客标识，不作为账号资源归属依据。</summary>
    public Guid VisitorId { get; init; }
    /// <summary>记录创建时间，沿用当前 UTC 与序列化约定。</summary>
    public DateTimeOffset CreatedAt { get; init; }
}

internal sealed class LinkArgs
{
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; }
    /// <summary>概率性关联的访客标识，不作为账号资源归属依据。</summary>
    public Guid VisitorId { get; init; }
    /// <summary>设备关联的置信度，不作为账号资源授权凭据。</summary>
    public float Confidence { get; init; }
    /// <summary>设备关联、协议请求或通知采用的方法。</summary>
    public int Method { get; init; }
    /// <summary>记录创建时间，沿用当前 UTC 与序列化约定。</summary>
    public DateTimeOffset CreatedAt { get; init; }
}
