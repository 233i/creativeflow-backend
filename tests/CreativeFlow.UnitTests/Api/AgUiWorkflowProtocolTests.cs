using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AGUI.Abstractions;
using AGUI.Formatting;
using CreativeFlow.Api.Http.Handlers;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Application.Abstractions.Persistence;

namespace CreativeFlow.UnitTests.Api;

/// <summary>锁定 AG-UI 请求解析和官方 SSE formatter 的线协议，不依赖数据库或模型供应商。</summary>
public sealed class AgUiWorkflowProtocolTests
{
    /// <summary>测试运行器的取消标记，避免流式断言在测试结束后悬挂。</summary>
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>官方输入类型能读取既有发布工作流的 forwardedProps 参数。</summary>
    [Fact]
    public void RunAgentInputAcceptsForwardedWorkflowInputs()
    {
        using var document = JsonDocument.Parse("""
            {"threadId":"thread-1","runId":"turn-1","forwardedProps":{"inputs":{"question":"hello"}}}
            """);

        var input = document.RootElement.Deserialize(AGUIJsonSerializerContext.Default.RunAgentInput);

        Assert.NotNull(input);
        Assert.Equal("thread-1", input.ThreadId);
        Assert.Equal("turn-1", input.RunId);
        Assert.True(input.ForwardedProperties.HasValue);
        Assert.Equal("hello", input.ForwardedProperties.Value.GetProperty("inputs").GetProperty("question").GetString());
    }

    /// <summary>锁定官方 formatter 的标准 data 帧和事件类型，不能退回自定义 event 名称。</summary>
    [Fact]
    public async Task OfficialFormatterWritesTypedEventsAsSseDataFrames()
    {
        await using var output = new MemoryStream();
        await new SseEventStreamFormatter().WriteAsync(Events(Ct), output, Ct);
        var text = Encoding.UTF8.GetString(output.ToArray());

        Assert.Contains("data:", text, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"RUN_STARTED\"", text, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"TEXT_MESSAGE_CONTENT\"", text, StringComparison.Ordinal);
        Assert.Contains("\"delta\":\"hello\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("event: output.delta", text, StringComparison.Ordinal);
    }

    /// <summary>步骤直接使用节点名称，状态快照同时保留可定位节点的 ID。</summary>
    [Fact]
    public void NodeEventsExposeDisplayNameWithoutChangingStepIdentity()
    {
        var projection = new AgUiWorkflowProjection("internal-run", new JsonObject
        {
            ["threadId"] = "thread-1", ["runId"] = "turn-1",
        }, """{"nodes":[{"id":"llm","type":"llm","data":{"title":"回答模型"}}]}""");
        var state = new ExternalWorkflowRunResponse("request", "internal-run", "workflow", 1, "running", .5,
            new ExternalWorkflowCurrentNodeResponse("llm", "llm", "回答模型"), "2026-01-01T00:00:00Z", null, null, null, null);
        var frames = projection.Project(new WorkflowRunJournalEntry(1, "node.state",
            """{"id":"llm","type":"llm","status":"running","progress":0.4}""", DateTimeOffset.UtcNow), state);

        var started = Assert.Single(frames.OfType<StepStartedEvent>());
        Assert.Equal("回答模型", started.StepName);
        Assert.NotNull(started.Metadata);
        Assert.Equal("llm", started.Metadata.Value.GetProperty("nodeId").GetString());
        Assert.Equal("回答模型", started.Metadata.Value.GetProperty("nodeName").GetString());
        var snapshot = Assert.Single(frames.OfType<StateSnapshotEvent>()).Snapshot;
        Assert.Equal("回答模型", snapshot.GetProperty("nodes")[0].GetProperty("name").GetString());
        Assert.Equal("llm", snapshot.GetProperty("currentNode").GetProperty("id").GetString());
        Assert.Equal("回答模型", snapshot.GetProperty("currentNode").GetProperty("name").GetString());
        Assert.Equal("参数提取", WorkflowNodeDisplayNames.Resolve(
            """{"nodes":[{"id":"extract","type":"parameterExtract","data":{}}]}""", "extract", "parameterExtract"));
    }

    /// <summary>合成标准运行及文本消息生命周期供线协议断言使用。</summary>
    private static async IAsyncEnumerable<BaseEvent> Events(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return new RunStartedEvent { ThreadId = "thread-1", RunId = "run-1" };
        yield return new TextMessageStartEvent { MessageId = "message-1", Role = "assistant" };
        yield return new TextMessageContentEvent { MessageId = "message-1", Delta = "hello" };
        yield return new TextMessageEndEvent { MessageId = "message-1" };
        await Task.CompletedTask;
    }
}
