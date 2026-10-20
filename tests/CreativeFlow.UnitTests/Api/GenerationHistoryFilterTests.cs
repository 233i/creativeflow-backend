using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using CreativeFlow.Api.Http;
using CreativeFlow.Api.Hubs;
using CreativeFlow.Api.Http.Contracts;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Domain.Identity;
using CreativeFlow.UnitTests.Support;

namespace CreativeFlow.UnitTests.Api;

/// <summary>验证记录采集不修改生成结果，且失败、取消和存储故障均有明确处理。</summary>
public sealed class GenerationHistoryFilterTests
{
    /// <summary>构造已验证账号和设备的 MVC 上下文，不注入管理员身份。</summary>
    private static ActionExecutingContext Context(object request, bool authenticated = true)
    {
        var http = new DefaultHttpContext();
        if (authenticated)
        {
            http.Items[DeviceContextItems.DeviceId] = DeviceId.New();
            http.Items[DeviceContextItems.AccountId] = AccountId.New();
        }
        return new(new ActionContext(http, new RouteData(), new ActionDescriptor()), [],
            new Dictionary<string, object?> { ["request"] = request }, new object());
    }

    /// <summary>提供合法的文本参数，附加画布定位不改变原有字段。</summary>
    private static GenerateTextRequest TextRequest() => new("provider", "model", "input", null, null, null, null)
        { CanvasId = Guid.NewGuid().ToString(), NodeId = "text-1" };

    /// <summary>文本和多图响应完整记录，MVC 返回的原对象保持一致。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulResultAndAllArtifactsRemainUnchanged(bool images)
    {
        var history = new RecordingRequestHistory();
        var request = TextRequest();
        var context = Context(request);
        if (images) context.ActionArguments["request"] = new GenerateImageRequest("provider", "model", "input", null, null, null, null, null, 2, null)
            { CanvasId = request.CanvasId, NodeId = request.NodeId };
        IActionResult result = images ? new OkObjectResult(new GenerateImageResponse([
            new("/one", "image/png", Guid.NewGuid().ToString()), new("/two", "image/png", Guid.NewGuid().ToString())]))
            : new OkObjectResult(new GenerateTextResponse("原始生成结果"));
        var executed = new ActionExecutedContext(context, [], new object()) { Result = result };
        await new GenerationHistoryFilter(history, NullLogger<GenerationHistoryFilter>.Instance)
            .OnActionExecutionAsync(context, () => Task.FromResult(executed));
        Assert.Same(result, executed.Result);
        var start = Assert.Single(history.Starts);
        Assert.Equal(images ? "image" : "text", start.Kind);
        Assert.Equal(Guid.Parse(request.CanvasId!), start.CanvasId);
        Assert.Equal("text-1", start.NodeId);
        var finish = Assert.Single(history.Finishes);
        Assert.Equal("succeeded", finish.Status);
        using var output = System.Text.Json.JsonDocument.Parse(finish.Output!);
        if (images) Assert.Equal(2, output.RootElement.GetProperty("assets").GetArrayLength());
        else Assert.Equal("原始生成结果", output.RootElement.GetProperty("text").GetString());
    }

    /// <summary>异步视频仅绑定任务标识，不把接受响应误报为生成成功。</summary>
    [Fact]
    public async Task AcceptedVideoTracksTheExistingJob()
    {
        var history = new RecordingRequestHistory();
        var context = Context(new StartVideoRequest("provider", "model", "input", null, null, null, null, null, null, null, null, null));
        var job = Guid.NewGuid();
        await new GenerationHistoryFilter(history, NullLogger<GenerationHistoryFilter>.Instance)
            .OnActionExecutionAsync(context, () => Task.FromResult(new ActionExecutedContext(context, [], new object())
                { Result = new AcceptedResult((string?)null, new StartVideoResponse(job.ToString())) }));
        Assert.Equal("video", Assert.Single(history.Starts).Kind);
        var finish = Assert.Single(history.Finishes);
        Assert.Equal("running", finish.Status);
        Assert.Equal(job, finish.VideoJobId);
        Assert.Null(finish.Output);
    }

    /// <summary>业务校验失败保存原因，并保持原 HTTP 错误对象。</summary>
    [Fact]
    public async Task ValidationFailureRetainsTheOriginalReason()
    {
        var history = new RecordingRequestHistory();
        var context = Context(TextRequest());
        var result = new BadRequestObjectResult(new ErrorResponse("invalid_request", ["缺少提示词"]));
        await new GenerationHistoryFilter(history, NullLogger<GenerationHistoryFilter>.Instance)
            .OnActionExecutionAsync(context, () => Task.FromResult(new ActionExecutedContext(context, [], new object()) { Result = result }));
        Assert.Equal("failed", Assert.Single(history.Finishes).Status);
        Assert.Equal("缺少提示词", history.Finishes[0].Error);
    }

    /// <summary>客户端取消仍写入终态，并原样传播异常。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExceptionsPropagateAndHistoryUsesIndependentCancellation(bool cancel)
    {
        var history = new RecordingRequestHistory();
        var context = Context(TextRequest());
        using var abort = new CancellationTokenSource();
        context.HttpContext.RequestAborted = abort.Token;
        Exception failure = cancel ? new OperationCanceledException() : new IOException("internal exception");
        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => new GenerationHistoryFilter(history, NullLogger<GenerationHistoryFilter>.Instance)
            .OnActionExecutionAsync(context, () => { if (cancel) abort.Cancel(); return Task.FromException<ActionExecutedContext>(failure); }));
        Assert.Same(failure, thrown);
        Assert.Equal(cancel ? "cancelled" : "failed", Assert.Single(history.Finishes).Status);
        Assert.False(history.WriteWasCancelled);
    }

    /// <summary>存储故障和未登录请求均不能阻止原业务管线继续执行。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnauthenticatedOrUnavailableHistoryDoesNotChangeTheAction(bool authenticated)
    {
        var history = new RecordingRequestHistory { ThrowOnWrite = true };
        var context = Context(TextRequest(), authenticated);
        var called = false;
        await new GenerationHistoryFilter(history, NullLogger<GenerationHistoryFilter>.Instance)
            .OnActionExecutionAsync(context, () => { called = true; return Task.FromResult(new ActionExecutedContext(context, [], new object())); });
        Assert.True(called);
        Assert.Empty(history.Starts);
    }
}
