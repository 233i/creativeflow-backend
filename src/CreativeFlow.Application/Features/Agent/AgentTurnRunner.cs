using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Features.Identity;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Application.Features.Agent;

/// <summary>恢复运行范围并推进 MAF 轮次，只协调模型、工具服务及运行终态。</summary>
public sealed class AgentTurnRunner(
    IAgentRepository agents,
    BackgroundAccountScope accountScope,
    AgentModelResolver modelResolver,
    IAgentModelDispatcher models,
    AgentModelMessageConverter messageConverter,
    AgentToolExecutionService toolExecution,
    AgentRunEventRecorder events,
    IAgentRuntime runtime)
{
    /// <summary>一次 Agent 运行允许的工具轮次上限。</summary>
    private const int MaxIterations = 20;

    /// <summary>恢复账号范围并执行持久化 Agent 轮次，保持工具审批和事件时序。</summary>
    public async Task RunAsync(AgentRunId runId, CancellationToken ct)
    {
        var run = await agents.FindRunByIdAsync(runId, ct);
        if (run is null || run.Status != AgentRunStatus.Queued) return;

        // 这个新建的后台作用域未经过账户认证中间件。
        // 在解析供应商以及后续可能属于其他关联设备的画布、素材、技能和 MCP 资源前，
        // 恢复运行设备当前所属的账户。
        await accountScope.RestoreForDeviceAsync(run.DeviceId, ct);

        var (binding, error) = await modelResolver.ResolveAsync(run, ct);
        if (binding is null)
        {
            await FailAsync(run, error!, ct);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        run = run with
        {
            Status = AgentRunStatus.Running,
            StartedAt = now,
            UpdatedAt = now,
            Error = null,
        };
        await agents.UpdateRunAsync(run, ct);
        await events.RecordAsync(run, "run.started", "{}", text: null, error: null, ct);

        var messages = await messageConverter.LoadHistoryAsync(run, ct);
        var toolSet = await toolExecution.PrepareAsync(run, ct);
        var totalInputTokens = 0;
        var totalOutputTokens = 0;

        await runtime.RunAsync(MaxIterations, models, ExecuteIterationAsync, ct);

        // 业务轮次仅执行一次模型流及其原顺序工具；下一轮仅由 MAF 推进。
        async Task<bool> ExecuteIterationAsync(int iteration, IAgentModelDispatcher turnModels, CancellationToken ct)
        {
            // 先存助手消息，再判定结束条件；只有允许继续的轮次才能串行执行其工具。
            var turn = await messageConverter.StreamTurnAsync(
                run,
                turnModels,
                binding.Protocol,
                binding.CreateRequest(messages, toolSet.Definitions),
                ct);

            if (turn.InputTokens is { } inputTokens) totalInputTokens += inputTokens;
            if (turn.OutputTokens is { } outputTokens) totalOutputTokens += outputTokens;

            if (turn.Blocks.Count > 0)
            {
                await messageConverter.AppendAsync(run, "assistant", turn.Blocks, ct);
                messages.Add(new AgentModelMessage(AgentMessageRole.Assistant, turn.Blocks));
            }

            if (turn.Error is not null)
            {
                await FailAsync(run, turn.Error, ct);
                return false;
            }
            if (turn.StopReason == "refusal")
            {
                await FailAsync(run, "The model declined this request.", ct);
                return false;
            }
            if (turn.StopReason == "max_tokens")
            {
                await FailAsync(run, "The model reached its output limit before finishing.", ct);
                return false;
            }

            var toolUses = turn.Blocks.Where(block => block.Kind == AgentContentKind.ToolUse).ToList();
            if (toolUses.Count == 0)
            {
                var finished = DateTimeOffset.UtcNow;
                run = run with
                {
                    Status = AgentRunStatus.Succeeded,
                    Iteration = iteration,
                    InputTokens = totalInputTokens,
                    OutputTokens = totalOutputTokens,
                    FinishedAt = finished,
                    UpdatedAt = finished,
                };
                await agents.UpdateRunAsync(run, ct);

                var payload = new JsonObject
                {
                    ["text"] = turn.Text,
                    ["stopReason"] = turn.StopReason ?? "end_turn",
                }.ToJsonString();
                await events.RecordAsync(run, "run.succeeded", payload, turn.Text, null, ct);
                return false;
            }

            if (iteration == MaxIterations - 1)
            {
                await FailAsync(run, $"The Agent exceeded its {MaxIterations}-iteration tool limit.", ct);
                return false;
            }

            var continuedRun = await toolExecution.ExecuteTurnAsync(
                run, toolSet, toolUses, messages, iteration, ct);
            if (continuedRun is null) return false;
            run = continuedRun;

            var refreshed = await agents.FindRunByIdAsync(run.Id, ct);
            if (refreshed is null || refreshed.Status == AgentRunStatus.Cancelled) return false;
            run = refreshed with
            {
                Status = AgentRunStatus.Running,
                Iteration = iteration + 1,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            await agents.UpdateRunAsync(run, ct);
            return true;
        }
    }

    /// <summary>使用可用的宿主令牌为未处理异常保存终止状态。</summary>
    public async Task FailUnhandledAsync(AgentRunId runId, Exception exception, CancellationToken ct)
    {
        var run = await agents.FindRunByIdAsync(runId, ct);
        if (run is null || run.Status == AgentRunStatus.Cancelled) return;
        await FailAsync(run, exception is OperationCanceledException
            ? "The Agent run was cancelled."
            : "The Agent run failed unexpectedly.", ct);
    }

    /// <summary>保存 Agent 失败终态并追加原有失败事件。</summary>
    private async Task FailAsync(AgentRun run, string error, CancellationToken ct)
    {
        var finished = DateTimeOffset.UtcNow;
        var failed = run with
        {
            Status = AgentRunStatus.Failed,
            Error = error,
            FinishedAt = finished,
            UpdatedAt = finished,
        };
        await agents.UpdateRunAsync(failed, ct);
        var payload = new JsonObject { ["error"] = error }.ToJsonString();
        await events.RecordAsync(failed, "run.failed", payload, null, error, ct);
    }
}
