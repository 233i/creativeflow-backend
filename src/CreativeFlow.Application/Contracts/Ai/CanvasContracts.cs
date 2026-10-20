using System.Text.Json.Nodes;

namespace CreativeFlow.Application.Contracts.Ai;

/// <param name="Graph">
/// 原样传递的节点图。使用 <see cref="JsonNode"/> 而非 DTO：其结构由画布客户端拥有并随之变化，
/// 在服务器端维护同步镜像没有收益。与反射绑定模型不同，<see cref="JsonNode"/> 也兼容 AOT。
/// </param>
/// <param name="Id">当前实体或契约项目的唯一标识。</param>
/// <param name="Name">实体或配置项的名称。</param>
/// <param name="CreatedAt">记录创建时间，沿用当前 UTC 与序列化约定。</param>
/// <param name="UpdatedAt">记录最近一次修改的 UTC 时间。</param>
public sealed record CanvasResponse(
    string Id,
    string Name,
    JsonNode? Graph,
    string CreatedAt,
    string UpdatedAt);

/// <param name="Id">当前实体或契约项目的唯一标识。</param>
/// <param name="Name">实体或配置项的名称。</param>
/// <param name="NodeCount">节点图包含的节点数量。</param>
/// <param name="CreatedAt">记录创建时间，沿用当前 UTC 与序列化约定。</param>
/// <param name="UpdatedAt">记录最近一次修改的 UTC 时间。</param>
public sealed record CanvasSummaryResponse(
    string Id,
    string Name,
    int NodeCount,
    string CreatedAt,
    string UpdatedAt);

/// <param name="Canvases">当前查询返回的画布集合。</param>
public sealed record CanvasListResponse(CanvasSummaryResponse[] Canvases);

/// <param name="Name">实体或配置项的名称。</param>
/// <param name="Graph">完整节点图，保持现有结构与未知字段。</param>
public sealed record CreateCanvasRequest(string? Name, JsonNode? Graph);

/// <param name="Name">空值表示保留当前名称。</param>
/// <param name="Graph">空值表示保留已存储的图，用于仅重命名的场景。</param>
public sealed record UpdateCanvasRequest(string? Name, JsonNode? Graph);
