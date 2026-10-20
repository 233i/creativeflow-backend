using Microsoft.AspNetCore.Mvc;
using CreativeFlow.Api.Http.Handlers;
using CreativeFlow.Api.Http;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Abstractions.Security;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Infrastructure.Options;

namespace CreativeFlow.Api.Controllers;

/// <summary>装配外部工作流的 HTTP 入口及其用例/端口依赖。</summary>
[ApiController]
[Route("api/external/v1")]
[Tags("external-workflows")]
public sealed class ExternalWorkflowsController(
    IWorkflowExternalApiRepository externalApi,
    IWorkflowRunRepository runs,
    IWorkflowApiKeyService keyService,
    IAssetStore assetStore,
    ICanvasAssetRepository assets,
    ISignedMediaUrlService signedUrls,
    ExternalWorkflowRateLimiter rateLimiter,
    ExternalWorkflowApiOptions options) : ControllerBase
{
    /// <summary>读取当前入口可见的工作流列表。</summary>
    [HttpGet("workflows", Name = "ListPublishedExternalWorkflows")]
    public async Task<IActionResult> ListWorkflows(CancellationToken ct) =>
        await ExternalWorkflowHandlers.ListCatalogAsync(externalApi, options, ct);

    /// <summary>读取指定工作流的公开信息。</summary>
    [HttpGet("workflows/{id}", Name = "GetPublishedExternalWorkflow")]
    public async Task<IActionResult> GetWorkflow([FromRoute] string id, CancellationToken ct) =>
        await ExternalWorkflowHandlers.GetCatalogItemAsync(id, externalApi, options, ct);

    /// <summary>按外部工作流的输入限制验证并保存图片。</summary>
    [HttpPost("assets/images", Name = "UploadExternalWorkflowImage")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> UploadImage(
        IFormFile file,
        CancellationToken ct) =>
        await ExternalWorkflowHandlers.UploadImageAsync(
            file, HttpContext, externalApi, keyService, rateLimiter, assetStore, assets, options, ct);

    /// <summary>校验输入并创建持久化运行，随后由既有后台执行机制推进。</summary>
    [HttpPost("workflows/{id}/runs", Name = "CreateExternalWorkflowRun")]
    public async Task<IActionResult> CreateRun(
        [FromRoute] string id,
        [FromBody] CreateExternalWorkflowRunRequest request,
        CancellationToken ct) =>
        await ExternalWorkflowHandlers.CreateRunAsync(
            id, request, HttpContext, externalApi, runs, keyService, rateLimiter, assets, options, ct);

    /// <summary>读取指定运行及其当前持久化状态。</summary>
    [HttpGet("runs/{runId}", Name = "GetExternalWorkflowRun")]
    public async Task<IActionResult> GetRun(
        [FromRoute] string runId,
        CancellationToken ct) =>
        await ExternalWorkflowHandlers.GetRunAsync(
            runId, HttpContext, externalApi, runs, keyService, rateLimiter,
            assets, signedUrls, options, ct);

    /// <summary>持续订阅已有运行的进度、公开文本增量和最终结果。</summary>
    [HttpGet("runs/{runId}/events", Name = "StreamExternalWorkflowRun")]
    public async Task<IActionResult> StreamRun([FromRoute] string runId,
        [FromServices] IWorkflowRunEventRepository events, CancellationToken ct) =>
        await ExternalWorkflowHandlers.StreamRunAsync(runId, HttpContext, externalApi, runs, events,
            keyService, rateLimiter, assets, signedUrls, options, ct);

    /// <summary>请求取消指定运行，沿用当前终止状态规则。</summary>
    [HttpDelete("runs/{runId}", Name = "CancelExternalWorkflowRun")]
    public async Task<IActionResult> CancelRun(
        [FromRoute] string runId,
        CancellationToken ct) =>
        await ExternalWorkflowHandlers.CancelRunAsync(
            runId, HttpContext, externalApi, runs, keyService, rateLimiter, options, ct);
}
