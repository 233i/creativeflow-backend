using Microsoft.AspNetCore.Mvc;
using CreativeFlow.Api.Http;
using CreativeFlow.Api.Middleware;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Application.Features.Generation;

namespace CreativeFlow.Api.Controllers;

/// <summary>普通画布异步生图。202 仅表示任务已持久化，最终结果通过查询获取。</summary>
[ApiController]
[Route("api/v1/ai/image-jobs")]
[Tags("ai")]
public sealed class ImageJobsController(ImageJobService service, IImageJobRepository jobs, ImageQueueLimits limits) : ControllerBase
{
    /// <summary>幂等提交，页面关闭不会取消任务。队列满返回 429，同 Key 不同参数返回 409。</summary>
    [HttpPost(Name = "SubmitImageJob")]
    [ProducesResponseType<ImageJobResponse>(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> Submit([FromBody] SubmitImageJobRequest request, CancellationToken ct)
    {
        if (HttpContext.GetAccountId() is not { } account || HttpContext.GetDeviceId() is not { } device) return Unauthorized();
        try { return await AcceptedAsync(await service.SubmitAsync(account.Value, device.Value, request, ct), ct); }
        catch (ArgumentException error) { return BadRequest(new ErrorResponse(error.Message, [])); }
    }

    /// <summary>重新查询本站任务及产物；不调用生成接口、不重试下载、不会产生模型费用。</summary>
    [HttpGet("{jobId:guid}", Name = "GetImageJob")]
    [ProducesResponseType<ImageJobResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Query(Guid jobId, CancellationToken ct)
    {
        if (HttpContext.GetAccountId() is not { } account) return Unauthorized();
        var job = await jobs.FindAsync(account.Value, jobId, ct);
        return job is null ? NotFound() : Ok(await service.DescribeAsync(job, ct));
    }

    /// <summary>恢复当前账号指定画布的任务，包括关闭页面期间完成的任务。</summary>
    [HttpGet("by-submission/{key:guid}", Name = "FindImageJobSubmission")]
    [ProducesResponseType<ImageJobResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> FindSubmission(Guid key, CancellationToken ct)
    {
        if (HttpContext.GetAccountId() is not { } account) return Unauthorized();
        var job = await jobs.FindSubmissionAsync(account.Value, key, ct);
        return job is null ? NotFound() : Ok(await service.DescribeAsync(job, ct));
    }

    /// <summary>恢复当前账号指定画布的任务，包括关闭页面期间完成的任务。</summary>
    [HttpGet(Name = "ListImageJobs")]
    [ProducesResponseType<ImageJobPage>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] Guid canvasId, [FromQuery] Guid? before, CancellationToken ct)
    {
        if (HttpContext.GetAccountId() is not { } account) return Unauthorized();
        var page = await jobs.ListAsync(account.Value, canvasId, before, 51, ct);
        var items = new List<ImageJobResponse>();
        foreach (var job in page.Take(50)) items.Add(await service.DescribeAsync(job, ct));
        return Ok(new ImageJobPage([.. items], page.Count > 50 ? items[^1].JobId : null));
    }

    /// <summary>只从持久化检查点恢复下载或保存，永不重新调用模型。</summary>
    [HttpPost("{jobId:guid}/retry-download", Name = "RetryImageJobDownload")]
    [ProducesResponseType<ImageJobResponse>(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> RetryDownload(Guid jobId, CancellationToken ct)
    {
        if (HttpContext.GetAccountId() is not { } account) return Unauthorized();
        if (await jobs.FindAsync(account.Value, jobId, ct) is not { } job) return NotFound();
        if (!job.HasCheckpoint) return Conflict(new ErrorResponse("没有已保存的上游产物，无法重新下载。此操作不会重新生成。", []));
        if (!await jobs.RetryDownloadAsync(account.Value, jobId, limits, ct))
            return StatusCode(429, new ErrorResponse("暂时无法恢复下载，请稍后重新查询或重试。", []));
        return Accepted($"/api/v1/ai/image-jobs/{jobId}", await service.DescribeAsync((await jobs.FindAsync(account.Value, jobId, ct))!, ct));
    }

    /// <summary>明确重新生成：按原参数创建新任务，可能再次收费。重试此请求必须复用 Key。</summary>
    [HttpPost("{jobId:guid}/regenerate", Name = "RegenerateImageJob")]
    [ProducesResponseType<ImageJobResponse>(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> Regenerate(Guid jobId, [FromBody] RegenerateImageJobRequest request, CancellationToken ct)
    {
        if (HttpContext.GetAccountId() is not { } account || HttpContext.GetDeviceId() is not { } device) return Unauthorized();
        if (await jobs.FindAsync(account.Value, jobId, ct) is not { } job) return NotFound();
        try
        {
            return await AcceptedAsync(await service.SubmitAsync(account.Value, device.Value,
                new(request.Key, ImageJobService.Deserialize(job.RequestJson)), ct, jobId), ct);
        }
        catch (ArgumentException error) { return BadRequest(new ErrorResponse(error.Message, [])); }
    }

    /// <summary>将提交结果映射为 202、幂等冲突或带重试提示的队列拒绝。</summary>
    private async Task<IActionResult> AcceptedAsync(ImageJobSubmission result, CancellationToken ct)
    {
        if (result.Conflict) return Conflict(new ErrorResponse("相同提交 Key 不能用于不同参数。", []));
        if (result.QueueFull)
        {
            Response.Headers.RetryAfter = "5";
            return StatusCode(429, new ErrorResponse("图片任务队列已满，请稍后提交。", []));
        }
        var job = result.Job!;
        return Accepted($"/api/v1/ai/image-jobs/{job.JobId}", await service.DescribeAsync(job, ct));
    }
}
