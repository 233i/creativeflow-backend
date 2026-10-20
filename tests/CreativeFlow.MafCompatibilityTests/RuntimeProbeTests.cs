using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using CreativeFlow.MafProbe;

namespace CreativeFlow.MafCompatibilityTests;

/// <summary>用纯内存模型流验证 MAF 控制点，不连接任何服务商或业务数据库。</summary>
public sealed class RuntimeProbeTests
{
    /// <summary>显式透传模式保留历史与参数，并将工具调用交还业务层而不是自动执行。</summary>
    [Fact]
    public async Task SingleTurnPreservesInputAndDoesNotInvokeTools()
    {
        var trace = new List<string>();
        var function = new RecordingFunction(trace);
        var history = new[] { new ChatMessage(ChatRole.System, "unchanged-system"), new ChatMessage(ChatRole.User, "hello") };
        var options = new ChatOptions { ModelId = "existing-model", MaxOutputTokens = 16000, Tools = [function] };
        using var client = new RecordingClient(
            new ChatResponseUpdate(ChatRole.Assistant, "first"),
            new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("call-1", function.Name, new Dictionary<string, object?>())])
            { FinishReason = ChatFinishReason.ToolCalls });
        var agent = RuntimeProbe.CreateSingleTurnAgent(client, options);

        var updates = new List<AIContent>();
        await foreach (var update in agent.RunStreamingAsync(history, cancellationToken: TestContext.Current.CancellationToken))
            updates.AddRange(update.Contents);

        Assert.Equal(1, client.Calls);
        Assert.Empty(trace);
        Assert.Equal(history.Select(message => message.Text), client.Messages.Select(message => message.Text));
        Assert.Equal(history.Select(message => message.Role), client.Messages.Select(message => message.Role));
        Assert.Equal("existing-model", client.Options?.ModelId);
        Assert.Equal(16000, client.Options?.MaxOutputTokens);
        Assert.Same(function, Assert.Single(client.Options!.Tools!));
        Assert.Equal("first", Assert.Single(updates.OfType<TextContent>()).Text);
        Assert.Equal("call-1", Assert.Single(updates.OfType<FunctionCallContent>()).CallId);
    }

    /// <summary>上游异常不会被 MAF 隐式重试，未知结果不会造成第二次模型请求。</summary>
    [Fact]
    public async Task ModelFailureIsNotRetried()
    {
        using var client = new RecordingClient { Failure = new HttpRequestException("upstream-result-unknown") };
        var agent = RuntimeProbe.CreateSingleTurnAgent(client, new ChatOptions());

        var error = await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var update in agent.RunStreamingAsync("hello", cancellationToken: TestContext.Current.CancellationToken))
                Assert.Fail("A failed upstream must not emit a fabricated result.");
        });
        Assert.Equal("upstream-result-unknown", error.Message);
        Assert.Equal(1, client.Calls);
    }

    /// <summary>取消令牌传到模型客户端；不会在取消后重试或制造成功结果。</summary>
    [Fact]
    public async Task CancellationReachesModelWithoutRetry()
    {
        using var source = new CancellationTokenSource();
        using var client = new RecordingClient(new ChatResponseUpdate(ChatRole.Assistant, "first")) { OnCall = source.Cancel };
        var agent = RuntimeProbe.CreateSingleTurnAgent(client, new ChatOptions());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var update in agent.RunStreamingAsync("hello", cancellationToken: source.Token))
                Assert.Fail("The cancelled model must not produce output.");
        });
        Assert.Equal(1, client.Calls);
    }

    /// <summary>记录已验证的不兼容点：MAF 达到工具轮数后仍增加一次模型请求，不能直接替代 Legacy 的硬上限。</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(20)]
    public async Task ToolLoopLimitAddsOneMoreModelRequest(int limit)
    {
        var trace = new List<string>();
        var function = new RecordingFunction(trace);
        using var client = new RecordingClient
        {
            ResponseFactory = call => [new ChatResponseUpdate(ChatRole.Assistant,
                [new FunctionCallContent($"call-{call}-a", function.Name, new Dictionary<string, object?>()),
                 new FunctionCallContent($"call-{call}-b", function.Name, new Dictionary<string, object?>())])
                { FinishReason = ChatFinishReason.ToolCalls }],
        };
        var agent = RuntimeProbe.CreateToolLoopAgent(client, new ChatOptions { Tools = [function] }, limit);

        await foreach (var update in agent.RunStreamingAsync("hello", cancellationToken: TestContext.Current.CancellationToken)) { }

        // 这是框架行为探针，不是迁移验收通过：Legacy 特征测试另行要求恰好 20 次请求。
        Assert.Equal(limit + 1, client.Calls);
        Assert.All(trace.Chunk(2), pair => Assert.Equal(["start", "finish"], pair));
        Assert.Equal(limit * 4, trace.Count);
    }

    /// <summary>候选预算边界在出站之前阻止第 21 次调用，而不是等收到上游响应后才计数。</summary>
    [Fact]
    public async Task ExplicitModelBudgetBlocksExtraRequestBeforeDispatch()
    {
        var trace = new List<string>();
        var function = new RecordingFunction(trace);
        using var client = new RecordingClient
        {
            ResponseFactory = call => [new ChatResponseUpdate(ChatRole.Assistant,
                [new FunctionCallContent($"call-{call}", function.Name, new Dictionary<string, object?>())])
                { FinishReason = ChatFinishReason.ToolCalls }],
        };
        var agent = RuntimeProbe.CreateBoundedToolLoopAgent(client, new ChatOptions { Tools = [function] }, 20);
        await Assert.ThrowsAsync<ModelCallBudgetExceededException>(async () =>
        {
            await foreach (var update in agent.RunStreamingAsync("hello", cancellationToken: TestContext.Current.CancellationToken)) { }
        });
        Assert.Equal(20, client.Calls);
        Assert.Equal(40, trace.Count);
    }

    /// <summary>校准后的框架只执行前 19 轮工具；第 20 轮工具请求须交还业务层映射原失败状态。</summary>
    [Fact]
    public async Task CalibratedLoopMatchesLegacyModelAndToolCounts()
    {
        var trace = new List<string>();
        var function = new RecordingFunction(trace);
        using var client = new RecordingClient
        {
            ResponseFactory = call => [new ChatResponseUpdate(ChatRole.Assistant,
                [new FunctionCallContent($"call-{call}", function.Name, new Dictionary<string, object?>())])
                { FinishReason = ChatFinishReason.ToolCalls }],
        };
        var agent = RuntimeProbe.CreateLegacyCountAgent(client, new ChatOptions { Tools = [function] });
        var calls = new List<FunctionCallContent>();
        await foreach (var update in agent.RunStreamingAsync("hello", cancellationToken: TestContext.Current.CancellationToken))
            calls.AddRange(update.Contents.OfType<FunctionCallContent>());
        Assert.Equal(20, client.Calls);
        Assert.Equal(38, trace.Count);
        Assert.Contains(calls, call => call.CallId == "call-20");
    }

    /// <summary>顺序 Executor 保持业务计划顺序，重复构建不会共享上次运行状态。</summary>
    [Fact]
    public async Task WorkflowSequenceIsStableAndIsolated()
    {
        foreach (var iteration in Enumerable.Range(0, 3))
        {
            var trace = new List<string>();
            var output = await RuntimeProbe.RunSequenceAsync(["text", "image", "video", "result"], trace, TestContext.Current.CancellationToken);
            Assert.Equal(["text", "image", "video", "result"], trace);
            Assert.Equal("input/text/image/video/result", Assert.Single(output));
        }
    }

    /// <summary>只返回固定内存事件，并记录实际请求；无 HttpClient、数据库或文件副作用。</summary>
    private sealed class RecordingClient(params ChatResponseUpdate[] updates) : IChatClient
    {
        /// <summary>已开始的上游请求次数。</summary>
        public int Calls { get; private set; }
        /// <summary>最后一次传入的模型历史。</summary>
        public IReadOnlyList<ChatMessage> Messages { get; private set; } = [];
        /// <summary>最后一次传入的模型参数。</summary>
        public ChatOptions? Options { get; private set; }
        /// <summary>用于注入响应未知的确定性异常。</summary>
        public Exception? Failure { get; init; }
        /// <summary>开始请求时注入取消，不依赖墙上时间或睡眠。</summary>
        public Action? OnCall { get; init; }
        /// <summary>按请求生成新内容实例，避免框架对同一响应对象的处理污染后续录制响应。</summary>
        public Func<int, ChatResponseUpdate[]>? ResponseFactory { get; init; }

        /// <summary>记录 SDK 传入的消息、选项和调用次数，执行可配置故障钩子并逐条返回合成流更新。</summary>
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            Messages = messages.ToArray();
            Options = options;
            OnCall?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            if (Failure is not null) throw Failure;
            foreach (var update in ResponseFactory?.Invoke(Calls) ?? updates)
            {
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                yield return update;
            }
        }

        /// <summary>拒绝非流式请求，确保此探针只验证 SDK 流式路径。</summary>
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The probe requires the streaming path.");

        /// <summary>仅在请求类型匹配且未指定服务键时返回当前合成聊天客户端。</summary>
        public object? GetService(Type serviceType, object? serviceKey = null) => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

        /// <summary>合成客户端不持有外部资源，释放操作不产生副作用。</summary>
        public void Dispose() { }
    }

    /// <summary>仅记录内存开始/结束顺序的工具，避免反射生成 Schema 或真实业务副作用。</summary>
    private sealed class RecordingFunction(IList<string> trace) : AIFunction
    {
        /// <summary>提供用于 SDK 工具循环探针的固定工具名称。</summary>
        public override string Name => "probe_tool";
        /// <summary>向 SDK 声明该工具是没有真实外部副作用的合成工具。</summary>
        public override string Description => "Synthetic tool without side effects.";
        /// <summary>返回没有参数属性的对象 schema，供合成工具调用测试使用。</summary>
        public override JsonElement JsonSchema
        {
            get
            {
                using var document = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{}}");
                return document.RootElement.Clone();
            }
        }

        /// <summary>记录工具执行的开始和结束顺序，中途观察取消，并返回固定成功文本。</summary>
        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            trace.Add("start");
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            trace.Add("finish");
            return "ok";
        }
    }
}
