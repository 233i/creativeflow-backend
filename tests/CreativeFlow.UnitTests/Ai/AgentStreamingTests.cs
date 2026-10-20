using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;
using CreativeFlow.Infrastructure.Ai.Protocols;

namespace CreativeFlow.UnitTests.Ai;

public class AgentStreamingTests
{
    /// <summary>回归场景：Sse Reader Joins Data Lines And Keeps The Final Frame At Eof。</summary>
    [Fact]
    public async Task SseReaderJoinsDataLinesAndKeepsTheFinalFrameAtEof()
    {
        const string fixture = """
            : heartbeat
            event: first
            data: one
            data: two

            data: final
            """;
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(fixture));

        var frames = new List<SseFrame>();
        await foreach (var frame in SseReader.ReadAsync(stream, TestContext.Current.CancellationToken))
        {
            frames.Add(frame);
        }

        Assert.Collection(
            frames,
            first =>
            {
                Assert.Equal("first", first.EventName);
                Assert.Equal("one\ntwo", first.Data);
            },
            final =>
            {
                Assert.Null(final.EventName);
                Assert.Equal("final", final.Data);
            });
    }

    /// <summary>回归场景：Anthropic Accumulates Split Tool Json Before Emitting Tool Use。</summary>
    [Fact]
    public async Task AnthropicAccumulatesSplitToolJsonBeforeEmittingToolUse()
    {
        const string fixture = """
            event: message_start
            data: {"type":"message_start","message":{"usage":{"input_tokens":11}}}

            event: content_block_start
            data: {"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"tool-1","name":"search"}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"query\":"}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"\"cats\"}"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":0}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":7}}

            event: message_stop
            data: {"type":"message_stop"}

            """;
        var client = new AnthropicAgentClient(new TestHttpClientFactory(fixture));

        var events = await CollectAsync(client.StreamAsync(
            Request(ProviderType.Anthropic), TestContext.Current.CancellationToken));

        Assert.Collection(
            events,
            tool =>
            {
                Assert.Equal(AgentModelEventKind.ToolUse, tool.Kind);
                Assert.Equal("tool-1", tool.ToolUseId);
                Assert.Equal("search", tool.ToolName);
                Assert.Equal("{\"query\":\"cats\"}", tool.Json);
            },
            completed =>
            {
                Assert.Equal(AgentModelEventKind.Completed, completed.Kind);
                Assert.Equal("tool_use", completed.StopReason);
                Assert.Equal(11, completed.InputTokens);
                Assert.Equal(7, completed.OutputTokens);
            });
    }

    /// <summary>回归场景：Open Ai Accumulates Split Function Arguments By Call Index。</summary>
    [Fact]
    public async Task OpenAiAccumulatesSplitFunctionArgumentsByCallIndex()
    {
        const string fixture = """
            data: {"choices":[{"delta":{"content":"Looking"},"finish_reason":null}]}

            data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call-1","function":{"name":"lookup","arguments":"{\"id\":"}}]},"finish_reason":null}]}

            data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"42}"}}]},"finish_reason":"tool_calls"}]}

            data: {"choices":[],"usage":{"prompt_tokens":13,"completion_tokens":5}}

            data: [DONE]

            """;
        var client = new OpenAiChatAgentClient(new TestHttpClientFactory(fixture));

        var events = await CollectAsync(client.StreamAsync(
            Request(ProviderType.OpenAiChat), TestContext.Current.CancellationToken));

        Assert.Collection(
            events,
            text =>
            {
                Assert.Equal(AgentModelEventKind.TextDelta, text.Kind);
                Assert.Equal("Looking", text.Text);
            },
            tool =>
            {
                Assert.Equal(AgentModelEventKind.ToolUse, tool.Kind);
                Assert.Equal("call-1", tool.ToolUseId);
                Assert.Equal("lookup", tool.ToolName);
                Assert.Equal("{\"id\":42}", tool.Json);
            },
            completed =>
            {
                Assert.Equal(AgentModelEventKind.Completed, completed.Kind);
                Assert.Equal("tool_calls", completed.StopReason);
                Assert.Equal(13, completed.InputTokens);
                Assert.Equal(5, completed.OutputTokens);
            });
    }

    /// <summary>回归场景：Responses Streams Reasoning Text And Function Call Arguments。</summary>
    [Fact]
    public async Task ResponsesStreamsReasoningTextAndFunctionCallArguments()
    {
        const string fixture = """
            event: response.reasoning_summary_text.delta
            data: {"type":"response.reasoning_summary_text.delta","delta":"Planning"}

            event: response.output_text.delta
            data: {"type":"response.output_text.delta","delta":"Checking"}

            event: response.output_item.added
            data: {"type":"response.output_item.added","item":{"type":"function_call","id":"fc_1","call_id":"call_1","name":"lookup","arguments":""}}

            event: response.function_call_arguments.delta
            data: {"type":"response.function_call_arguments.delta","item_id":"fc_1","call_id":"call_1","delta":"{\"id\":"}

            event: response.function_call_arguments.delta
            data: {"type":"response.function_call_arguments.delta","item_id":"fc_1","call_id":"call_1","delta":"42}"}

            event: response.function_call_arguments.done
            data: {"type":"response.function_call_arguments.done","item_id":"fc_1","call_id":"call_1","arguments":"{\"id\":42}"}

            event: response.output_item.done
            data: {"type":"response.output_item.done","item":{"type":"function_call","id":"fc_1","call_id":"call_1","name":"lookup","arguments":"{\"id\":42}"}}

            event: response.completed
            data: {"type":"response.completed","response":{"status":"completed","output":[{"type":"function_call","id":"fc_1","call_id":"call_1","name":"lookup","arguments":"{\"id\":42}"}],"usage":{"input_tokens":17,"output_tokens":8}}}

            """;
        var client = new OpenAiResponsesAgentClient(new TestHttpClientFactory(fixture));

        var events = await CollectAsync(client.StreamAsync(
            Request(ProviderType.OpenAiResponses), TestContext.Current.CancellationToken));

        Assert.Collection(
            events,
            thinking =>
            {
                Assert.Equal(AgentModelEventKind.ThinkingDelta, thinking.Kind);
                Assert.Equal("Planning", thinking.Text);
            },
            text =>
            {
                Assert.Equal(AgentModelEventKind.TextDelta, text.Kind);
                Assert.Equal("Checking", text.Text);
            },
            tool =>
            {
                Assert.Equal(AgentModelEventKind.ToolUse, tool.Kind);
                Assert.Equal("call_1", tool.ToolUseId);
                Assert.Equal("fc_1", tool.ProviderItemId);
                Assert.Equal("lookup", tool.ToolName);
                Assert.Equal("{\"id\":42}", tool.Json);
            },
            completed =>
            {
                Assert.Equal(AgentModelEventKind.Completed, completed.Kind);
                Assert.Equal("tool_calls", completed.StopReason);
                Assert.Equal(17, completed.InputTokens);
                Assert.Equal(8, completed.OutputTokens);
            });
    }

    /// <summary>回归场景：Responses Input Uses Function Call And Output Items。</summary>
    [Fact]
    public void ResponsesInputUsesFunctionCallAndOutputItems()
    {
        var input = AgentProtocolJson.ResponsesInput(
        [
            new AgentModelMessage(
                AgentMessageRole.Assistant,
                [new AgentContentBlock(
                    AgentContentKind.ToolUse,
                    ToolUseId: "call_1",
                    ToolName: "lookup",
                    Json: "{\"id\":42}",
                    ProviderItemId: "fc_1")]),
            new AgentModelMessage(
                AgentMessageRole.User,
                [new AgentContentBlock(
                    AgentContentKind.ToolResult,
                    ToolUseId: "call_1",
                    Json: "{\"ok\":true}")]),
        ]);

        var functionCall = Assert.IsType<JsonObject>(input[0]);
        Assert.Equal("function_call", (string?)functionCall["type"]);
        Assert.Equal("fc_1", (string?)functionCall["id"]);
        Assert.Equal("call_1", (string?)functionCall["call_id"]);

        var functionOutput = Assert.IsType<JsonObject>(input[1]);
        Assert.Equal("function_call_output", (string?)functionOutput["type"]);
        Assert.Equal("call_1", (string?)functionOutput["call_id"]);
        Assert.Equal("{\"ok\":true}", (string?)functionOutput["output"]);
    }

    /// <summary>回归场景：Anthropic Removes Open Ai Version Segment Before Appending Messages。</summary>
    [Fact]
    public async Task AnthropicRemovesOpenAiVersionSegmentBeforeAppendingMessages()
    {
        var factory = new TestHttpClientFactory(string.Empty);
        var client = new AnthropicAgentClient(factory);

        await CollectAsync(client.StreamAsync(
            Request(ProviderType.OpenAiChat, "https://example.test/v1"),
            TestContext.Current.CancellationToken));

        Assert.Equal(
            "https://example.test/v1/messages",
            factory.LastRequestUri?.ToString());
    }

    /// <summary>完整消费模型异步事件流，供断言顺序与最终响应使用。</summary>
    private static async Task<List<AgentModelEvent>> CollectAsync(
        IAsyncEnumerable<AgentModelEvent> stream)
    {
        var events = new List<AgentModelEvent>();
        await foreach (var item in stream) events.Add(item);
        return events;
    }

    /// <summary>构造带关联 id 的 MCP JSON-RPC 请求对象。</summary>
    private static AgentModelRequest Request(
        ProviderType protocol,
        string baseUrl = "https://example.test") => new(
        new AiProvider(
            ProviderId.New(),
            BuiltinId: null,
            Name: "provider",
            Description: null,
            Icon: null,
            protocol,
            ApiKey: null,
            BaseUrl: baseUrl,
            Enabled: true,
            PresetVersion: null,
            SortOrder: 0,
            CreatedAt: DateTimeOffset.UnixEpoch,
            UpdatedAt: DateTimeOffset.UnixEpoch),
        ApiKey: "test-key",
        ModelKey: "test-model",
        SystemPrompt: "system",
        Messages: [],
        Tools: [],
        MaxOutputTokens: 1_000);

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        /// <summary>当前测试或组件注册的请求处理委托。</summary>
        private readonly FixtureHandler _handler;
        /// <summary>当前组件复用的客户端实例。</summary>
        private readonly HttpClient _client;

        /// <summary>初始化 TestHttpClientFactory，保留既有依赖、配置校验与资源生命周期。</summary>
        public TestHttpClientFactory(string responseBody)
        {
            _handler = new FixtureHandler(responseBody);
            _client = new HttpClient(_handler);
        }

        /// <summary>测试捕获的最近一次上游请求地址。</summary>
        public Uri? LastRequestUri => _handler.LastRequestUri;

        /// <summary>校验目的地址并创建带既有网络限制的 MCP HTTP 客户端。</summary>
        public HttpClient CreateClient(string name)
        {
            Assert.Equal("upstream", name);
            return _client;
        }
    }

    /// <summary>初始化 FixtureHandler，保留既有依赖、配置校验与资源生命周期。</summary>
    private sealed class FixtureHandler(string responseBody) : HttpMessageHandler
    {
        /// <summary>测试捕获的最近一次上游请求地址。</summary>
        public Uri? LastRequestUri { get; private set; }

        /// <summary>记录模型请求地址，并以预设文本构造成功的 SSE 响应流，不发起真实网络请求。</summary>
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(
                    new MemoryStream(Encoding.UTF8.GetBytes(responseBody), writable: false)),
                RequestMessage = request,
            };
            response.Content.Headers.ContentType = new("text/event-stream");
            return Task.FromResult(response);
        }
    }
}
