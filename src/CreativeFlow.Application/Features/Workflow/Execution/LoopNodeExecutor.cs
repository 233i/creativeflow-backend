using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Ai;

namespace CreativeFlow.Application.Features.Workflow;

/// <summary>按声明式子图逐项执行循环或批处理，保留结果顺序并显式记录失败项。</summary>
public sealed class LoopNodeExecutor(
    WorkflowNestedRunner nested,
    IWorkflowNodeProgress progress) : GeneralWorkflowNodeExecutor
{
    protected override async Task<JsonObject> ExecuteBoundAsync(
        WorkflowNodeExecutionContext context,
        JsonObject bound,
        CancellationToken ct)
    {
        var items = bound["items"] as JsonArray
            ?? throw new WorkflowV2Exception("循环输入 items 必须是数组。");
        var data = context.Node.Data;
        var maxItems = (int)WorkflowV2Values.Number(data["maxItems"])!.Value;
        if (items.Count > maxItems) throw new WorkflowV2Exception($"循环输入最多允许 {maxItems} 项。");
        var body = (JsonObject)data["body"]!;
        var bodyValidation = WorkflowV2GraphValidator.Validate(body);
        if (!bodyValidation.IsValid || bodyValidation.Plan is null)
            throw new WorkflowV2Exception("循环子流程校验失败。");

        var isBatch = context.Node.Type == "batch";
        var batchSize = isBatch ? (int)WorkflowV2Values.Number(data["batchSize"])!.Value : 1;
        var work = isBatch
            ? Enumerable.Range(0, (items.Count + batchSize - 1) / batchSize)
                .Select(index => new WorkItem(index, new JsonArray(items.Skip(index * batchSize).Take(batchSize).Select(item => item?.DeepClone()).ToArray())))
                .ToArray()
            : Enumerable.Range(0, items.Count)
                .Select(index => new WorkItem(index, items[index]?.DeepClone() ?? null))
                .ToArray();
        var results = new JsonNode?[work.Length];
        var errors = new ConcurrentBag<(int Index, string Message)>();
        var failurePolicy = WorkflowV2Values.Text(data["failurePolicy"]);
        var parallelism = (int)WorkflowV2Values.Number(data["parallelism"])!.Value;
        using var failFast = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var completed = 0;
        var progressLock = new SemaphoreSlim(1, 1);
        try
        {
            await Parallel.ForEachAsync(work, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = failFast.Token }, async (item, token) =>
            {
                try
                {
                    var input = new JsonObject
                    {
                        [isBatch ? "items" : "item"] = item.Value?.DeepClone(),
                        ["index"] = item.Index,
                    };
                    var output = await nested.ExecuteAsync(context, bodyValidation.Plan, input, token);
                    results[item.Index] = output;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested && failurePolicy == "continue")
                {
                    errors.Add((item.Index, "子流程被取消"));
                }
                catch (Exception exception) when (failurePolicy == "continue")
                {
                    errors.Add((item.Index, exception is WorkflowV2Exception ? exception.Message : "子流程执行失败"));
                }
                catch
                {
                    failFast.Cancel();
                    throw;
                }
                finally
                {
                    var done = Interlocked.Increment(ref completed);
                    await progressLock.WaitAsync(CancellationToken.None);
                    try { await progress.ReportAsync(context.Run, context.NodeRun, work.Length == 0 ? 1 : (double)done / work.Length, CancellationToken.None); }
                    finally { progressLock.Release(); }
                }
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw exception is WorkflowV2Exception ? exception : new WorkflowV2Exception("循环节点执行失败。");
        }
        finally
        {
            progressLock.Dispose();
        }

        var resultArray = new JsonArray(results.Where(item => item is not null).Select(item => item?.DeepClone()).ToArray());
        var errorArray = new JsonArray(errors.OrderBy(item => item.Index).Select(item => (JsonNode?)JsonValue.Create($"第 {item.Index + 1} 项：{item.Message}")).ToArray());
        var output = new JsonObject { ["results"] = resultArray, ["errors"] = errorArray, ["count"] = items.Count };
        if (WorkflowV2Values.Bind((JsonArray)data["outputFields"]!, output, out var boundOutput, useDefaults: false) is { } error)
            throw new WorkflowV2Exception($"循环输出校验失败：{error}");
        return boundOutput;
    }

    private sealed record WorkItem(int Index, JsonNode? Value);
}
