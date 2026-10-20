using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Domain.Ai;
using CreativeFlow.UnitTests.Support;

namespace CreativeFlow.UnitTests.Ai;

/// <summary>在拆分前锁定工具调用、审批、持久消息和通知顺序，重构后复用同一组断言。</summary>
public sealed class AgentToolExecutionCharacterizationTests
{
    /// <summary>只允许显式指定的隔离 PostgreSQL，缺少时明确跳过。</summary>
    public static bool NoDatabase => ImageJobDatabase.NoDatabase;

    /// <summary>MCP 成功、拒绝和超时都只经过一次审批；异常不触发工具重试。</summary>
    [Theory(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    [InlineData("approved", "succeeded", 1)]
    [InlineData("denied", "denied", 0)]
    [InlineData("timeout", "timeout", 1)]
    public async Task McpCallsPreserveApprovalAndResultOrder(string outcome, string status, int calls)
    {
        var ports = new AgentToolPorts();
        if (outcome == "timeout") ports.Call = () => throw new TaskCanceledException("MCP timeout");
        await using var fixture = await AgentRunFixture.CreateAsync(mcpRuntime: ports);
        var server = await AddMcpAsync(fixture, "Lookup");
        fixture.Browser.Approve = outcome != "denied";
        var name = $"mcp__{server.Id.Value:N}"[..13] + "__lookup_item";
        OneTool(fixture, name, "{\"query\":\"test\"}");
        ObserveToolPersistence(fixture);
        await fixture.ExecuteAsync();
        Assert.Equal(calls, ports.Calls.Count);
        if (calls > 0)
        {
            Assert.Equal("lookup.item", ports.Calls[0].Name);
            Assert.Equal("{\"query\":\"test\"}", ports.Calls[0].Json);
        }
        Assert.Equal(["approval:tool-1"], fixture.Browser.Approvals);
        Assert.Equal(["run.started", "tool.started", "run.awaiting_approval", "tool.completed", "run.succeeded"],
            fixture.Browser.Events.Select(item => item.Type));
        Assert.Equal([1L, 2L, 3L, 4L, 5L], fixture.Browser.Events.Select(item => item.Sequence));
        Assert.Equal(1, await fixture.Database.CountAsync($"SELECT count(*) FROM agent_tool_call WHERE status='{status}' AND site='server'"));
        Assert.Equal(2, fixture.Models.Calls.Count);
        var result = Assert.Single(fixture.Models.Calls[1].Request.Messages[^1].Content);
        Assert.Equal(outcome != "approved", result.IsError);
        Assert.Equal(AgentRunStatus.Succeeded, (await fixture.ReadRunAsync()).Status);
    }

    /// <summary>发现失败独立隔离、禁用和 stdio 服务不发现，同名映射冲突保留第一项。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task DiscoveryKeepsStableNamesAndSkipsUnavailableServers()
    {
        var ports = new AgentToolPorts
        {
            Discover = server => server.Name == "Broken" ? throw new HttpRequestException("offline") :
                [new("lookup.item", "first", "{}"), new("lookup-item", "duplicate", "{}")],
        };
        await using var fixture = await AgentRunFixture.CreateAsync(mcpRuntime: ports);
        await AddMcpAsync(fixture, "Broken");
        await AddMcpAsync(fixture, "Disabled", enabled: false);
        await AddMcpAsync(fixture, "Stdio", transport: McpTransport.Stdio);
        await AddMcpAsync(fixture, "Working");
        await fixture.ExecuteAsync();
        Assert.Equal(["Broken", "Working"], ports.Discovered.Select(server => server.Name));
        var tools = Assert.Single(fixture.Models.Calls).Request.Tools;
        Assert.Single(tools, tool => tool.Name.StartsWith("mcp__", StringComparison.Ordinal));
        Assert.Contains(tools, tool => tool.Name == "read_canvas");
        Assert.DoesNotContain(tools, tool => tool.Name == "run_skill_script");
    }

    /// <summary>脚本参数校验后才审批，审批后重新检查运行器和 Skill，保留受限结果字段。</summary>
    [Theory(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    [InlineData("approved", 1)]
    [InlineData("denied", 0)]
    [InlineData("revoked", 0)]
    [InlineData("disabled", 0)]
    [InlineData("timeout", 1)]
    public async Task ScriptsExecuteOnceOnlyAfterApprovalAndRevalidation(string outcome, int calls)
    {
        // 状态变化发生在审批回执之前，不能被提前读取的 Skill 快照掩盖。
        var ports = new AgentToolPorts();
        if (outcome == "timeout") ports.RunScript = () => throw new TaskCanceledException();
        await using var fixture = await AgentRunFixture.CreateAsync(skillStore: ports, skillRunner: ports);
        var skill = await AddSkillAsync(fixture);
        fixture.Browser.Approve = outcome != "denied";
        fixture.Browser.BeforeApprovalResult = async () =>
        {
            Assert.Empty(ports.Scripts);
            Assert.Equal(0, ports.PackageReads);
            if (outcome == "revoked") await fixture.Database.SqlAsync($"UPDATE skill SET enabled=false WHERE skill_id='{skill.Id}'");
            if (outcome == "disabled") ports.Available = false;
        };
        OneTool(fixture, "run_skill_script", """{"slug":"synthetic","scriptPath":"scripts/test.py","arguments":["a","b"],"stdin":"input","timeoutSeconds":17}""");
        ObserveToolPersistence(fixture);
        await fixture.ExecuteAsync();
        Assert.Equal(calls, ports.Scripts.Count);
        Assert.Equal(["approval:tool-1"], fixture.Browser.Approvals);
        var result = Assert.Single(fixture.Models.Calls[1].Request.Messages[^1].Content);
        Assert.Equal(outcome != "approved", result.IsError);
        if (calls > 0)
        {
            var script = Assert.Single(ports.Scripts);
            Assert.Equal(["a", "b"], script.Arguments);
            Assert.Equal("input", script.Stdin);
            Assert.Equal(TimeSpan.FromSeconds(17), script.Timeout);
            Assert.Equal(1, ports.PackageReads);
        }
        if (outcome == "approved") Assert.True((bool)JsonNode.Parse(result.Json!)!["stdoutTruncated"]!);
        if (outcome == "revoked") Assert.Contains("skill_not_found_or_disabled", result.Json);
        if (outcome == "disabled") Assert.Contains("skill_script_execution_disabled", result.Json);
        if (outcome == "timeout") Assert.Contains("skill_script_timed_out", result.Json);
    }

    /// <summary>非法脚本输入作为工具错误返回，不能审批、读包或执行脚本。</summary>
    [Theory(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    [InlineData("[]", "invalid_skill_script_input")]
    [InlineData("{}", "skill_slug_and_script_required")]
    [InlineData("{\"slug\":\"missing\",\"scriptPath\":\"x.py\"}", "skill_not_found_or_disabled")]
    [InlineData("{\"slug\":\"synthetic\",\"scriptPath\":\"x.py\",\"arguments\":[1]}", "invalid_skill_script_arguments")]
    [InlineData("{\"slug\":\"synthetic\",\"scriptPath\":\"x.py\",\"timeoutSeconds\":61}", "invalid_skill_script_timeout")]
    public async Task InvalidScriptInputNeverRequestsApproval(string json, string error)
    {
        var ports = new AgentToolPorts();
        await using var fixture = await AgentRunFixture.CreateAsync(skillStore: ports, skillRunner: ports);
        await AddSkillAsync(fixture);
        OneTool(fixture, "run_skill_script", json);
        await fixture.ExecuteAsync();
        Assert.Empty(ports.Scripts);
        Assert.Empty(fixture.Browser.Approvals);
        Assert.Equal(0, ports.PackageReads);
        Assert.Contains(error, Assert.Single(fixture.Models.Calls[1].Request.Messages[^1].Content).Json);
    }

    /// <summary>非法 JSON 在旧数据库 jsonb 边界失败，不借结构重构改成自动修复或重试。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task MalformedToolJsonStillFailsBeforeAnyExecution()
    {
        var ports = new AgentToolPorts();
        await using var fixture = await AgentRunFixture.CreateAsync(skillStore: ports, skillRunner: ports);
        await AddSkillAsync(fixture);
        OneTool(fixture, "run_skill_script", "{broken");
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => fixture.ExecuteAsync());
        Assert.Empty(ports.Scripts);
        Assert.Empty(fixture.Browser.Approvals);
        Assert.Equal(0, await fixture.Database.CountAsync("SELECT count(*) FROM agent_tool_call"));
        Assert.Equal(["run.started"], fixture.Browser.Events.Select(item => item.Type));
    }

    /// <summary>Skill 说明按需加载并剥离 frontmatter；执行前禁用 Skill 会阻止正文读取。</summary>
    [Theory(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SkillReadsRetainOwnershipRecheck(bool revoked)
    {
        var ports = new AgentToolPorts();
        await using var fixture = await AgentRunFixture.CreateAsync(skillStore: ports);
        var skill = await AddSkillAsync(fixture);
        OneTool(fixture, "load_skill", "{\"slug\":\"synthetic\"}");
        if (revoked) fixture.Browser.BeforeEvent = payload => payload.Type == "tool.started"
            ? fixture.Database.SqlAsync($"UPDATE skill SET enabled=false WHERE skill_id='{skill.Id}'") : Task.CompletedTask;
        await fixture.ExecuteAsync();
        Assert.Empty(fixture.Browser.Approvals);
        var result = Assert.Single(fixture.Models.Calls[1].Request.Messages[^1].Content);
        Assert.Equal(revoked, result.IsError);
        Assert.Equal(revoked ? 0 : 1, ports.TextReads.Count);
        if (!revoked) Assert.Equal("Read this instruction.", (string?)JsonNode.Parse(result.Json!)!["content"]);
    }

    /// <summary>未知工具只回传一次错误，不走画布或其他外部执行端。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task UnknownToolProducesOneStoredResult()
    {
        await using var fixture = await AgentRunFixture.CreateAsync();
        OneTool(fixture, "not_registered", "{}");
        ObserveToolPersistence(fixture);
        await fixture.ExecuteAsync();
        Assert.Empty(fixture.Browser.ExecutedTools);
        Assert.Empty(fixture.Browser.Approvals);
        Assert.Contains("unknown_agent_tool", Assert.Single(fixture.Models.Calls[1].Request.Messages[^1].Content).Json);
        Assert.Equal(1, await fixture.Database.CountAsync("SELECT count(*) FROM agent_tool_call"));
    }

    /// <summary>完成通知发送失败时，工具结果已经保存，宿主记录失败后不能重复执行工具。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task NotificationFailureDoesNotReplayCompletedTool()
    {
        await using var fixture = await AgentRunFixture.CreateAsync();
        OneTool(fixture, "read_canvas", "{}");
        fixture.Browser.BeforeEvent = payload => payload.Type == "tool.completed"
            ? throw new IOException("synthetic realtime failure") : Task.CompletedTask;
        var error = await Assert.ThrowsAnyAsync<Exception>(() => fixture.ExecuteAsync());
        Assert.Equal(["tool-1"], fixture.Browser.ExecutedTools);
        Assert.Single(fixture.Models.Calls);
        Assert.Equal(1, await fixture.Database.CountAsync("SELECT count(*) FROM agent_tool_call WHERE status='succeeded'"));
        Assert.Equal(3, (await fixture.MessagesAsync()).Count);
        var events = await fixture.Agents.ListEventsAsync(fixture.Run.DeviceId, fixture.Run.Id, 0, TestContext.Current.CancellationToken);
        Assert.Equal("tool.completed", events[^1].Type);
        fixture.Browser.BeforeEvent = null;
        await fixture.Runner.FailUnhandledAsync(fixture.Run.Id, error, TestContext.Current.CancellationToken);
        await fixture.ExecuteAsync();
        Assert.Single(fixture.Browser.ExecutedTools);
        Assert.Single(fixture.Models.Calls);
        Assert.Equal(AgentRunStatus.Failed, (await fixture.ReadRunAsync()).Status);
    }

    /// <summary>画布破坏性工具审批期间取消后，不调用画布也不追加成功结果。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task CanvasCancellationDuringApprovalStopsBeforeExecution()
    {
        await using var fixture = await AgentRunFixture.CreateAsync();
        OneTool(fixture, "delete_nodes", "{}");
        fixture.Browser.BeforeApprovalResult = async () => await fixture.Agents.UpdateRunAsync(
            (await fixture.ReadRunAsync()) with { Status = AgentRunStatus.Cancelled }, TestContext.Current.CancellationToken);
        await fixture.ExecuteAsync();
        Assert.Empty(fixture.Browser.ExecutedTools);
        Assert.Single(fixture.Models.Calls);
        Assert.DoesNotContain(fixture.Browser.Events, item => item.Type == "tool.completed");
        Assert.Equal(AgentRunStatus.Cancelled, (await fixture.ReadRunAsync()).Status);
    }

    /// <summary>同一轮混合工具必须逐个完成后再推进，目录只在运行开始时发现一次。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task MixedToolFamiliesKeepSingleInvocationAndMessageOrder()
    {
        var ports = new AgentToolPorts();
        await using var fixture = await AgentRunFixture.CreateAsync(mcpRuntime: ports, skillStore: ports, skillRunner: ports);
        var server = await AddMcpAsync(fixture, "Mixed");
        await AddSkillAsync(fixture);
        var mcpName = $"mcp__{server.Id.Value:N}"[..13] + "__lookup_item";
        fixture.Models.Respond = call => call == 1
            ? [new(AgentModelEventKind.ToolUse, ToolUseId: "mcp-1", ToolName: mcpName, Json: "{}"),
               new(AgentModelEventKind.ToolUse, ToolUseId: "skill-1", ToolName: "load_skill", Json: "{\"slug\":\"synthetic\"}"),
               new(AgentModelEventKind.ToolUse, ToolUseId: "script-1", ToolName: "run_skill_script", Json: "{\"slug\":\"synthetic\",\"scriptPath\":\"scripts/test.py\"}"),
               new(AgentModelEventKind.ToolUse, ToolUseId: "canvas-1", ToolName: "read_canvas", Json: "{}"),
               new(AgentModelEventKind.Completed, StopReason: "tool_calls")]
            : [new(AgentModelEventKind.Completed, StopReason: "stop")];
        fixture.Browser.BeforeEvent = async payload =>
        {
            if (payload.Type != "tool.started") return;
            // 开始下一工具前，前一个工具必须已经记录完成和对应 user 结果。
            var startedCount = fixture.Browser.Events.Count(item => item.Type == "tool.started");
            Assert.Equal(startedCount, fixture.Browser.Events.Count(item => item.Type == "tool.completed"));
            Assert.Equal(startedCount + 2, (await fixture.MessagesAsync()).Count);
        };
        await fixture.ExecuteAsync();
        Assert.Single(ports.Discovered);
        Assert.Single(ports.Calls);
        Assert.Single(ports.TextReads);
        Assert.Single(ports.Scripts);
        Assert.Equal(["canvas-1"], fixture.Browser.ExecutedTools);
        Assert.Equal(["approval:mcp-1", "approval:script-1"], fixture.Browser.Approvals);
        Assert.Equal(["mcp-1", "skill-1", "script-1", "canvas-1"],
            fixture.Models.Calls[1].Request.Messages.Skip(2).Select(message => Assert.Single(message.Content).ToolUseId));
        Assert.Equal(4, await fixture.Database.CountAsync("SELECT count(*) FROM agent_tool_call WHERE status='succeeded'"));
    }

    /// <summary>明确锁定旧限制：重复工具 ID 只去重调用行，不提供执行幂等；本次不暗改该策略。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task DuplicateToolIdKeepsExistingNonIdempotentBehavior()
    {
        await using var fixture = await AgentRunFixture.CreateAsync();
        fixture.Models.Respond = call => call == 1
            ? [new(AgentModelEventKind.ToolUse, ToolUseId: "same-id", ToolName: "read_canvas", Json: "{}"),
             new(AgentModelEventKind.ToolUse, ToolUseId: "same-id", ToolName: "read_canvas", Json: "{}"),
             new(AgentModelEventKind.Completed, StopReason: "tool_calls")]
            : [new(AgentModelEventKind.Completed, StopReason: "stop")];
        await fixture.ExecuteAsync();
        Assert.Equal(["same-id", "same-id"], fixture.Browser.ExecutedTools);
        Assert.Equal(2, fixture.Models.Calls.Count);
        Assert.Equal(2, fixture.Browser.Events.Count(item => item.Type == "tool.completed"));
        Assert.Equal(1, await fixture.Database.CountAsync("SELECT count(*) FROM agent_tool_call"));
    }

    /// <summary>无执行连接时先持久化 awaiting_canvas；重连后仍只发送一次工具。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task AwaitingCanvasReconnectKeepsEventOrder()
    {
        await using var fixture = await AgentRunFixture.CreateAsync();
        await fixture.Database.SqlAsync("UPDATE agent_run SET executor_connection_id=NULL");
        OneTool(fixture, "read_canvas", "{}");
        fixture.Browser.BeforeEvent = async payload =>
        {
            if (payload.Type != "run.awaiting_canvas") return;
            Assert.Equal(AgentRunStatus.AwaitingCanvas, (await fixture.ReadRunAsync()).Status);
            Assert.True(await fixture.Agents.ClaimExecutorConnectionAsync(fixture.Run.DeviceId, fixture.Run.Id,
                fixture.Run.CanvasId, "fixture-browser", TestContext.Current.CancellationToken));
            await fixture.Browser.Bridge.AttachAsync(fixture.Run.Id, "fixture-browser", TestContext.Current.CancellationToken);
        };
        await fixture.ExecuteAsync();
        Assert.Equal(["tool-1"], fixture.Browser.ExecutedTools);
        Assert.Equal(["run.started", "tool.started", "run.awaiting_canvas", "tool.completed", "run.succeeded"],
            fixture.Browser.Events.Select(item => item.Type));
        Assert.Equal(AgentRunStatus.Succeeded, (await fixture.ReadRunAsync()).Status);
    }

    /// <summary>构造只调用一次工具、下一轮结束的模型响应。</summary>
    private static void OneTool(AgentRunFixture fixture, string name, string json) => fixture.Models.Respond = call => call == 1
        ? [new(AgentModelEventKind.ToolUse, ToolUseId: "tool-1", ToolName: name, Json: json),
           new(AgentModelEventKind.Completed, StopReason: "tool_calls")]
        : [new(AgentModelEventKind.Completed, StopReason: "stop")];

    /// <summary>发送开始事件前必须已有调用行和助手消息；完成事件前必须已有终态和结果消息。</summary>
    private static void ObserveToolPersistence(AgentRunFixture fixture) => fixture.Browser.BeforeEvent = async payload =>
    {
        if (payload.Type == "tool.started")
        {
            Assert.Equal(1, await fixture.Database.CountAsync("SELECT count(*) FROM agent_tool_call WHERE status='running'"));
            Assert.Equal("assistant", (await fixture.MessagesAsync())[^1].Role);
        }
        if (payload.Type == "tool.completed")
        {
            Assert.Equal(0, await fixture.Database.CountAsync("SELECT count(*) FROM agent_tool_call WHERE status='running'"));
            Assert.Equal("user", (await fixture.MessagesAsync())[^1].Role);
        }
    };

    /// <summary>将合成 MCP 服务写入测试 schema，不配置可访问的真实端点。</summary>
    private static async Task<McpServer> AddMcpAsync(AgentRunFixture fixture, string name, bool enabled = true,
        McpTransport transport = McpTransport.StreamableHttp)
    {
        var now = DateTimeOffset.UtcNow;
        var server = new McpServer(McpServerId.New(), fixture.Run.DeviceId, name, null, enabled, transport,
            null, "[]", "{}", null, "https://mcp.invalid", "{}", false, now, now);
        await fixture.McpServers.InsertAsync(server, TestContext.Current.CancellationToken);
        return server;
    }

    /// <summary>只添加合成 Skill 元数据，内容由内存替身提供。</summary>
    private static async Task<Skill> AddSkillAsync(AgentRunFixture fixture)
    {
        var now = DateTimeOffset.UtcNow;
        var skill = new Skill(SkillId.New(), fixture.Run.DeviceId, "synthetic", "Synthetic skill", "Only a fixture",
            "synthetic/package", true, true, "[]", SkillRiskLevel.Safe, 3, now, now);
        Assert.True(await fixture.Skills.InsertAsync(skill, TestContext.Current.CancellationToken));
        return skill;
    }
}
