using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Features.Agent;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;
using CreativeFlow.UnitTests.Support;

namespace CreativeFlow.UnitTests.Ai;

/// <summary>冻结迁移前 Agent 的持久化、消息、审批与循环行为；这些期望不能为适应 MAF 而修改。</summary>
public sealed class AgentRunCharacterizationTests
{
    /// <summary>缺少显式测试库时报告跳过，而不是假装执行了 SQL 验证。</summary>
    public static bool NoDatabase => ImageJobDatabase.NoDatabase;

    /// <summary>三个模型协议沿用原参数、系统提示词、文本顺序和成功终态。</summary>
    [Theory(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    [InlineData(ProviderType.OpenAiChat)]
    [InlineData(ProviderType.OpenAiResponses)]
    [InlineData(ProviderType.Anthropic)]
    public async Task CompletedTurnPreservesProtocolPromptAndEvents(ProviderType protocol)
    {
        await using var fixture = await AgentRunFixture.CreateAsync(protocol);
        await fixture.ExecuteAsync();
        var call = Assert.Single(fixture.Models.Calls);
        Assert.Equal(protocol, call.Protocol);
        Assert.Equal(AgentSystemPrompt.Text, call.Request.SystemPrompt);
        Assert.Equal("unchanged prompt", Assert.Single(Assert.Single(call.Request.Messages).Content).Text);
        Assert.Equal(16000, call.Request.MaxOutputTokens);
        Assert.True(call.Request.SupportsThinking);
        Assert.Equal(new AccountId(fixture.Database.Account), fixture.Context.AccountId);
        Assert.Equal(["run.started", "text.delta", "run.succeeded"], fixture.Browser.Events.Select(item => item.Type));
        Assert.Equal([1L, 0L, 2L], fixture.Browser.Events.Select(item => item.Sequence));
        Assert.Equal("{\"text\":\"done\",\"stopReason\":\"stop\"}", fixture.Browser.Events[^1].Json);
        var run = await fixture.ReadRunAsync();
        Assert.Equal(AgentRunStatus.Succeeded, run.Status);
        Assert.Equal(0, run.Iteration);
        Assert.Equal(3, run.InputTokens);
        Assert.Equal(5, run.OutputTokens);
        Assert.Equal(["user", "assistant"], (await fixture.MessagesAsync()).Select(message => message.Role));
        await fixture.ExecuteAsync();
        Assert.Single(fixture.Models.Calls);
    }

    /// <summary>锁定输出 token 的有效范围和非法配置回退，不沿用 MAF 的默认 token 上限。</summary>
    [Theory(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    [InlineData(12345, 12345)]
    [InlineData(64000, 64000)]
    [InlineData(64001, 16000)]
    public async Task OutputTokenPolicyIsUnchanged(int configured, int expected)
    {
        await using var fixture = await AgentRunFixture.CreateAsync(maxOutputTokens: configured);
        await fixture.ExecuteAsync();
        Assert.Equal(expected, Assert.Single(fixture.Models.Calls).Request.MaxOutputTokens);
    }

    /// <summary>多个工具保持串行、工具历史仍为 user 角色，ProviderItemId 和用量不能丢失。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task MultipleToolsPreserveOrderHistoryAndUsage()
    {
        await using var fixture = await AgentRunFixture.CreateAsync();
        fixture.Models.Respond = call => call == 1
            ? [new(AgentModelEventKind.ThinkingDelta, Text: "think"),
               Tool("tool-1", "read_canvas", "fc_1"), Tool("tool-2", "get_node_status", "fc_2"),
               new(AgentModelEventKind.Completed, StopReason: "tool_calls", InputTokens: 11, OutputTokens: 7)]
            : [new(AgentModelEventKind.TextDelta, Text: "finished"), new(AgentModelEventKind.Completed, StopReason: "stop", InputTokens: 13, OutputTokens: 8)];
        await fixture.ExecuteAsync();
        Assert.Equal(["tool-1", "tool-2"], fixture.Browser.ExecutedTools);
        Assert.Empty(fixture.Browser.Approvals);
        var history = fixture.Models.Calls[1].Request.Messages;
        Assert.Equal([AgentMessageRole.User, AgentMessageRole.Assistant, AgentMessageRole.User, AgentMessageRole.User], history.Select(message => message.Role));
        Assert.Equal(["fc_1", "fc_2"], history[1].Content.Where(block => block.Kind == AgentContentKind.ToolUse).Select(block => block.ProviderItemId));
        Assert.Equal(["tool-1", "tool-2"], history.Skip(2).Select(message => Assert.Single(message.Content).ToolUseId));
        var run = await fixture.ReadRunAsync();
        Assert.Equal(AgentRunStatus.Succeeded, run.Status);
        Assert.Equal(24, run.InputTokens);
        Assert.Equal(15, run.OutputTokens);
        Assert.Equal(1, run.Iteration);
    }

    /// <summary>审批拒绝不执行工具但仍按旧逻辑向模型回传错误结果；批准后才允许执行。</summary>
    [Theory(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DestructiveToolsRequireApprovalBeforeExecution(bool approved)
    {
        await using var fixture = await AgentRunFixture.CreateAsync();
        fixture.Browser.Approve = approved;
        fixture.Models.Respond = call => call == 1
            ? [Tool("delete-1", "delete_nodes"), new(AgentModelEventKind.Completed, StopReason: "tool_calls")]
            : [new(AgentModelEventKind.Completed, StopReason: "stop")];
        await fixture.ExecuteAsync();
        Assert.Equal(["approval:delete-1"], fixture.Browser.Approvals);
        Assert.Equal(approved ? ["delete-1"] : Array.Empty<string>(), fixture.Browser.ExecutedTools);
        var result = Assert.Single(fixture.Models.Calls[1].Request.Messages[^1].Content);
        Assert.Equal(!approved, result.IsError);
        if (!approved) Assert.Equal("denied_by_user", (string?)JsonNode.Parse(result.Json!)?["status"]);
        Assert.Equal(AgentRunStatus.Succeeded, (await fixture.ReadRunAsync()).Status);
    }

    /// <summary>拒绝、截断和显式失败不自动重试，已收到的文本仍按旧顺序保存。</summary>
    [Theory(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    [InlineData("refusal", "The model declined this request.")]
    [InlineData("max_tokens", "The model reached its output limit before finishing.")]
    [InlineData("failed", "upstream unknown")]
    public async Task TerminalModelErrorsDoNotRetry(string reason, string expectedError)
    {
        await using var fixture = await AgentRunFixture.CreateAsync();
        fixture.Models.Respond = _ => [new(AgentModelEventKind.TextDelta, Text: "partial"), reason == "failed"
            ? new(AgentModelEventKind.Failed, Error: expectedError)
            : new(AgentModelEventKind.Completed, StopReason: reason)];
        await fixture.ExecuteAsync();
        Assert.Single(fixture.Models.Calls);
        var run = await fixture.ReadRunAsync();
        Assert.Equal(AgentRunStatus.Failed, run.Status);
        Assert.Equal(expectedError, run.Error);
        Assert.Equal("assistant", (await fixture.MessagesAsync())[^1].Role);
        Assert.Equal("run.failed", fixture.Browser.Events[^1].Type);
    }

    /// <summary>迁移前最多发起 20 次模型请求，第 20 轮仍要求工具时失败且不再执行该轮工具。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task TwentyToolRoundsNeverMakeTwentyFirstModelRequest()
    {
        await using var fixture = await AgentRunFixture.CreateAsync();
        fixture.Models.Respond = call => [Tool($"read-{call}", "read_canvas"), new(AgentModelEventKind.Completed, StopReason: "tool_calls")];
        await fixture.ExecuteAsync();
        Assert.Equal(20, fixture.Models.Calls.Count);
        Assert.Equal(19, fixture.Browser.ExecutedTools.Count);
        var run = await fixture.ReadRunAsync();
        Assert.Equal(19, run.Iteration);
        Assert.Equal(AgentRunStatus.Failed, run.Status);
        Assert.Equal("The Agent exceeded its 20-iteration tool limit.", run.Error);
        Assert.NotNull(run.FinishedAt);
        Assert.Equal("run.failed", fixture.Browser.Events[^1].Type);
        Assert.DoesNotContain(fixture.Browser.Events, item => item.Type == "run.succeeded");
    }

    /// <summary>工具回执与取消发生竞争时，不能覆盖已取消状态或继续发起模型请求。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task CancelledRunDoesNotContinueAfterLateToolResult()
    {
        await using var fixture = await AgentRunFixture.CreateAsync();
        fixture.Models.Respond = _ => [Tool("late-1", "read_canvas"), new(AgentModelEventKind.Completed, StopReason: "tool_calls")];
        fixture.Browser.BeforeToolResult = async () =>
        {
            var run = await fixture.ReadRunAsync();
            await fixture.Agents.UpdateRunAsync(run with { Status = AgentRunStatus.Cancelled }, TestContext.Current.CancellationToken);
        };
        await fixture.ExecuteAsync();
        Assert.Single(fixture.Models.Calls);
        Assert.Equal(AgentRunStatus.Cancelled, (await fixture.ReadRunAsync()).Status);
        Assert.DoesNotContain(fixture.Browser.Events, item => item.Type == "run.succeeded");
    }

    /// <summary>解密失败在模型调用前终止，不把密钥异常交给框架回退。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task InvalidStoredKeyDoesNotContactModel()
    {
        await using var fixture = await AgentRunFixture.CreateAsync();
        fixture.Protector.Fail = true;
        await fixture.ExecuteAsync();
        Assert.Empty(fixture.Models.Calls);
        Assert.Equal(AgentRunStatus.Failed, (await fixture.ReadRunAsync()).Status);
    }

    /// <summary>生成具有稳定 ID 的合成工具事件，避免随机值影响对照。</summary>
    private static AgentModelEvent Tool(string id, string name, string? providerItemId = null) =>
        new(AgentModelEventKind.ToolUse, ToolUseId: id, ToolName: name, Json: "{}", ProviderItemId: providerItemId);
}
