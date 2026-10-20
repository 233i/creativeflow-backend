using Dapper;
using Npgsql;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;

namespace CreativeFlow.Infrastructure.Persistence;

/// <summary>初始化 SkillRepository 的依赖与实例状态。</summary>
public sealed class SkillRepository(CreativeFlowConnectionFactory connections) : ISkillRepository
{
    /// <summary>该仓储查询使用的固定列清单。</summary>
    private const string Columns = """
        skill_id, device_id, slug, name, description, storage_path, enabled, has_scripts,
        risk::text AS risk_json, max_risk, byte_size, created_at, updated_at
        """;

    /// <summary>按名称列出当前可访问设备范围内已安装的 Skill 元数据。</summary>
    public async Task<IReadOnlyList<Skill>> ListAsync(DeviceId deviceId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<SkillRow>(
            $"SELECT {Columns} FROM skill WHERE device_id IN (SELECT device_id FROM creativeflow_accessible_device_ids(@DeviceId)) ORDER BY name",
            new DeviceIdParam { DeviceId = deviceId.Value });
        return rows.Select(row => row.ToDomain()).ToList();
    }

    /// <summary>在当前可访问设备范围内按标识读取 Skill 元数据。</summary>
    public async Task<Skill?> FindAsync(DeviceId deviceId, SkillId skillId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var row = await connection.QueryFirstOrDefaultAsync<SkillRow>(
            $"SELECT {Columns} FROM skill WHERE skill_id = @SkillId AND device_id IN (SELECT device_id FROM creativeflow_accessible_device_ids(@DeviceId))",
            new SkillScopeParams { SkillId = skillId.Value, DeviceId = deviceId.Value });
        return row?.ToDomain();
    }

    /// <summary>在当前可访问设备范围内按 slug 读取 Skill 元数据。</summary>
    public async Task<Skill?> FindBySlugAsync(DeviceId deviceId, string slug, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var row = await connection.QueryFirstOrDefaultAsync<SkillRow>(
            $"SELECT {Columns} FROM skill WHERE slug = @Slug AND device_id IN (SELECT device_id FROM creativeflow_accessible_device_ids(@DeviceId))",
            new SkillSlugParams { Slug = slug, DeviceId = deviceId.Value });
        return row?.ToDomain();
    }

    /// <summary>保存 Skill 安装元数据，同一设备的 slug 已存在时返回 false 而不覆盖记录。</summary>
    public async Task<bool> InsertAsync(Skill skill, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var affected = await connection.ExecuteAsync(
            """
            INSERT INTO skill
                (skill_id, device_id, slug, name, description, storage_path, enabled,
                 has_scripts, risk, max_risk, byte_size, created_at, updated_at)
            VALUES
                (@SkillId, @DeviceId, @Slug, @Name, @Description, @StoragePath, @Enabled,
                 @HasScripts, @RiskJson::jsonb, @MaxRisk, @ByteSize, @CreatedAt, @UpdatedAt)
            ON CONFLICT (device_id, slug) DO NOTHING
            """,
            SkillParams.From(skill));
        return affected > 0;
    }

    /// <summary>删除当前可访问设备范围内的 Skill 元数据，不在此处清理包文件。</summary>
    public async Task<bool> DeleteAsync(DeviceId deviceId, SkillId skillId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var affected = await connection.ExecuteAsync(
            "DELETE FROM skill WHERE skill_id = @SkillId AND device_id IN (SELECT device_id FROM creativeflow_accessible_device_ids(@DeviceId))",
            new SkillScopeParams { SkillId = skillId.Value, DeviceId = deviceId.Value });
        return affected > 0;
    }

    /// <summary>将业务枚举映射为既有存储或接口字符串。</summary>
    internal static string ToWire(SkillRiskLevel level) => level switch
    {
        SkillRiskLevel.Safe => "safe",
        SkillRiskLevel.Warning => "warning",
        SkillRiskLevel.Danger => "danger",
        _ => throw new ArgumentOutOfRangeException(nameof(level)),
    };

    /// <summary>将既有持久化风险名称还原为风险枚举。</summary>
    internal static SkillRiskLevel ParseRisk(string value) => value switch
    {
        "safe" => SkillRiskLevel.Safe,
        "warning" => SkillRiskLevel.Warning,
        "danger" => SkillRiskLevel.Danger,
        _ => throw new InvalidOperationException($"Unknown skill risk '{value}'."),
    };
}

internal sealed class SkillScopeParams {
    /// <summary>已安装 Skill 的唯一标识。</summary>
    public Guid SkillId { get; init; }
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; } }
internal sealed class SkillSlugParams {
    /// <summary>Skill 的稳定短名称。</summary>
    public string Slug { get; init; } = string.Empty;
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; } }
internal sealed class SkillParams
{
    /// <summary>已安装 Skill 的唯一标识。</summary>
    public Guid SkillId { get; init; }
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; }
    /// <summary>Skill 的稳定短名称。</summary>
    public string Slug { get; init; } = string.Empty;
    /// <summary>实体或配置项的名称。</summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>面向用户或模型展示的说明文本。</summary>
    public string Description { get; init; } = string.Empty;
    /// <summary>存储后端的不透明键，不能直接当作用户可控文件路径。</summary>
    public string StoragePath { get; init; } = string.Empty;
    /// <summary>当前配置或资源是否启用。</summary>
    public bool Enabled { get; init; }
    /// <summary>Skill 包是否包含可执行脚本。</summary>
    public bool HasScripts { get; init; }
    /// <summary>扫描风险结果的持久化 JSON。</summary>
    public string RiskJson { get; init; } = "[]";
    /// <summary>包扫描中发现的最高风险级别。</summary>
    public string MaxRisk { get; init; } = "safe";
    /// <summary>素材或文件内容的字节数。</summary>
    public long ByteSize { get; init; }
    /// <summary>记录创建时间，沿用当前 UTC 与序列化约定。</summary>
    public DateTimeOffset CreatedAt { get; init; }
    /// <summary>记录最近一次修改的 UTC 时间。</summary>
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>把业务记录投影为当前存储操作的静态参数。</summary>
    public static SkillParams From(Skill skill) => new()
    {
        SkillId = skill.Id.Value,
        DeviceId = skill.DeviceId.Value,
        Slug = skill.Slug,
        Name = skill.Name,
        Description = skill.Description,
        StoragePath = skill.StoragePath,
        Enabled = skill.Enabled,
        HasScripts = skill.HasScripts,
        RiskJson = skill.RiskJson,
        MaxRisk = SkillRepository.ToWire(skill.MaxRisk),
        ByteSize = skill.ByteSize,
        CreatedAt = skill.CreatedAt,
        UpdatedAt = skill.UpdatedAt,
    };
}

internal sealed class SkillRow
{
    /// <summary>已安装 Skill 的唯一标识。</summary>
    public Guid SkillId { get; init; }
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; }
    /// <summary>Skill 的稳定短名称。</summary>
    public string Slug { get; init; } = string.Empty;
    /// <summary>实体或配置项的名称。</summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>面向用户或模型展示的说明文本。</summary>
    public string Description { get; init; } = string.Empty;
    /// <summary>存储后端的不透明键，不能直接当作用户可控文件路径。</summary>
    public string StoragePath { get; init; } = string.Empty;
    /// <summary>当前配置或资源是否启用。</summary>
    public bool Enabled { get; init; }
    /// <summary>Skill 包是否包含可执行脚本。</summary>
    public bool HasScripts { get; init; }
    /// <summary>扫描风险结果的持久化 JSON。</summary>
    public string RiskJson { get; init; } = "[]";
    /// <summary>包扫描中发现的最高风险级别。</summary>
    public string MaxRisk { get; init; } = "safe";
    /// <summary>素材或文件内容的字节数。</summary>
    public long ByteSize { get; init; }
    /// <summary>记录创建时间，沿用当前 UTC 与序列化约定。</summary>
    public DateTime CreatedAt { get; init; }
    /// <summary>记录最近一次修改的 UTC 时间。</summary>
    public DateTime UpdatedAt { get; init; }

    /// <summary>把数据库静态行映射为业务记录，保留既有状态和时间语义。</summary>
    public Skill ToDomain() => new(
        new SkillId(SkillId), new DeviceId(DeviceId), Slug, Name, Description, StoragePath,
        Enabled, HasScripts, RiskJson, SkillRepository.ParseRisk(MaxRisk), ByteSize,
        Utc(CreatedAt), Utc(UpdatedAt));

    /// <summary>将数据库时间值按原有约定转换为 UTC DateTimeOffset。</summary>
    private static DateTimeOffset Utc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
