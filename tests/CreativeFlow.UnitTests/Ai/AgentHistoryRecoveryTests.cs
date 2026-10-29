using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Features.Agent;
using CreativeFlow.Domain.Ai;
using CreativeFlow.UnitTests.Support;

namespace CreativeFlow.UnitTests.Ai;

/// <summary>失败轮次缺少工具回执时，后续模型输入闭合调用，原历史和画布不被重放或改写。</summary>
public sealed class AgentHistoryRecoveryTests
{
    /// <summary>这些用例只允许访问显式指定的隔离 PostgreSQL。</summary>
    public static bool NoDatabase => ImageJobDatabase.NoDatabase;

    /// <summary>三个模型协议均只为缺失回执补充未知结果，已有结果原样保留且不会再次执行旧工具。</summary>
    [Theory(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    [InlineData(ProviderType.OpenAiChat, AgentRunStatus.Failed)]
    [InlineData(ProviderType.OpenAiChat, AgentRunStatus.Cancelled)]
    [InlineData(ProviderType.OpenAiChat, AgentRunStatus.Interrupted)]
    [InlineData(ProviderType.OpenAiResponses, AgentRunStatus.Failed)]
    [InlineData(ProviderType.OpenAiResponses, AgentRunStatus.Cancelled)]
    [InlineData(ProviderType.OpenAiResponses, AgentRunStatus.Interrupted)]
    [InlineData(ProviderType.Anthropic, AgentRunStatus.Failed)]
    [InlineData(ProviderType.Anthropic, AgentRunStatus.Cancelled)]
    [InlineData(ProviderType.Anthropic, AgentRunStatus.Interrupted)]
    public async Task FollowingPromptClosesMissingHistoricalReceiptsWithoutReexecution(ProviderType protocol, AgentRunStatus status)
    {
        await using var f = await AgentRunFixture.CreateAsync(protocol);
        var ct = TestContext.Current.CancellationToken;
        await f.Database.SqlAsync("UPDATE agent_message SET ordinal=20");
        var old = f.Run with { Id = AgentRunId.New(), Status = status, FinishedAt = DateTimeOffset.UtcNow };
        await f.Agents.InsertRunAsync(old, ct);
        await f.Agents.InsertMessageAsync(Message(old, 1, "user", [new(AgentContentKind.Text, Text: "earlier prompt")]), ct);
        await f.Agents.InsertMessageAsync(Message(old, 2, "assistant",
            [new(AgentContentKind.ToolUse, ToolUseId: "complete", ToolName: "read_canvas", Json: "{}"),
             new(AgentContentKind.ToolUse, ToolUseId: "missing", ToolName: "create_node", Json: "{}")]), ct);
        await f.Agents.InsertMessageAsync(Message(old, 3, "user",
            [new(AgentContentKind.ToolResult, ToolUseId: "complete", Json: "{\"value\":42}")]), ct);
        var before = await f.MessagesAsync();

        await f.ExecuteAsync();

        var call = Assert.Single(f.Models.Calls);
        Assert.Equal(protocol, call.Protocol);
        var results = call.Request.Messages.SelectMany(m => m.Content).Where(b => b.Kind == AgentContentKind.ToolResult).ToArray();
        Assert.Equal(["complete", "missing"], results.Select(b => b.ToolUseId));
        Assert.Equal(42, JsonNode.Parse(results[0].Json!)!["value"]!.GetValue<int>());
        Assert.False(results[0].IsError);
        Assert.True(results[1].IsError);
        Assert.Equal("unknown", JsonNode.Parse(results[1].Json!)!["status"]!.GetValue<string>());
        Assert.Equal("previous_tool_result_unavailable", JsonNode.Parse(results[1].Json!)!["error"]!.GetValue<string>());
        Assert.Equal("unchanged prompt", Assert.Single(call.Request.Messages[^1].Content).Text);
        Assert.Empty(f.Browser.ExecutedTools);
        Assert.Equal(AgentRunStatus.Succeeded, (await f.ReadRunAsync()).Status);
        Assert.Equal(status, (await f.Agents.FindRunByIdAsync(old.Id, ct))!.Status);
        var after = await f.MessagesAsync();
        Assert.Equal(before.Count + 1, after.Count);
        Assert.Equal(before, after.Take(before.Count));
        Assert.DoesNotContain(after, m => m.ContentJson.Contains("previous_tool_result_unavailable", StringComparison.Ordinal));
    }

    /// <summary>已经配对的历史工具不生成占位结果，当前轮次的新工具由正常执行路径保存真实结果。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task CompleteHistoryAndCurrentToolCallsKeepTheirOriginalResults()
    {
        await using var f = await AgentRunFixture.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        await f.Database.SqlAsync("UPDATE agent_message SET ordinal=20");
        var old = f.Run with { Id = AgentRunId.New(), Status = AgentRunStatus.Succeeded, FinishedAt = DateTimeOffset.UtcNow };
        await f.Agents.InsertRunAsync(old, ct);
        await f.Agents.InsertMessageAsync(Message(old, 1, "assistant",
            [new(AgentContentKind.ToolUse, ToolUseId: "same-id", ToolName: "read_canvas", Json: "{}")]), ct);
        await f.Agents.InsertMessageAsync(Message(old, 2, "user",
            [new(AgentContentKind.ToolResult, ToolUseId: "same-id", Json: "{\"old\":true}")]), ct);
        f.Models.Respond = iteration => iteration == 1
            ? [new(AgentModelEventKind.ToolUse, ToolUseId: "same-id", ToolName: "read_canvas", Json: "{}"),
               new(AgentModelEventKind.Completed, StopReason: "tool_use")]
            : [new(AgentModelEventKind.TextDelta, Text: "current result"), new(AgentModelEventKind.Completed, StopReason: "stop")];

        await f.ExecuteAsync();

        Assert.Equal(["same-id"], f.Browser.ExecutedTools);
        Assert.Equal(2, f.Models.Calls.Count);
        var results = f.Models.Calls[1].Request.Messages.SelectMany(m => m.Content).Where(b => b.Kind == AgentContentKind.ToolResult).ToArray();
        Assert.Equal(2, results.Length);
        Assert.All(results, r => Assert.False(r.IsError));
        Assert.True(JsonNode.Parse(results[0].Json!)!["old"]!.GetValue<bool>());
        Assert.True(JsonNode.Parse(results[1].Json!)!["ok"]!.GetValue<bool>());
    }

    /// <summary>构造属于指定历史运行的合成消息，保持原内容序列化和作用域。</summary>
    private static AgentStoredMessage Message(AgentRun run, int ordinal, string role, AgentContentBlock[] blocks) =>
        new(Guid.NewGuid(), run.SessionId, run.Id, run.DeviceId, ordinal, role,
            AgentModelMessageConverter.SerializeBlocks(blocks), DateTimeOffset.UtcNow);
}
