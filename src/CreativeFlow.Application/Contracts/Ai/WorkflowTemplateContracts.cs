using System.Text.Json.Nodes;

namespace CreativeFlow.Application.Contracts.Ai;

/// <param name="Id">当前实体或契约项目的唯一标识。</param>
/// <param name="Name">实体或配置项的名称。</param>
/// <param name="Description">面向用户或模型展示的说明文本。</param>
/// <param name="Category">所属的模型、模板或内容分类。</param>
/// <param name="Tags">模板或内容项的标签集合。</param>
/// <param name="Status">当前对象的业务状态，存储和接口名称保持既有映射。</param>
/// <param name="Version">实体、发布版本或协议的版本号。</param>
/// <param name="IsFeatured">模板是否被标记为推荐项。</param>
/// <param name="SortOrder">界面显示及列表查询使用的排序值。</param>
/// <param name="UseCount">模板累计实例化次数。</param>
/// <param name="NodeCount">节点图包含的节点数量。</param>
/// <param name="TextInputCount">外部契约接受的文本输入数量。</param>
/// <param name="ImageInputCount">外部契约接受的图片输入数量。</param>
/// <param name="OutputKinds">当前端口或工具可以产生的输出种类。</param>
/// <param name="UpdatedAt">记录最近一次修改的 UTC 时间。</param>
/// <param name="PublishedAt">模板或工作流版本发布时间。</param>
public sealed record WorkflowTemplateSummaryResponse(
    string Id,
    string Name,
    string Description,
    string Category,
    string[] Tags,
    string Status,
    int Version,
    bool IsFeatured,
    int SortOrder,
    long UseCount,
    int NodeCount,
    int TextInputCount,
    int ImageInputCount,
    string[] OutputKinds,
    string UpdatedAt,
    string? PublishedAt);

/// <param name="Templates">本次查询返回的工作流模板集合。</param>
public sealed record WorkflowTemplateListResponse(WorkflowTemplateSummaryResponse[] Templates);

/// <param name="Id">当前实体或契约项目的唯一标识。</param>
/// <param name="Name">实体或配置项的名称。</param>
/// <param name="Description">面向用户或模型展示的说明文本。</param>
/// <param name="Category">所属的模型、模板或内容分类。</param>
/// <param name="Tags">模板或内容项的标签集合。</param>
/// <param name="SchemaVersion">图或信号载荷的 schema 版本。</param>
/// <param name="Graph">完整节点图，保持现有结构与未知字段。</param>
/// <param name="Status">当前对象的业务状态，存储和接口名称保持既有映射。</param>
/// <param name="Version">实体、发布版本或协议的版本号。</param>
/// <param name="IsFeatured">模板是否被标记为推荐项。</param>
/// <param name="SortOrder">界面显示及列表查询使用的排序值。</param>
/// <param name="UseCount">模板累计实例化次数。</param>
/// <param name="CreatedAt">记录创建时间，沿用当前 UTC 与序列化约定。</param>
/// <param name="UpdatedAt">记录最近一次修改的 UTC 时间。</param>
/// <param name="PublishedAt">模板或工作流版本发布时间。</param>
public sealed record WorkflowTemplateResponse(
    string Id,
    string Name,
    string Description,
    string Category,
    string[] Tags,
    int SchemaVersion,
    JsonNode? Graph,
    string Status,
    int Version,
    bool IsFeatured,
    int SortOrder,
    long UseCount,
    string CreatedAt,
    string UpdatedAt,
    string? PublishedAt);

/// <param name="Name">实体或配置项的名称。</param>
/// <param name="Description">面向用户或模型展示的说明文本。</param>
/// <param name="Category">所属的模型、模板或内容分类。</param>
/// <param name="Tags">模板或内容项的标签集合。</param>
/// <param name="SchemaVersion">图或信号载荷的 schema 版本。</param>
/// <param name="Graph">完整节点图，保持现有结构与未知字段。</param>
/// <param name="IsFeatured">模板是否被标记为推荐项。</param>
/// <param name="SortOrder">界面显示及列表查询使用的排序值。</param>
public sealed record CreateWorkflowTemplateRequest(
    string? Name,
    string? Description,
    string? Category,
    string[]? Tags,
    int? SchemaVersion,
    JsonNode? Graph,
    bool? IsFeatured,
    int? SortOrder);

/// <param name="Name">实体或配置项的名称。</param>
/// <param name="Description">面向用户或模型展示的说明文本。</param>
/// <param name="Category">所属的模型、模板或内容分类。</param>
/// <param name="Tags">模板或内容项的标签集合。</param>
/// <param name="SchemaVersion">图或信号载荷的 schema 版本。</param>
/// <param name="Graph">完整节点图，保持现有结构与未知字段。</param>
/// <param name="IsFeatured">模板是否被标记为推荐项。</param>
/// <param name="SortOrder">界面显示及列表查询使用的排序值。</param>
public sealed record UpdateWorkflowTemplateRequest(
    string? Name,
    string? Description,
    string? Category,
    string[]? Tags,
    int? SchemaVersion,
    JsonNode? Graph,
    bool? IsFeatured,
    int? SortOrder);

/// <param name="Name">实体或配置项的名称。</param>
public sealed record InstantiateWorkflowTemplateRequest(string? Name);
