using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using CreativeFlow.Api.Middleware;
using CreativeFlow.Api.Serialization;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Contracts.Ai;

namespace CreativeFlow.Api.Http;

/// <summary>观察三个画布生成入口的业务参数和结果，不改变既有生成和异常处理流程。</summary>
public sealed class GenerationHistoryFilter(IRequestHistoryRepository history, ILogger<GenerationHistoryFilter> logger) : IAsyncActionFilter
{
    /// <summary>写入开始和完成记录；失败、取消及异步视频接受分别记录。</summary>
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var http = context.HttpContext;
        if (http.GetDeviceId() is not { } device || http.GetAccountId() is not { } account
            || !context.ActionArguments.TryGetValue("request", out var request) || Snapshot(request) is not { } snapshot)
        {
            await next();
            return;
        }
        using var document = JsonDocument.Parse(snapshot.Json);
        var input = document.RootElement;
        // 在调用业务前固定归属和输入，保证失败请求也能追溯到最初的画布节点。
        var id = Guid.CreateVersion7();
        var started = await TryWriteAsync(token => history.StartAsync(new(id, device.Value, account.Value,
            Identifier(input, "canvasId"), String(input, "nodeId"), snapshot.Kind, Identifier(input, "providerId"),
            String(input, "modelKey") ?? string.Empty, snapshot.Json, DateTimeOffset.UtcNow), token));
        ActionExecutedContext executed;
        try { executed = await next(); }
        catch (Exception error)
        {
            if (started) await FinishAsync(id, null, error, http.RequestAborted.IsCancellationRequested);
            throw;
        }
        if (started) await FinishAsync(id, executed.Result, executed.Exception, http.RequestAborted.IsCancellationRequested);
    }

    /// <summary>限定记录为生成 DTO；账号、请求头和渠道密钥不进入快照。</summary>
    private static (string Kind, string Json)? Snapshot(object? request) => request switch
    {
        GenerateTextRequest text => ("text", JsonSerializer.Serialize(text, ApiJsonContext.Default.GenerateTextRequest)),
        GenerateImageRequest image => ("image", JsonSerializer.Serialize(image, ApiJsonContext.Default.GenerateImageRequest)),
        StartVideoRequest video => ("video", JsonSerializer.Serialize(video, ApiJsonContext.Default.StartVideoRequest)),
        _ => null,
    };

    /// <summary>提取可选字符串，不影响原有模型绑定和参数校验。</summary>
    private static string? String(JsonElement input, string key) =>
        input.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>只有有效非零 UUID 可进入记录关联。</summary>
    private static Guid? Identifier(JsonElement input, string key) =>
        Guid.TryParse(String(input, key), out var id) && id != Guid.Empty ? id : null;

    /// <summary>保存已生成产物优先于客户端断开信号；视频终态继续取已有任务行。</summary>
    private Task<bool> FinishAsync(Guid id, IActionResult? result, Exception? error, bool cancelled)
    {
        var value = (result as ObjectResult)?.Value;
        var output = value switch
        {
            GenerateTextResponse text => JsonSerializer.Serialize(text, ApiJsonContext.Default.GenerateTextResponse),
            GenerateImageResponse image => JsonSerializer.Serialize(image, ApiJsonContext.Default.GenerateImageResponse),
            _ => null,
        };
        Guid? job = value is StartVideoResponse video && Guid.TryParse(video.JobId, out var parsed) ? parsed : null;
        var status = output is not null ? "succeeded" : job is not null ? "running"
            : cancelled || error is OperationCanceledException ? "cancelled" : "failed";
        var message = value is ErrorResponse failure ? string.Join("；", failure.Details is { Length: > 0 } ? failure.Details : [failure.Error])
            : status == "failed" ? "生成请求未完成，请结合运行日志检查。" : null;
        return TryWriteAsync(token => history.FinishAsync(id, status, output, message, job, token));
    }

    /// <summary>记录存储故障不覆盖原请求结果；仅记录请求记录系统自身错误，不打印业务正文。</summary>
    private async Task<bool> TryWriteAsync(Func<CancellationToken, Task> write)
    {
        // 取消仍需写终态，但故障存储最多延迟三秒，不能无限阻塞原生成请求。
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await write(timeout.Token); return true; }
        catch (Exception error)
        {
            logger.LogWarning("Request history could not be written ({ErrorType}).", error.GetType().Name);
            return false;
        }
    }
}
