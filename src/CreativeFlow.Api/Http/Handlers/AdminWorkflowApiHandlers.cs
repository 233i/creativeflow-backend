using CreativeFlow.Api.Middleware;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Contracts.Ai;

namespace CreativeFlow.Api.Http.Handlers;

internal static class AdminWorkflowApiHandlers
{
    /// <summary>在管理员身份验证后列出包含历史发布在内的全部 API 工作流。</summary>
    internal static async Task<IActionResult> ListAsync(
        HttpContext http, IWorkflowRepository workflows, CancellationToken ct)
    {
        if (!http.IsAdminWorkflowApi()) return ApiResults.Unauthorized();
        var list = await workflows.ListApisAsync(ct);
        return ApiResults.Ok(new AdminWorkflowApiListResponse(
            [.. list.Select(item => new AdminWorkflowApiSummary(
                item.WorkflowId.ToString(), item.Name, item.Status, item.Version,
                item.UpdatedAt.ToString("O")))]));
    }
}
