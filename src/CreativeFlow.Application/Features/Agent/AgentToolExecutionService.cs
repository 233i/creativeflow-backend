using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Abstractions.Realtime;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Application.Features.Agent;

/// <summary>串行执行单轮 MCP、Skill 和画布工具，拥有审批等待及工具记录边界，不推进模型轮次。</summary>
public sealed class AgentToolExecutionService(
    IAgentRepository agents,
    AgentToolDiscovery discovery,
    AgentToolApprovalPolicy approvalPolicy,
    AgentSkillTools skillTools,
    IMcpRuntime mcpRuntime,
    IAgentCanvasBridge canvasBridge,
    AgentModelMessageConverter messageConverter,
    AgentRunEventRecorder events)
{
    /// <summary>为当前运行准备一次工具目录，后续轮次复用绑定而不重复发现。</summary>
    internal Task<AgentToolSet> PrepareAsync(AgentRun run, CancellationToken ct) =>
        discovery.PrepareAsync(run, ct);

    /// <summary>按模型内容块顺序逐一调用工具，取消时返回空值阻止下一轮，绝不自动重试。</summary>
    internal async Task<AgentRun?> ExecuteTurnAsync(
        AgentRun run,
        AgentToolSet toolSet,
        IReadOnlyList<AgentContentBlock> toolUses,
        List<AgentModelMessage> messages,
        int iteration,
        CancellationToken ct)
    {
        var enabledSkills = toolSet.EnabledSkills;
        var mcpBindings = toolSet.McpBindings;
        foreach (var use in toolUses)
        {
            var toolUseId = use.ToolUseId ?? $"tool-{iteration}-{Guid.CreateVersion7():N}";
            var toolName = use.ToolName ?? string.Empty;
            var descriptor = AgentToolCatalog.Find(toolName);
            mcpBindings.TryGetValue(toolName, out var mcpBinding);
            var startedAt = DateTimeOffset.UtcNow;
            await agents.InsertToolCallAsync(
                new AgentStoredToolCall(
                    Guid.CreateVersion7(), run.Id, run.DeviceId, toolUseId, toolName,
                    mcpBinding is not null || descriptor?.Site == AgentToolSite.Server
                        ? "server"
                        : "canvas",
                    "running", use.Json ?? "{}", null, null, startedAt, null),
                ct);

            // 助手轮次会在执行开始前持久化，但浏览器不应等到缓慢的画布工具完成后才能渲染调用。
            // 此可重放事件为实时界面提供与运行器即将执行内容相同的有序边界。
            var toolSite = mcpBinding is not null || descriptor?.Site == AgentToolSite.Server
                ? "server"
                : "canvas";
            var startedPayload = new JsonObject
            {
                ["toolUseId"] = toolUseId,
                ["toolName"] = toolName,
                ["site"] = toolSite,
                ["input"] = AgentMessageJson.ToNode(use.Json ?? "{}"),
            }.ToJsonString();
            await events.RecordAsync(
                run, "tool.started", startedPayload, text: null, error: null, ct);
            AgentCanvasToolResult result;

            if (mcpBinding is not null)
            {
                var current = await agents.FindRunByIdAsync(run.Id, ct);
                if (current is null || current.Status == AgentRunStatus.Cancelled) return null;
                var approvalId = $"approval:{toolUseId}";
                run = current with
                {
                    Status = AgentRunStatus.AwaitingApproval,
                    Iteration = iteration + 1,
                    UpdatedAt = DateTimeOffset.UtcNow,
                };
                await agents.UpdateRunAsync(run, ct);
                await events.RecordAsync(
                    run, "run.awaiting_approval",
                    new JsonObject
                    {
                        ["approvalId"] = approvalId,
                        ["toolName"] = toolName,
                    }.ToJsonString(), text: null, error: null, ct);
                var approved = await canvasBridge.RequestApprovalAsync(
                    run,
                    approvalId,
                    toolName,
                    approvalPolicy.McpSummary(mcpBinding),
                    use.Json ?? "{}",
                    TimeSpan.FromMinutes(5),
                    ct);
                if (!approved)
                {
                    result = new AgentCanvasToolResult(
                        "{\"status\":\"denied_by_user\"}", IsError: true);
                }
                else
                {
                    try
                    {
                        var called = await mcpRuntime.CallToolAsync(
                            mcpBinding.Server,
                            mcpBinding.OriginalName,
                            use.Json ?? "{}",
                            ct);
                        result = new AgentCanvasToolResult(called.Json, called.IsError);
                    }
                    catch (Exception exception) when (exception is InvalidOperationException
                        or NotSupportedException or HttpRequestException or TaskCanceledException)
                    {
                        var error = exception.Message.Length <= 400
                            ? exception.Message
                            : exception.Message[..400] + "…";
                        result = new AgentCanvasToolResult(
                            new JsonObject
                            {
                                ["status"] = "error",
                                ["error"] = error,
                            }.ToJsonString(), IsError: true);
                    }
                }
            }
            else if (descriptor is null)
            {
                result = new AgentCanvasToolResult(
                    "{\"status\":\"error\",\"error\":\"unknown_agent_tool\"}",
                    IsError: true);
            }
            else if (descriptor.Name == "load_skill")
            {
                result = await skillTools.LoadSkillAsync(
                    run.DeviceId, enabledSkills, use.Json ?? "{}", ct);
            }
            else if (descriptor.Name == "run_skill_script")
            {
                var approvalId = $"approval:{toolUseId}";
                var parsed = skillTools.ParseSkillScriptInput(enabledSkills, use.Json ?? "{}");
                if (parsed.Error is not null)
                {
                    result = AgentSkillTools.SkillError(parsed.Error);
                }
                else
                {
                    var current = await agents.FindRunByIdAsync(run.Id, ct);
                    if (current is null || current.Status == AgentRunStatus.Cancelled) return null;
                    run = current with
                    {
                        Status = AgentRunStatus.AwaitingApproval,
                        Iteration = iteration + 1,
                        UpdatedAt = DateTimeOffset.UtcNow,
                    };
                    await agents.UpdateRunAsync(run, ct);
                    await events.RecordAsync(
                        run, "run.awaiting_approval",
                        new JsonObject
                        {
                            ["approvalId"] = approvalId,
                            ["toolName"] = toolName,
                        }.ToJsonString(), text: null, error: null, ct);
                    var approved = await canvasBridge.RequestApprovalAsync(
                        run,
                        approvalId,
                        toolName,
                        approvalPolicy.ScriptSummary(parsed),
                        use.Json ?? "{}",
                        TimeSpan.FromMinutes(5),
                        ct);
                    result = approved
                        ? await skillTools.RunSkillScriptAsync(run.DeviceId, parsed, ct)
                        : new AgentCanvasToolResult(
                            "{\"status\":\"denied_by_user\"}", IsError: true);
                }
            }
            else if (descriptor.Site != AgentToolSite.Canvas)
            {
                result = new AgentCanvasToolResult(
                    "{\"status\":\"error\",\"error\":\"server_tool_not_implemented\"}",
                    IsError: true);
            }
            else
            {
                var current = await agents.FindRunByIdAsync(run.Id, ct);
                if (current is null || current.Status == AgentRunStatus.Cancelled) return null;

                if (approvalPolicy.CanvasSummary(descriptor, use.Json ?? "{}") is { } approvalSummary)
                {
                    var approvalId = $"approval:{toolUseId}";
                    run = current with
                    {
                        Status = AgentRunStatus.AwaitingApproval,
                        Iteration = iteration + 1,
                        UpdatedAt = DateTimeOffset.UtcNow,
                    };
                    await agents.UpdateRunAsync(run, ct);
                    var approvalPayload = new JsonObject
                    {
                        ["approvalId"] = approvalId,
                        ["toolName"] = toolName,
                    }.ToJsonString();
                    await events.RecordAsync(
                        run, "run.awaiting_approval", approvalPayload,
                        text: null, error: null, ct);

                    var approved = await canvasBridge.RequestApprovalAsync(
                        run,
                        approvalId,
                        toolName,
                        approvalSummary,
                        use.Json ?? "{}",
                        TimeSpan.FromMinutes(5),
                        ct);
                    if (!approved)
                    {
                        result = new AgentCanvasToolResult(
                            "{\"status\":\"denied_by_user\"}", IsError: true);
                    }
                    else
                    {
                        current = await agents.FindRunByIdAsync(run.Id, ct);
                        if (current is null || current.Status == AgentRunStatus.Cancelled) return null;
                        result = await ExecuteCanvasAsync(
                            current, descriptor, toolUseId, toolName, use.Json ?? "{}",
                            iteration, ct);
                        run = current;
                    }
                }
                else
                {
                    result = await ExecuteCanvasAsync(
                        current, descriptor, toolUseId, toolName, use.Json ?? "{}",
                        iteration, ct);
                    run = current;
                }
            }

            var completedAt = DateTimeOffset.UtcNow;
            await agents.CompleteToolCallAsync(
                run.DeviceId,
                run.Id,
                toolUseId,
                CompletionStatus(result),
                result.Json,
                result.IsError ? "Agent tool failed." : null,
                completedAt,
                ct);

            var resultBlock = new AgentContentBlock(
                AgentContentKind.ToolResult,
                ToolUseId: toolUseId,
                Json: result.Json,
                IsError: result.IsError);
            await messageConverter.AppendAsync(run, "user", [resultBlock], ct);
            messages.Add(new AgentModelMessage(AgentMessageRole.User, [resultBlock]));

            var eventPayload = new JsonObject
            {
                ["toolUseId"] = toolUseId,
                ["toolName"] = toolName,
                ["isError"] = result.IsError,
                ["result"] = AgentMessageJson.ToNode(result.Json),
            }.ToJsonString();
            await events.RecordAsync(
                run, "tool.completed", eventPayload, text: null,
                error: result.IsError ? "Agent tool failed." : null, ct);
        }
        return run;
    }

    /// <summary>将工具结果映射为成功、拒绝、超时或失败状态。</summary>
    private static string CompletionStatus(AgentCanvasToolResult result)
    {
        if (!result.IsError) return "succeeded";
        if (result.Json.Contains("denied_by_user", StringComparison.Ordinal)) return "denied";
        if (result.Json.Contains("timeout", StringComparison.Ordinal)) return "timeout";
        return "failed";
    }

    /// <summary>通过既有浏览器桥接器执行画布工具并等待结果。</summary>
    private async Task<AgentCanvasToolResult> ExecuteCanvasAsync(
        AgentRun current,
        AgentToolDescriptor descriptor,
        string toolUseId,
        string toolName,
        string inputJson,
        int iteration,
        CancellationToken ct)
    {
        var active = current with
        {
            Status = current.ExecutorConnectionId is null
                ? AgentRunStatus.AwaitingCanvas
                : AgentRunStatus.Running,
            Iteration = iteration + 1,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await agents.UpdateRunAsync(active, ct);
        if (active.Status == AgentRunStatus.AwaitingCanvas)
        {
            await events.RecordAsync(
                active, "run.awaiting_canvas", "{}", text: null, error: null, ct);
        }

        return await canvasBridge.ExecuteAsync(
            active,
            toolUseId,
            toolName,
            inputJson,
            TimeSpan.FromMilliseconds(descriptor.TimeoutMilliseconds),
            ct);
    }

}
