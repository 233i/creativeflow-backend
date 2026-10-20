using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Features.Identity;
using CreativeFlow.Application.Features.Workflow;
using CreativeFlow.Domain.Ai;
using static CreativeFlow.Application.Features.Workflow.WorkflowExecutionErrors;

namespace CreativeFlow.Infrastructure.Ai.Workflows;

/// <summary>协调一次工作流运行的校验、复用、MAF 调度和状态推进；具体节点交给注册表。</summary>
internal sealed class WorkflowRunOrchestrator(
    IWorkflowRunRepository runs,
    BackgroundAccountScope accountScope,
    IWorkflowRuntime runtime,
    WorkflowNodeExecutorRegistry executors,
    WorkflowExecutionPolicy policy,
    WorkflowProgressPublisher progress,
    WorkflowRunFinalizer finalizer,
    ILogger<WorkflowRunWorker> logger,
    WorkflowModelCallTrace? modelTrace = null,
    WorkflowPublicTextStream? textStream = null)
{
    /// <summary>按持久化图快照执行节点，保留输出复用、取消检查与终止写入。</summary>
    internal async Task RunAsync(
        WorkflowRun run,
        CancellationToken ct)
    {
        // 执行以运行保存的图快照为准，并恢复设备的当前账号范围；已有成功节点只恢复输出，避免再次调用上游。
        await accountScope.RestoreForDeviceAsync(run.DeviceId, ct);

        JsonNode? graph;
        try
        {
            graph = JsonNode.Parse(run.GraphJson);
        }
        catch (System.Text.Json.JsonException)
        {
            await finalizer.FailRunAsync(
                run, "The stored workflow snapshot is invalid.", ct);
            return;
        }
        var isTyped = WorkflowGraphCompiler.IsTypedGraph(graph);
        if (isTyped && !policy.GeneralEnabled)
        {
            await finalizer.FailRunAsync(run, "AI 工作流已停用，本次任务没有调用模型。", ct);
            return;
        }
        var debugNodeId = isTyped && run.TriggerType == WorkflowRunTriggerType.Interactive ? WorkflowNodeDebug.Target(run.InputJson) : null;
        var validation = debugNodeId is null ? WorkflowGraphCompiler.Validate(graph) : WorkflowV2GraphValidator.Validate(graph, onlyNodeId: debugNodeId);
        if (!validation.IsValid || validation.Plan is null)
        {
            await finalizer.FailRunAsync(run, string.Join("; ", validation.Errors), ct);
            return;
        }

        await progress.PublishAsync(run, "running", null, null, null, null, null, ct);
        if (isTyped) textStream?.Configure(run, validation.Plan);
        var storedNodes = (await runs.ListNodesAsync(run.Id, ct))
            .ToDictionary(node => node.NodeId, StringComparer.Ordinal);
        var values = new Dictionary<string, WorkflowNodeValue>(StringComparer.Ordinal);
        var skipped = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            var snapshot = isTyped ? JsonNode.Parse(run.InputJson ?? "null") as JsonObject
                ?? throw new WorkflowV2Exception("运行输入快照缺失。") : new JsonObject();
            var authorization = policy.Bind(run, validation.Plan, snapshot, isTyped, debugNodeId);
            // MAF 是唯一调度器；注册表只执行当前节点，不创建自己的调度循环。
            await runtime.RunAsync(validation.Plan.Nodes.Select(node => node.Id).ToArray(), async (index, ct) =>
            {
                var planNode = validation.Plan.Nodes[index];
                await ThrowIfCancelledAsync(run.Id, ct);
                await authorization.ValidateNodeAsync(ct);
                // MAF 仍访问拓扑中的每步；未命中路径只写 skipped，不启动业务调用，也不恢复该路径的旧输出。
                if (isTyped && debugNodeId is null && planNode.Type != "end" && WorkflowBranchFlow.ShouldSkip(planNode,
                    values.ToDictionary(pair => pair.Key, pair => pair.Value.Fields!, StringComparer.Ordinal), skipped))
                {
                    skipped.Add(planNode.Id);
                    var skippedNode = new WorkflowNodeRun(run.Id, planNode.Id, planNode.Type, WorkflowNodeRunStatus.Skipped,
                        false, null, null, null, null, DateTimeOffset.UtcNow);
                    if (!await runs.UpdateNodeAsync(skippedNode, ct)) throw new WorkflowCancelledException();
                    await progress.PublishAsync(run, "running", planNode.Id, "skipped", null, null, null, ct);
                    return;
                }
                if (storedNodes.TryGetValue(planNode.Id, out var storedNode)
                    && storedNode.Status == WorkflowNodeRunStatus.Succeeded)
                {
                    WorkflowNodeValue storedValue;
                    var restored = isTyped
                        ? WorkflowNodeValue.TryRestoreV2(planNode, storedNode.OutputJson, out storedValue)
                        : WorkflowNodeValue.TryParse(storedNode.OutputJson, out storedValue);
                    if (!restored)
                    {
                        throw new WorkflowExecutionException(
                            $"The completed output for node '{planNode.Id}' could not be restored. " +
                            "Run the workflow again from the beginning.");
                    }

                    values[planNode.Id] = storedValue;
                    return;
                }
                if (storedNodes.TryGetValue(planNode.Id, out storedNode)
                    && storedNode.Status == WorkflowNodeRunStatus.Skipped)
                {
                    skipped.Add(planNode.Id);
                    return;
                }
                if (isTyped && (WorkflowModelTasks.UsesModel(planNode.Type) || planNode.Type is "knowledgeRetrieve" or "httpRequest") && storedNode?.StartedAt is not null)
                    throw new WorkflowV2Exception("模型或外部接口调用状态不明确，已阻止自动重复调用。请检查后创建新运行。");

                var started = DateTimeOffset.UtcNow;
                var retry = isTyped ? WorkflowRetryPolicy.Read(planNode.Data)
                    : new WorkflowRetrySettings(WorkflowRetryPolicy.DefaultMaxAttempts, 1000, WorkflowRetryPolicy.MaxBackoffMilliseconds, new HashSet<string>(StringComparer.Ordinal));
                var nodeRun = new WorkflowNodeRun(
                    run.Id, planNode.Id, planNode.Type, WorkflowNodeRunStatus.Running,
                    false, null, null, null, started, null)
                {
                    MaxAttempts = retry.MaxAttempts,
                };

                for (var attempt = 1; ; attempt++)
                {
                    nodeRun = nodeRun with
                    {
                        Attempt = attempt,
                        Status = WorkflowNodeRunStatus.Running,
                        Error = null,
                        RetryAt = null,
                        StartedAt = attempt == 1 ? started : DateTimeOffset.UtcNow,
                        FinishedAt = null,
                    };
                    if (!await runs.UpdateNodeAsync(nodeRun, ct)) throw new WorkflowCancelledException();
                    await runs.RecordNodeAttemptAsync(new WorkflowNodeAttempt(run.Id, planNode.Id, attempt,
                        WorkflowNodeRunStatus.Running, null, nodeRun.StartedAt, null, null), ct);
                    await progress.PublishAsync(run, "running", planNode.Id, "running", null, null, null, ct);

                    try
                    {
                        var trace = isTyped ? modelTrace : null;
                        trace?.Bind(async (record, token) =>
                        {
                            nodeRun = nodeRun with { OutputJson = new JsonObject { ["schemaVersion"] = WorkflowGraphValidator.SchemaVersion, ["fields"] = new JsonObject(), ["modelCall"] = record.DeepClone() }.ToJsonString() };
                            if (!await runs.UpdateNodeAsync(nodeRun, token)) throw new WorkflowCancelledException();
                        });
                        var context = new WorkflowNodeExecutionContext(
                            isTyped ? WorkflowNodeFamily.General : WorkflowNodeFamily.Media,
                            run, planNode, values, snapshot, skipped,
                            debugNodeId is not null
                                ? snapshot["nodeDebug"]?["inputs"] as JsonObject ?? throw new WorkflowV2Exception("调试输入快照缺失。")
                                : null,
                            nodeRun);
                        var value = await executors.ExecuteAsync(context, ct);
                        await ThrowIfCancelledAsync(run.Id, ct);
                        await authorization.ValidateNodeAsync(ct);

                        values[planNode.Id] = value;
                        var output = value.ToJson();
                        if (trace?.Current is { } modelCall) output["modelCall"] = modelCall.DeepClone();
                        nodeRun = nodeRun with
                        {
                            Status = WorkflowNodeRunStatus.Succeeded,
                            Progress = 1,
                            OutputJson = output.ToJsonString(),
                            RetryAt = null,
                            FinishedAt = DateTimeOffset.UtcNow,
                        };
                        if (!await runs.UpdateNodeAsync(nodeRun, ct)) throw new WorkflowCancelledException();
                        await runs.RecordNodeAttemptAsync(new WorkflowNodeAttempt(run.Id, planNode.Id, attempt,
                            WorkflowNodeRunStatus.Succeeded, null, nodeRun.StartedAt, nodeRun.FinishedAt, null), ct);
                        await progress.PublishAsync(run, "running", planNode.Id, "succeeded", 1, output, null, ct);
                        break;
                    }
                    catch (WorkflowCancelledException)
                    {
                        throw;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw new WorkflowCancelledException();
                    }
                    catch (Exception exception)
                    {
                        var message = UserFacingError(exception);
                        if (attempt < retry.MaxAttempts && WorkflowRetryPolicy.ShouldRetry(exception, retry))
                        {
                            var delay = WorkflowRetryPolicy.Backoff(retry, attempt);
                            var retryAt = DateTimeOffset.UtcNow + delay;
                            nodeRun = nodeRun with
                            {
                                Status = WorkflowNodeRunStatus.Retrying,
                                Error = message,
                                RetryAt = retryAt,
                                FinishedAt = null,
                            };
                            if (!await runs.UpdateNodeAsync(nodeRun, ct)) throw new WorkflowCancelledException();
                            await runs.RecordNodeAttemptAsync(new WorkflowNodeAttempt(run.Id, planNode.Id, attempt,
                                WorkflowNodeRunStatus.Retrying, message, nodeRun.StartedAt, null, retryAt), ct);
                            await progress.PublishAsync(run, "running", planNode.Id, "retrying", null, null, message, ct);
                            await Task.Delay(delay, ct);
                            continue;
                        }

                        nodeRun = nodeRun with
                        {
                            Status = WorkflowNodeRunStatus.Failed,
                            Error = message,
                            RetryAt = null,
                            FinishedAt = DateTimeOffset.UtcNow,
                        };
                        if (!await runs.UpdateNodeAsync(nodeRun, ct)) throw new WorkflowCancelledException();
                        await runs.RecordNodeAttemptAsync(new WorkflowNodeAttempt(run.Id, planNode.Id, attempt,
                            WorkflowNodeRunStatus.Failed, message, nodeRun.StartedAt, nodeRun.FinishedAt, null), ct);
                        await progress.PublishAsync(run, "running", planNode.Id, "failed", null, null, message, ct);
                        throw new WorkflowExecutionException(message, exception);
                    }
                }
            }, ct);

            await ThrowIfCancelledAsync(run.Id, ct);
            await authorization.ValidateCompletionAsync(ct);
            var now = DateTimeOffset.UtcNow;
            var succeeded = run with
            {
                Status = WorkflowRunStatus.Succeeded,
                UpdatedAt = now,
                FinishedAt = now,
            };
            if (!await runs.UpdateAsync(succeeded, ct))
            {
                throw new WorkflowCancelledException();
            }
            await progress.PublishAsync(
                succeeded, "succeeded", null, null, 1, null, null, ct);
            logger.LogInformation("Workflow run {RunId} completed.", run.Id);
        }
        catch (WorkflowCancelledException)
        {
            await progress.PublishAsync(
                run, "cancelled", null, null, null, null, null,
                CancellationToken.None);
        }
        catch (WorkflowV2Exception exception)
        {
            await finalizer.FailRunAsync(run, exception.Message, ct);
        }
        catch (WorkflowHttpException exception)
        {
            await finalizer.FailRunAsync(run, UserFacingError(exception), ct);
        }
        catch (WorkflowExecutionException exception)
        {
            if (await runs.IsCancellationRequestedAsync(run.Id, ct))
            {
                await progress.PublishAsync(
                    run, "cancelled", null, null, null, null, null, ct);
            }
            else
            {
                await finalizer.FailRunAsync(run, exception.Message, ct);
            }
        }
    }

    /// <summary>在继续执行或持久化前检查工作流取消状态。</summary>
    private async Task ThrowIfCancelledAsync(
        WorkflowRunId runId,
        CancellationToken ct)
    {
        if (await runs.IsCancellationRequestedAsync(runId, ct))
        {
            throw new WorkflowCancelledException();
        }
    }
}
