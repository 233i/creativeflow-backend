namespace CreativeFlow.Domain.Ai;

/// <param name="Value">底层 UUID 值，保持现有标识编码与持久化格式。</param>
public readonly record struct WorkflowTemplateId(Guid Value)
{
    /// <summary>生成新的 UUIDv7 标识，保留当前持久化排序特性。</summary>
    public static WorkflowTemplateId New() => new(Guid.CreateVersion7());

    /// <summary>解析非空 UUID 标识；无效或全零输入返回失败并清空输出。</summary>
    public static bool TryParse(string? value, out WorkflowTemplateId id)
    {
        if (Guid.TryParse(value, out var parsed))
        {
            id = new WorkflowTemplateId(parsed);
            return true;
        }

        id = default;
        return false;
    }

    /// <summary>以既有标准 UUID 字符串格式输出该标识。</summary>
    public override string ToString() => Value.ToString();
}

public enum WorkflowTemplateStatus
{
    /// <summary>尚未对普通用户发布的模板草稿。</summary>
    Draft,
    /// <summary>当前可公开使用的已发布版本。</summary>
    Published,
    /// <summary>已经下架并保留历史记录的模板。</summary>
    Archived,
}

/// <summary>由管理员拥有的可复用工作流定义。</summary>
/// <param name="Id">当前实体或契约项目的唯一标识。</param>
/// <param name="Name">实体或配置项的名称。</param>
/// <param name="Description">面向用户或模型展示的说明文本。</param>
/// <param name="Category">所属的模型、模板或内容分类。</param>
/// <param name="Tags">模板或内容项的标签集合。</param>
/// <param name="SchemaVersion">图或信号载荷的 schema 版本。</param>
/// <param name="GraphJson">按现有 schema 保存的完整图 JSON 快照。</param>
/// <param name="Status">当前对象的业务状态，存储和接口名称保持既有映射。</param>
/// <param name="Version">实体、发布版本或协议的版本号。</param>
/// <param name="IsFeatured">模板是否被标记为推荐项。</param>
/// <param name="SortOrder">界面显示及列表查询使用的排序值。</param>
/// <param name="UseCount">模板累计实例化次数。</param>
/// <param name="CreatedAt">记录创建时间，沿用当前 UTC 与序列化约定。</param>
/// <param name="UpdatedAt">记录最近一次修改的 UTC 时间。</param>
/// <param name="PublishedAt">模板或工作流版本发布时间。</param>
public sealed record WorkflowTemplate(
    WorkflowTemplateId Id,
    string Name,
    string Description,
    string Category,
    string[] Tags,
    int SchemaVersion,
    string GraphJson,
    WorkflowTemplateStatus Status,
    int Version,
    bool IsFeatured,
    int SortOrder,
    long UseCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? PublishedAt);
