using System.Text.Json;
using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Infrastructure.Ai.Maf;
using CreativeFlow.UnitTests.Support;

namespace CreativeFlow.UnitTests.Ai;

/// <summary>验证独立 MAF 运行轨迹一致且状态不串用，适配不增删原始流事件或重试业务步骤。</summary>
public sealed class MafRuntimeParityTests
{
    /// <summary>数据库测试必须显式配置隔离库。</summary>
    public static bool NoDatabase => ImageJobDatabase.NoDatabase;

    /// <summary>三协议各自两次独立运行的消息、事件、参数和终态一致，每次只调用两轮模型及两项原业务工具。</summary>
    [Theory(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    [InlineData(ProviderType.OpenAiChat)]
    [InlineData(ProviderType.OpenAiResponses)]
    [InlineData(ProviderType.Anthropic)]
    public async Task IndependentMafRunsHaveIdenticalBusinessTraces(ProviderType protocol)
    {
        await using var first = await AgentRunFixture.CreateAsync(protocol);
        await using var second = await AgentRunFixture.CreateAsync(protocol);
        foreach (var fixture in new[] { first, second })
        {
            fixture.Models.Respond = call => call == 1
                ? [new(AgentModelEventKind.TextDelta, Text: "before"), new(AgentModelEventKind.ThinkingDelta, Text: "reason"),
                   new(AgentModelEventKind.ToolUse, ToolUseId: "delete-1", ToolName: "delete_nodes", Json: "{}", ProviderItemId: "provider-1"),
                   new(AgentModelEventKind.ToolUse, ToolUseId: "read-2", ToolName: "read_canvas", Json: "{}", ProviderItemId: "provider-2"),
                   new(AgentModelEventKind.Completed, StopReason: "tool_calls", InputTokens: 10, OutputTokens: 20)]
                : [new(AgentModelEventKind.TextDelta, Text: "after"), new(AgentModelEventKind.Completed, StopReason: "stop", InputTokens: 30, OutputTokens: 40)];
            await fixture.ExecuteAsync();
            Assert.Equal(2, fixture.Models.Calls.Count);
            Assert.Equal(["delete-1", "read-2"], fixture.Browser.ExecutedTools);
            Assert.Single(fixture.Browser.Approvals);
            var run = await fixture.ReadRunAsync();
            Assert.Equal(AgentRunStatus.Succeeded, run.Status);
            Assert.Equal(40, run.InputTokens);
            Assert.Equal(60, run.OutputTokens);
        }
        Assert.True(JsonNode.DeepEquals(await TraceAsync(first), await TraceAsync(second)));
    }

    /// <summary>SDK 的流不能合并或丢掉空文本、思考、工具、失败、用量及结束原因；硬预算之外不出站。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task StreamingRoundTripIsLosslessAndBudgetIsHard()
    {
        await using var fixture = await AgentRunFixture.CreateAsync();
        await fixture.ExecuteAsync();
        var request = fixture.Models.Calls[0].Request;
        var original = new AgentModelEvent[]
        {
            new(AgentModelEventKind.TextDelta), new(AgentModelEventKind.TextDelta, Text: ""),
            new(AgentModelEventKind.ThinkingDelta, Text: "思考"),
            new(AgentModelEventKind.ToolUse, ToolUseId: "call", ToolName: "read_canvas", Json: "null", ProviderItemId: "fc"),
            new(AgentModelEventKind.Completed, StopReason: "length", InputTokens: 0, OutputTokens: 123),
            new(AgentModelEventKind.Failed, Error: "unchanged upstream error"),
        };
        fixture.Models.Respond = _ => original;
        fixture.Models.Calls.Clear();
        var dispatcher = new MafModelDispatcher(fixture.Models, 1);
        var actual = await dispatcher.StreamAsync(ProviderType.OpenAiChat, request, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(original, actual);
        for (var index = 0; index < original.Length; index++) Assert.Same(original[index], actual[index]);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await dispatcher.StreamAsync(ProviderType.OpenAiChat, request, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Single(fixture.Models.Calls);
    }

    /// <summary>旧历史中的空字段、相对素材 URL 和图片字节保持原对象，不被 SDK 构造约束拒绝或重新解释。</summary>
    [Fact]
    public void HistoricalEmptyFieldsRemainReadable()
    {
        var blocks = new AgentContentBlock[]
        {
            new(AgentContentKind.Text), new(AgentContentKind.Thinking, Text: ""),
            new(AgentContentKind.ToolUse, ToolUseId: "", ToolName: "", Json: "null"),
            new(AgentContentKind.ToolResult, ToolUseId: "", Json: "{}", IsError: true),
            new(AgentContentKind.Image, ImageUrl: "/api/v1/assets/image", MediaType: "", ImageBytes: [1, 2, 3]),
        };
        var message = new AgentModelMessage(AgentMessageRole.User, blocks);
        Assert.Same(message, MafModelDispatcher.FromChatMessage(MafModelDispatcher.ToChatMessage(message)));
    }

    /// <summary>任意业务节点 ID 仍按原索引串行执行，包括重复文字、Unicode、空 ID；不引入节点并行。</summary>
    [Fact]
    public async Task TwoHundredStepsPreserveOriginalIndexOrder()
    {
        var identities = Enumerable.Range(0, 200).Select(index => index % 3 == 0 ? "" : "节点:/" + index).ToArray();
        var runtime = new MafWorkflowRuntime();
        var visited = new List<int>();
        await runtime.RunAsync(identities, async (index, _) =>
        {
            Assert.Equal(index, visited.Count);
            await Task.Yield();
            visited.Add(index);
        }, TestContext.Current.CancellationToken);
        Assert.Equal(Enumerable.Range(0, identities.Length), visited);
    }

    /// <summary>出错步骤不重试，后续步骤不执行，原异常对象及取消类型不被框架包装改变。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureDoesNotRetryOrWrap(bool cancellation)
    {
        var original = cancellation ? (Exception)new OperationCanceledException("original cancellation") : new InvalidOperationException("original failure");
        var visited = new List<int>();
        var caught = await Record.ExceptionAsync(() => new MafWorkflowRuntime().RunAsync(["a", "b", "c"], (index, _) =>
        {
            visited.Add(index);
            if (index == 1) throw original;
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken));
        Assert.Same(original, caught);
        Assert.Equal([0, 1], visited);
    }

    /// <summary>只排除数据库生成的消息 ID 与时间戳；业务 JSON、数组顺序、参数、用量和错误不作宽松归一化。</summary>
    private static async Task<JsonNode?> TraceAsync(AgentRunFixture fixture)
    {
        var run = await fixture.ReadRunAsync();
        return JsonSerializer.SerializeToNode(new
        {
            requests = fixture.Models.Calls.Select(call => new
            {
                call.Protocol, call.Request.ModelKey, call.Request.SystemPrompt, call.Request.MaxOutputTokens,
                call.Request.SupportsThinking, call.Request.Messages, call.Request.Tools,
                call.Request.Provider.Type, call.Request.Provider.BaseUrl,
            }),
            messages = (await fixture.MessagesAsync()).Select(message => new { message.Ordinal, message.Role, content = JsonNode.Parse(message.ContentJson) }),
            events = fixture.Browser.Events.Select(item => new { item.Sequence, item.Type, item.Text, item.Error, data = item.Json is null ? null : JsonNode.Parse(item.Json) }),
            fixture.Browser.ExecutedTools, fixture.Browser.Approvals,
            run.Status, run.Iteration, run.InputTokens, run.OutputTokens, run.Error,
        });
    }
}
