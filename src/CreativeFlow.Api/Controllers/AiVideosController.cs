using Microsoft.AspNetCore.Mvc;
using CreativeFlow.Api.Http.Handlers;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Abstractions.Security;
using CreativeFlow.Application.Contracts.Ai;

namespace CreativeFlow.Api.Controllers;

/// <summary>装配视频生成任务的 HTTP 入口及其用例/端口依赖。</summary>
[ApiController]
[Route("api/v1/ai/videos")]
[Tags("ai")]
public sealed class AiVideosController(
    IProviderRepository providers,
    IApiKeyProtector protector,
    IVideoGenerationDispatcher dispatcher,
    IVideoJobRepository jobs,
    ICanvasAssetRepository assets,
    IAssetStore assetStore,
    ISignedMediaUrlService signedUrls) : ControllerBase
{
    /// <summary>校验输入并提交异步视频生成任务。</summary>
    [HttpPost("", Name = "StartVideoJob")]
    public async Task<IActionResult> Start(
        [FromBody] StartVideoRequest request,
        CancellationToken ct) =>
        await AiVideoHandlers.StartAsync(
            request,
            HttpContext,
            providers,
            protector,
            dispatcher,
            jobs,
            assets,
            assetStore,
            signedUrls,
            ct);

    /// <summary>读取视频任务当前持久化状态与结果。</summary>
    [HttpGet("{id}", Name = "GetVideoJob")]
    public async Task<IActionResult> GetStatus([FromRoute] string id, CancellationToken ct) =>
        await AiVideoHandlers.GetStatusAsync(id, HttpContext, jobs, ct);

    /// <summary>请求取消视频生成任务，保持既有的终止与清理规则。</summary>
    [HttpDelete("{id}", Name = "CancelVideoJob")]
    public async Task<IActionResult> Cancel([FromRoute] string id, CancellationToken ct) =>
        await AiVideoHandlers.CancelAsync(id, HttpContext, jobs, ct);
}
