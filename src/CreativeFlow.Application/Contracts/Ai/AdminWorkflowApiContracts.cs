namespace CreativeFlow.Application.Contracts.Ai;

/// <param name="Id">API 的稳定工作流标识。</param>
/// <param name="Name">工作流名称。</param>
/// <param name="Status">草稿、已发布或已停用。</param>
/// <param name="Version">最近一次发布版本。</param>
/// <param name="UpdatedAt">草稿最近修改时间。</param>
public sealed record AdminWorkflowApiSummary(
    string Id, string Name, string Status, int? Version, string UpdatedAt);

/// <param name="Apis">后台管理的全部 API 工作流。</param>
public sealed record AdminWorkflowApiListResponse(AdminWorkflowApiSummary[] Apis);
