using Dapper;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Infrastructure.Persistence;

/// <summary>初始化 WorkflowTemplateRepository 的依赖与实例状态。</summary>
public sealed class WorkflowTemplateRepository(CreativeFlowConnectionFactory connections)
    : IWorkflowTemplateRepository
{
    /// <summary>按搜索词、分类和可选发布状态查询工作流模板，返回匹配的模板元数据与图快照。</summary>
    public async Task<IReadOnlyList<WorkflowTemplate>> ListAsync(
        string? search,
        string? category,
        WorkflowTemplateStatus? status,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<WorkflowTemplateRow>(
            """
            SELECT template_id, name, description, category, tags, schema_version,
                   graph::text AS graph_json, status, version, is_featured, sort_order,
                   use_count, created_at, updated_at, published_at
            FROM workflow_template
            WHERE (@Status IS NULL OR status = @Status)
              AND deleted_at IS NULL
              AND (@Category IS NULL OR category = @Category)
              AND (@Search IS NULL
                   OR name ILIKE @Search
                   OR description ILIKE @Search
                   OR array_to_string(tags, ' ') ILIKE @Search)
            ORDER BY is_featured DESC, sort_order ASC,
                     published_at DESC NULLS LAST, updated_at DESC
            """,
            new WorkflowTemplateListParams
            {
                Search = SearchPattern(search),
                Category = NormalizeFilter(category),
                Status = status is null ? null : ToStorageStatus(status.Value),
            });

        return rows.Select(row => row.ToDomain()).ToList();
    }

    /// <summary>按模板标识读取模板，未找到时返回空。</summary>
    public async Task<WorkflowTemplate?> FindAsync(
        WorkflowTemplateId id,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var row = await connection.QueryFirstOrDefaultAsync<WorkflowTemplateRow>(
            """
            SELECT template_id, name, description, category, tags, schema_version,
                   graph::text AS graph_json, status, version, is_featured, sort_order,
                   use_count, created_at, updated_at, published_at
            FROM workflow_template
            WHERE template_id = @TemplateId AND deleted_at IS NULL
            """,
            new WorkflowTemplateIdParams { TemplateId = id.Value });

        return row?.ToDomain();
    }

    /// <summary>保存新的工作流模板配置、图快照及发布状态。</summary>
    public async Task InsertAsync(WorkflowTemplate template, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await connection.ExecuteAsync(
            """
            INSERT INTO workflow_template
                (template_id, name, description, category, tags, schema_version, graph,
                 status, version, is_featured, sort_order, use_count, created_at,
                 updated_at, published_at)
            VALUES
                (@TemplateId, @Name, @Description, @Category, @Tags, @SchemaVersion,
                 @GraphJson::jsonb, @Status, @Version, @IsFeatured, @SortOrder,
                 @UseCount, @CreatedAt, @UpdatedAt, @PublishedAt)
            """,
            WorkflowTemplateWriteParams.From(template));
    }

    /// <summary>按模板标识更新配置、图快照和状态，返回是否命中记录。</summary>
    public async Task<bool> UpdateAsync(WorkflowTemplate template, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var affected = await connection.ExecuteAsync(
            """
            UPDATE workflow_template
            SET name = @Name,
                description = @Description,
                category = @Category,
                tags = @Tags,
                schema_version = @SchemaVersion,
                graph = @GraphJson::jsonb,
                status = @Status,
                version = @Version,
                is_featured = @IsFeatured,
                sort_order = @SortOrder,
                use_count = @UseCount,
                updated_at = @UpdatedAt,
                published_at = @PublishedAt
            WHERE template_id = @TemplateId AND deleted_at IS NULL
            """,
            WorkflowTemplateWriteParams.From(template));

        return affected > 0;
    }

    /// <summary>仅对仍为已发布状态的模板递增使用次数。</summary>
    public async Task IncrementUseCountAsync(WorkflowTemplateId id, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await connection.ExecuteAsync(
            """
            UPDATE workflow_template
            SET use_count = use_count + 1
            WHERE template_id = @TemplateId AND status = 'published' AND deleted_at IS NULL
            """,
            new WorkflowTemplateIdParams { TemplateId = id.Value });
    }

    /// <summary>根据原有转义规则构造模板搜索参数。</summary>
    private static string? SearchPattern(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : $"%{value.Trim()}%";

    /// <summary>规范化可选的模板筛选条件。</summary>
    private static string? NormalizeFilter(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>将模板状态转换为既有数据库字符串。</summary>
    internal static string ToStorageStatus(WorkflowTemplateStatus status) => status switch
    {
        WorkflowTemplateStatus.Draft => "draft",
        WorkflowTemplateStatus.Published => "published",
        WorkflowTemplateStatus.Archived => "archived",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    /// <summary>将数据库中的既有状态字符串还原为业务枚举。</summary>
    internal static WorkflowTemplateStatus ParseStatus(string status) => status switch
    {
        "draft" => WorkflowTemplateStatus.Draft,
        "published" => WorkflowTemplateStatus.Published,
        "archived" => WorkflowTemplateStatus.Archived,
        _ => throw new InvalidOperationException($"Unknown workflow template status '{status}'."),
    };
}

internal sealed class WorkflowTemplateListParams
{
    /// <summary>列表查询的搜索文本。</summary>
    public string? Search { get; init; }
    /// <summary>所属的模型、模板或内容分类。</summary>
    public string? Category { get; init; }
    /// <summary>当前对象的业务状态，存储和接口名称保持既有映射。</summary>
    public string? Status { get; init; }
}

internal sealed class WorkflowTemplateIdParams
{
    /// <summary>工作流模板的唯一标识。</summary>
    public Guid TemplateId { get; init; }
}

internal sealed class WorkflowTemplateWriteParams
{
    /// <summary>工作流模板的唯一标识。</summary>
    public Guid TemplateId { get; init; }
    /// <summary>实体或配置项的名称。</summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>面向用户或模型展示的说明文本。</summary>
    public string Description { get; init; } = string.Empty;
    /// <summary>所属的模型、模板或内容分类。</summary>
    public string Category { get; init; } = string.Empty;
    /// <summary>模板或内容项的标签集合。</summary>
    public string[] Tags { get; init; } = [];
    /// <summary>图或信号载荷的 schema 版本。</summary>
    public int SchemaVersion { get; init; }
    /// <summary>按现有 schema 保存的完整图 JSON 快照。</summary>
    public string GraphJson { get; init; } = "{}";
    /// <summary>当前对象的业务状态，存储和接口名称保持既有映射。</summary>
    public string Status { get; init; } = string.Empty;
    /// <summary>实体、发布版本或协议的版本号。</summary>
    public int Version { get; init; }
    /// <summary>模板是否被标记为推荐项。</summary>
    public bool IsFeatured { get; init; }
    /// <summary>界面显示及列表查询使用的排序值。</summary>
    public int SortOrder { get; init; }
    /// <summary>模板累计实例化次数。</summary>
    public long UseCount { get; init; }
    /// <summary>记录创建时间，沿用当前 UTC 与序列化约定。</summary>
    public DateTimeOffset CreatedAt { get; init; }
    /// <summary>记录最近一次修改的 UTC 时间。</summary>
    public DateTimeOffset UpdatedAt { get; init; }
    /// <summary>模板或工作流版本发布时间。</summary>
    public DateTimeOffset? PublishedAt { get; init; }

    /// <summary>把业务记录投影为当前存储操作的静态参数。</summary>
    public static WorkflowTemplateWriteParams From(WorkflowTemplate template) => new()
    {
        TemplateId = template.Id.Value,
        Name = template.Name,
        Description = template.Description,
        Category = template.Category,
        Tags = template.Tags,
        SchemaVersion = template.SchemaVersion,
        GraphJson = template.GraphJson,
        Status = WorkflowTemplateRepository.ToStorageStatus(template.Status),
        Version = template.Version,
        IsFeatured = template.IsFeatured,
        SortOrder = template.SortOrder,
        UseCount = template.UseCount,
        CreatedAt = template.CreatedAt,
        UpdatedAt = template.UpdatedAt,
        PublishedAt = template.PublishedAt,
    };
}

internal sealed class WorkflowTemplateRow
{
    /// <summary>工作流模板的唯一标识。</summary>
    public Guid TemplateId { get; init; }
    /// <summary>实体或配置项的名称。</summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>面向用户或模型展示的说明文本。</summary>
    public string Description { get; init; } = string.Empty;
    /// <summary>所属的模型、模板或内容分类。</summary>
    public string Category { get; init; } = string.Empty;
    /// <summary>模板或内容项的标签集合。</summary>
    public string[] Tags { get; init; } = [];
    /// <summary>图或信号载荷的 schema 版本。</summary>
    public int SchemaVersion { get; init; }
    /// <summary>按现有 schema 保存的完整图 JSON 快照。</summary>
    public string GraphJson { get; init; } = "{}";
    /// <summary>当前对象的业务状态，存储和接口名称保持既有映射。</summary>
    public string Status { get; init; } = string.Empty;
    /// <summary>实体、发布版本或协议的版本号。</summary>
    public int Version { get; init; }
    /// <summary>模板是否被标记为推荐项。</summary>
    public bool IsFeatured { get; init; }
    /// <summary>界面显示及列表查询使用的排序值。</summary>
    public int SortOrder { get; init; }
    /// <summary>模板累计实例化次数。</summary>
    public long UseCount { get; init; }
    /// <summary>记录创建时间，沿用当前 UTC 与序列化约定。</summary>
    public DateTime CreatedAt { get; init; }
    /// <summary>记录最近一次修改的 UTC 时间。</summary>
    public DateTime UpdatedAt { get; init; }
    /// <summary>模板或工作流版本发布时间。</summary>
    public DateTime? PublishedAt { get; init; }

    /// <summary>把数据库静态行映射为业务记录，保留既有状态和时间语义。</summary>
    public WorkflowTemplate ToDomain() => new(
        new WorkflowTemplateId(TemplateId),
        Name,
        Description,
        Category,
        Tags,
        SchemaVersion,
        GraphJson,
        WorkflowTemplateRepository.ParseStatus(Status),
        Version,
        IsFeatured,
        SortOrder,
        UseCount,
        WorkflowPersistenceTime.ToOffset(CreatedAt),
        WorkflowPersistenceTime.ToOffset(UpdatedAt),
        WorkflowPersistenceTime.ToOffset(PublishedAt));
}
