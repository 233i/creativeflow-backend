using Dapper;
using Npgsql;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Domain.Identity;

namespace CreativeFlow.Infrastructure.Persistence;

/// <remarks>
/// 此处每个 Dapper 调用都会在构建时生成拦截器。必须限制在受支持的接口范围内：单泛型
/// <c>Query&lt;T&gt;</c>/<c>QueryAsync&lt;T&gt;</c>、<c>Execute</c>、<c>ExecuteScalar</c>
/// 以及静态类型参数对象。不支持多重映射和 <c>QueryMultiple</c>，它们会静默回退到反射；
/// Directory.Build.props 中的 DAP001 构建门禁会将其转为错误。
/// </remarks>
/// <summary>初始化 DeviceRepository 的依赖与实例状态。</summary>
public sealed class DeviceRepository(NpgsqlDataSource dataSource) : IDeviceRepository
{
    /// <summary>按设备标识读取指纹、信号及首次和最近活动时间。</summary>
    public async Task<Device?> FindByIdAsync(DeviceId id, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);

        var row = await connection.QueryFirstOrDefaultAsync<DeviceRow>(
            """
            SELECT device_id, fp_exact, fp_coarse, host(ip_prefix) AS ip_prefix,
                   signals::text AS signals, sig_version, user_agent, first_seen, last_seen
            FROM device
            WHERE device_id = @DeviceId
            """,
            new DeviceIdArg { DeviceId = id.Value });

        return row?.ToDomain();
    }

    /// <summary>保存新设备的指纹与信号信息；设备标识已存在时不覆盖原记录。</summary>
    public async Task InsertAsync(Device device, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);

        await connection.ExecuteAsync(
            """
            INSERT INTO device (device_id, fp_exact, fp_coarse, ip_prefix, signals,
                                sig_version, user_agent, first_seen, last_seen)
            VALUES (@DeviceId, @FpExact, @FpCoarse, @IpPrefix::inet, @Signals::jsonb,
                    @SigVersion, @UserAgent, @FirstSeen, @LastSeen)
            ON CONFLICT (device_id) DO NOTHING
            """,
            new InsertDeviceArgs
            {
                DeviceId = device.Id.Value,
                FpExact = device.FingerprintExact,
                FpCoarse = device.FingerprintCoarse,
                IpPrefix = device.IpPrefix,
                Signals = device.SignalsJson,
                SigVersion = device.SignalVersion,
                UserAgent = device.UserAgent,
                FirstSeen = device.FirstSeen,
                LastSeen = device.LastSeen,
            });
    }

    /// <summary>更新指定设备的最近活动时间，不重算指纹或身份关联。</summary>
    public async Task TouchAsync(DeviceId id, DateTimeOffset seenAt, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);

        await connection.ExecuteAsync(
            "UPDATE device SET last_seen = @SeenAt WHERE device_id = @DeviceId",
            new TouchDeviceArgs { DeviceId = id.Value, SeenAt = seenAt });
    }

    /// <summary>按粗指纹、IP 网段、信号版本和活动时间查询跨浏览器候选，排除精确指纹相同的设备。</summary>
    public async Task<IReadOnlyList<CoarseCandidate>> FindCoarseCandidatesAsync(
        byte[] fingerprintCoarse,
        byte[] excludeFingerprintExact,
        string ipPrefix,
        int signalVersion,
        DateTimeOffset seenSince,
        int limit,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);

        // 共享 fp_exact 的设备来自同一浏览器，本应提供 Cookie；排除它们后，
        // 此查询只回答跨浏览器匹配问题。
        //
        // 调用方会比上限多请求一条记录，以便检测拥有过多候选项的指纹，而不是静默截断。
        // 常见粗粒度哈希必须阻止关联，不能生成任意子集。
        var rows = await connection.QueryAsync<CoarseCandidateRow>(
            """
            SELECT d.device_id, dl.visitor_id
            FROM device d
            JOIN device_link dl ON dl.device_id = d.device_id
            WHERE d.fp_coarse   = @FpCoarse
              AND d.ip_prefix   = @IpPrefix::inet
              AND d.sig_version = @SigVersion
              AND d.last_seen  >= @SeenSince
              AND d.fp_exact   <> @ExcludeFpExact
            ORDER BY d.last_seen DESC
            LIMIT @Limit
            """,
            new CoarseCandidateArgs
            {
                FpCoarse = fingerprintCoarse,
                ExcludeFpExact = excludeFingerprintExact,
                IpPrefix = ipPrefix,
                SigVersion = signalVersion,
                SeenSince = seenSince,
                Limit = limit,
            });

        return rows.Select(r => new CoarseCandidate(r.DeviceId, r.VisitorId)).ToList();
    }
}

internal sealed class DeviceIdArg
{
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; }
}

internal sealed class TouchDeviceArgs
{
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; }
    /// <summary>本次记录设备活动的时间。</summary>
    public DateTimeOffset SeenAt { get; init; }
}

internal sealed class InsertDeviceArgs
{
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; }
    /// <summary>精确设备指纹摘要。</summary>
    public byte[] FpExact { get; init; } = [];
    /// <summary>粗粒度设备指纹摘要，用于受限候选关联。</summary>
    public byte[] FpCoarse { get; init; } = [];
    /// <summary>用于设备关联的截断 IP 前缀。</summary>
    public string IpPrefix { get; init; } = string.Empty;
    /// <summary>本次设备握手提交的信号集合。</summary>
    public string Signals { get; init; } = "{}";
    /// <summary>数据库保存的设备信号格式版本。</summary>
    public int SigVersion { get; init; }
    /// <summary>浏览器发送的 User-Agent 信息。</summary>
    public string? UserAgent { get; init; }
    /// <summary>首次观察到该设备或访客的时间。</summary>
    public DateTimeOffset FirstSeen { get; init; }
    /// <summary>最近一次观察到设备或访客的时间。</summary>
    public DateTimeOffset LastSeen { get; init; }
}

internal sealed class CoarseCandidateArgs
{
    /// <summary>粗粒度设备指纹摘要，用于受限候选关联。</summary>
    public byte[] FpCoarse { get; init; } = [];
    /// <summary>候选设备查询中需要排除的精确指纹。</summary>
    public byte[] ExcludeFpExact { get; init; } = [];
    /// <summary>用于设备关联的截断 IP 前缀。</summary>
    public string IpPrefix { get; init; } = string.Empty;
    /// <summary>数据库保存的设备信号格式版本。</summary>
    public int SigVersion { get; init; }
    /// <summary>候选设备查询允许的最早活动时间。</summary>
    public DateTimeOffset SeenSince { get; init; }
    /// <summary>本次查询或处理允许返回的最大数量。</summary>
    public int Limit { get; init; }
}

internal sealed class CoarseCandidateRow
{
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; }
    /// <summary>概率性关联的访客标识，不作为账号资源归属依据。</summary>
    public Guid VisitorId { get; init; }
}

internal sealed class DeviceRow
{
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; }
    /// <summary>精确设备指纹摘要。</summary>
    public byte[] FpExact { get; init; } = [];
    /// <summary>粗粒度设备指纹摘要，用于受限候选关联。</summary>
    public byte[] FpCoarse { get; init; } = [];
    /// <summary>用于设备关联的截断 IP 前缀。</summary>
    public string IpPrefix { get; init; } = string.Empty;
    /// <summary>本次设备握手提交的信号集合。</summary>
    public string Signals { get; init; } = "{}";
    /// <summary>数据库保存的设备信号格式版本。</summary>
    public int SigVersion { get; init; }
    /// <summary>浏览器发送的 User-Agent 信息。</summary>
    public string? UserAgent { get; init; }

    // Npgsql 将 timestamptz 实例化为 UTC DateTime；若此处声明为 DateTimeOffset，
    // Dapper 会尝试不存在的 Convert.ChangeType 转换并抛错。
    /// <summary>首次观察到该设备或访客的时间。</summary>
    public DateTime FirstSeen { get; init; }
    /// <summary>最近一次观察到设备或访客的时间。</summary>
    public DateTime LastSeen { get; init; }

    /// <summary>把数据库静态行映射为业务记录，保留既有状态和时间语义。</summary>
    public Device ToDomain() => new(
        new DeviceId(DeviceId), FpExact, FpCoarse, IpPrefix, Signals, SigVersion, UserAgent,
        new DateTimeOffset(DateTime.SpecifyKind(FirstSeen, DateTimeKind.Utc)),
        new DateTimeOffset(DateTime.SpecifyKind(LastSeen, DateTimeKind.Utc)));
}
