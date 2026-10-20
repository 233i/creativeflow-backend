using Microsoft.AspNetCore.Mvc;
using CreativeFlow.Api.Http.Handlers;
using CreativeFlow.Api.Http;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Abstractions.Security;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Infrastructure.Options;

namespace CreativeFlow.Api.Controllers;

/// <summary>装配工作流发布的 HTTP 入口及其用例/端口依赖。</summary>
[ApiController]
[Route("api/v1/admin/workflow-apis")]
[Tags("admin")]
[TypeFilter(typeof(WorkflowApiAccessFilter), Arguments = new object[] { true })]
public sealed class WorkflowPublicationController(
    IWorkflowRepository workflows,
    IWorkflowRunRepository runs,
    IWorkflowExternalApiRepository externalApi,
    IWorkflowApiKeyService keyService,
    ExternalWorkflowApiOptions options) : ControllerBase
{
    /// <summary>验证工作流发布并保存不可变的发布快照。</summary>
    [HttpPost("{id}/publish", Name = "PublishWorkflowApi")]
    public async Task<IActionResult> Publish(
        [FromRoute] string id,
        [FromBody] PublishWorkflowRequest request,
        CancellationToken ct) =>
        await WorkflowPublicationHandlers.PublishAsync(
            id, request, HttpContext, workflows, externalApi, options, ct);

    /// <summary>读取工作流已有的发布版本。</summary>
    [HttpGet("{id}/deployments", Name = "ListWorkflowDeployments")]
    public async Task<IActionResult> ListDeployments(
        [FromRoute] string id,
        CancellationToken ct) =>
        await WorkflowPublicationHandlers.ListDeploymentsAsync(
            id, HttpContext, externalApi, options, ct);

    /// <summary>停用工作流的对外发布入口。</summary>
    [HttpDelete("{id}/deployment", Name = "DisableWorkflowApi")]
    public async Task<IActionResult> Disable(
        [FromRoute] string id,
        CancellationToken ct) =>
        await WorkflowPublicationHandlers.DisableAsync(
            id, HttpContext, externalApi, options, ct);

    /// <summary>为工作流创建 API Key，并按原规则仅在签发时返回明文。</summary>
    [HttpPost("{id}/api-keys", Name = "CreateWorkflowApiKey")]
    public async Task<IActionResult> CreateApiKey(
        [FromRoute] string id,
        [FromBody] CreateWorkflowApiKeyRequest request,
        CancellationToken ct) =>
        await WorkflowPublicationHandlers.CreateApiKeyAsync(
            id, request, HttpContext, workflows, externalApi, keyService, options, ct);

    /// <summary>列出工作流 API Key 的安全元数据。</summary>
    [HttpGet("{id}/api-keys", Name = "ListWorkflowApiKeys")]
    public async Task<IActionResult> ListApiKeys(
        [FromRoute] string id,
        CancellationToken ct) =>
        await WorkflowPublicationHandlers.ListApiKeysAsync(
            id, HttpContext, externalApi, options, ct);

    /// <summary>撤销指定工作流 API Key。</summary>
    [HttpDelete("{id}/api-keys/{keyId}", Name = "RevokeWorkflowApiKey")]
    public async Task<IActionResult> RevokeApiKey(
        [FromRoute] string id,
        [FromRoute] string keyId,
        CancellationToken ct) =>
        await WorkflowPublicationHandlers.RevokeApiKeyAsync(
            id, keyId, HttpContext, externalApi, options, ct);

    /// <summary>列出工作流由外部 API 触发的运行。</summary>
    [HttpGet("{id}/external-runs", Name = "ListWorkflowExternalRuns")]
    public async Task<IActionResult> ListExternalRuns(
        [FromRoute] string id,
        CancellationToken ct) =>
        await WorkflowPublicationHandlers.ListExternalRunsAsync(
            id, HttpContext, runs, options, ct);

    /// <summary>管理员查看一次外部调用的节点结果；v2 仍复验原 Key 和知识/HTTP 授权。</summary>
    [HttpGet("{id}/external-runs/{runId}", Name = "GetAdminWorkflowExternalRun")]
    public Task<IActionResult> GetExternalRun([FromRoute] string id, [FromRoute] string runId, CancellationToken ct) =>
        WorkflowPublicationHandlers.GetExternalRunAsync(id, runId, HttpContext, workflows, runs, options, ct);

    /// <summary>只读返回外部运行当时的图；仍验证原 Key 和知识/HTTP 资源权限。</summary>
    [HttpGet("{id}/external-runs/{runId}/snapshot", Name = "GetAdminExternalRunSnapshot")]
    public Task<IActionResult> GetExternalRunSnapshot([FromRoute] string id, [FromRoute] string runId, CancellationToken ct) =>
        WorkflowPublicationHandlers.GetExternalRunAsync(id, runId, HttpContext, workflows, runs, options, ct, snapshot: true);
}
