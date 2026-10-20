using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Features.Workflow;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Infrastructure.Ai;

/// <summary>每次节点执行的模型调用收据；先持久化请求尝试，再发网络请求，进程中断后仍能识别未知状态。</summary>
internal sealed class WorkflowModelCallTrace
{
    /// <summary>由现有运行存储提供的写入函数，不维护另一套执行队列。</summary>
    private Func<JsonObject, CancellationToken, Task>? persist;

    /// <summary>当前节点的调用记录，不含密钥、原始输入或隐藏推理。</summary>
    public JsonObject? Current { get; private set; }

    /// <summary>每个 MAF 回调绑定新的节点存储，防止上个节点的记录泄漏到下个节点。</summary>
    public void Bind(Func<JsonObject, CancellationToken, Task> sink) { persist = sink; Current = null; }

    /// <summary>网络调用前保存实际目标和请求参数；写入失败时禁止发送。</summary>
    public async Task BeginAsync(WorkflowPlanNode node, AiProvider provider, AiModel model, CancellationToken ct)
    {
        Current = new JsonObject
        {
            ["id"] = Guid.NewGuid().ToString(), ["nodeId"] = node.Id,
            ["modelId"] = model.Id.ToString(), ["modelKey"] = model.ModelKey, ["modelName"] = model.Name,
            ["providerId"] = provider.Id.ToString(), ["providerName"] = provider.Name,
            ["protocol"] = provider.ResolveProtocol(model).ToWire(),
            ["status"] = "requesting", ["startedAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["outputMode"] = WorkflowModelTasks.IsStructured(node) ? "json" : "text",
            ["transport"] = WorkflowModelTasks.IsStructured(node) ? WorkflowV2Values.Text(node.Data["outputTransport"]) is "json_schema" ? "json_schema" : "prompt" : "text",
            ["maxOutputTokens"] = node.Data["maxOutputTokens"]?.DeepClone(), ["temperature"] = node.Data["temperature"]?.DeepClone(),
        };
        if (persist is not null) await persist(Current, ct);
    }

    /// <summary>保存真实结束时间及上游用量；网络或取消不明时使用 unknown，不伪造失败退款或零用量。</summary>
    public async Task FinishAsync(string status, TextGenerationResult? result, CancellationToken ct)
    {
        if (Current is null) return;
        Current["status"] = status;
        var finished = DateTimeOffset.UtcNow;
        Current["finishedAt"] = finished.ToString("O");
        Current["durationMs"] = Math.Max(0, (long)(finished - DateTimeOffset.Parse(Current["startedAt"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture)).TotalMilliseconds);
        if (result is not null)
        {
            Current["httpStatus"] = result.ResponseStatusCode ?? result.StatusCode; Current["usage"] = result.Usage?.DeepClone();
            Current["upstreamRequestId"] = result.UpstreamRequestId; Current["finishReason"] = result.FinishReason;
        }
        if (persist is not null) await persist(Current, ct);
    }

    /// <summary>重排序请求发送前记录候选数量和模型身份，不持久化供应商密钥或重复正文。</summary>
    public async Task BeginRerankAsync(string nodeId, AiProvider provider, AiModel model, int candidates, CancellationToken ct)
    {
        Current = new JsonObject
        {
            ["id"] = Guid.NewGuid().ToString(), ["nodeId"] = nodeId, ["modelId"] = model.Id.ToString(),
            ["modelKey"] = model.ModelKey, ["modelName"] = model.Name, ["providerId"] = provider.Id.ToString(),
            ["providerName"] = provider.Name, ["protocol"] = "rerank", ["outputMode"] = "rerank",
            ["status"] = "requesting", ["startedAt"] = DateTimeOffset.UtcNow.ToString("O"), ["candidateCount"] = candidates,
        };
        if (persist is not null) await persist(Current, ct);
    }

    /// <summary>保存重排序实际 HTTP 结果和已报告用量；超时与连接中断标记待核对。</summary>
    public async Task FinishRerankAsync(RerankResult result, CancellationToken ct)
    {
        if (Current is null) return;
        Current["httpStatus"] = result.StatusCode; Current["errorCode"] = result.Code; Current["error"] = result.Error;
        if (result.TotalTokens is { } tokens) Current["usage"] = new JsonObject { ["total_tokens"] = tokens };
        await FinishAsync(result.Ok ? "completed" : result.Code is "timeout_unknown" or "connection_unknown" ? "unknown" : "failed", null, ct);
    }
}
