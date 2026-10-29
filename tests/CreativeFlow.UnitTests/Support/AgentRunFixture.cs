using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using CreativeFlow.Application;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Abstractions.Realtime;
using CreativeFlow.Application.Abstractions.Security;
using CreativeFlow.Application.Contracts.Agent;
using CreativeFlow.Application.Features.Agent;
using CreativeFlow.Application.Features.Identity;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;
using CreativeFlow.Infrastructure.Ai;
using CreativeFlow.Infrastructure.Ai.Maf;
using CreativeFlow.Infrastructure.Options;
using CreativeFlow.Infrastructure.Persistence;
using CreativeFlow.Infrastructure.Skills;

namespace CreativeFlow.UnitTests.Support;

/// <summary>使用隔离 SQL、原 Agent 运行器和原画布桥的特征测试；只把上游与浏览器替换为内存响应。</summary>
internal sealed class AgentRunFixture : IAsyncDisposable
{
    /// <summary>按正式 AddApplication 注册解析运行器，不在测试中拼装另一套执行路径。</summary>
    private ServiceProvider? serviceProvider;
    /// <summary>每次合成运行独享的应用服务作用域。</summary>
    private AsyncServiceScope? serviceScope;
    /// <summary>独占的随机 schema；绝不使用开发或生产连接。</summary>
    public ImageJobDatabase Database { get; private set; } = null!;
    /// <summary>真实 Agent 持久化仓储。</summary>
    public AgentRepository Agents { get; private set; } = null!;
    /// <summary>隔离数据库中的 MCP 配置，仅供合成工具目录测试使用。</summary>
    public McpServerRepository McpServers { get; private set; } = null!;
    /// <summary>隔离数据库中的 Skill 配置，用于验证执行时重新检查启用状态。</summary>
    public SkillRepository Skills { get; private set; } = null!;
    /// <summary>仅驻留内存的合成素材字节，不访问真实存储。</summary>
    public ImageGenerationHttpFixture.MemoryAssets AssetStore { get; } = new();
    /// <summary>待执行的合成运行。</summary>
    public AgentRun Run { get; private set; } = null!;
    /// <summary>未经修改的业务运行器。</summary>
    public AgentTurnRunner Runner { get; private set; } = null!;
    /// <summary>可观测的模型请求与合成响应。</summary>
    public RecordedModel Models { get; } = new();
    /// <summary>会在原画布桥上提交工具结果和审批的测试浏览器。</summary>
    public RecordedBrowser Browser { get; } = new();
    /// <summary>用于验证解密失败不发起请求的非生产密钥保护器。</summary>
    public ImageGenerationHttpFixture.TestProtector Protector { get; } = new();
    /// <summary>真实后台账户恢复所使用的隔离上下文。</summary>
    public AccountContext Context { get; } = new();

    /// <summary>建立最小的真实账号、模型、会话和运行，再装配原业务路径。</summary>
    public static async Task<AgentRunFixture> CreateAsync(ProviderType protocol = ProviderType.OpenAiChat, int? maxOutputTokens = null,
        IMcpRuntime? mcpRuntime = null, ISkillStore? skillStore = null, ISkillScriptRunner? skillRunner = null)
    {
        var fixture = new AgentRunFixture { Database = await ImageJobDatabase.CreateAsync() };
        try
        {
            var ct = TestContext.Current.CancellationToken;
            var connections = new CreativeFlowConnectionFactory(fixture.Database.Source, fixture.Context);
            fixture.Agents = new AgentRepository(connections);
            fixture.McpServers = new McpServerRepository(connections);
            fixture.Skills = new SkillRepository(connections);
            var providers = new ProviderRepository(connections);
            var now = DateTimeOffset.UtcNow;
            var provider = new AiProvider(new ProviderId(fixture.Database.Provider), null, "Migration fixture", null, null,
                protocol, new ApiKeySecret([1], [2], [3], "test"), "https://provider.invalid/v1", true, null, 0, now, now);
            await providers.InsertAsync(provider, ct);
            await providers.InsertModelAsync(new AiModel(ModelId.New(), provider.Id, "test-chat", "Test chat", true, null,
                ModelCategory.Chat, null, null, maxOutputTokens, true, true, true, 0, now), ct);
            var device = new DeviceId(fixture.Database.Device);
            var session = new AgentSession(AgentSessionId.New(), device, new CanvasId(fixture.Database.Canvas), "Fixture", now, now);
            await fixture.Agents.InsertSessionAsync(session, ct);
            fixture.Run = new AgentRun(AgentRunId.New(), session.Id, device, session.CanvasId, provider.Id, "test-chat",
                AgentRunStatus.Queued, "fixture-browser", 0, 0, null, null, null, null, null, now, now);
            await fixture.Agents.InsertRunAsync(fixture.Run, ct);
            await fixture.Agents.InsertMessageAsync(new AgentStoredMessage(Guid.NewGuid(), session.Id, fixture.Run.Id,
                device, 0, "user", "[{\"kind\":\"text\",\"text\":\"unchanged prompt\"}]", now), ct);

            // 保留真实桥接、SQL、账户恢复和事件写入；任何意外 MCP/Skill 文件操作直接失败。
            var bridge = new AgentCanvasBridge(fixture.Browser);
            fixture.Browser.Bridge = bridge;
            fixture.Browser.Agents = fixture.Agents;
            var services = new ServiceCollection();
            services.AddApplication();
            services.AddScoped(_ => new BackgroundAccountScope(new AccountRepository(fixture.Database.Source), fixture.Context));
            services.AddSingleton<IAgentRepository>(fixture.Agents);
            services.AddSingleton<IProviderRepository>(providers);
            services.AddSingleton<IMcpServerRepository>(fixture.McpServers);
            services.AddSingleton<IApiKeyProtector>(fixture.Protector);
            services.AddSingleton<IAgentModelDispatcher>(fixture.Models);
            services.AddSingleton<IMcpRuntime>(mcpRuntime ?? new NoExternalTools());
            services.AddSingleton<ISkillRepository>(fixture.Skills);
            services.AddSingleton<ISkillStore>(skillStore ?? new NoExternalTools());
            services.AddSingleton<ISkillScriptRunner>(skillRunner ?? new DisabledSkillScriptRunner(new SkillsOptions()));
            services.AddSingleton<IAgentRealtimeSink>(fixture.Browser);
            services.AddSingleton<IAgentCanvasBridge>(bridge);
            services.AddSingleton<ICanvasAssetRepository>(new CanvasAssetRepository(connections));
            services.AddSingleton<IAssetStore>(fixture.AssetStore);
            services.AddScoped<IAgentRuntime, MafAgentRuntime>();
            fixture.serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            fixture.serviceScope = fixture.serviceProvider.CreateAsyncScope();
            fixture.Runner = fixture.serviceScope.Value.ServiceProvider.GetRequiredService<AgentTurnRunner>();
            return fixture;
        }
        catch { await fixture.DisposeAsync(); throw; }
    }

    /// <summary>执行当前合成运行，不启动后台宿主或网络服务。</summary>
    public Task ExecuteAsync() => Runner.RunAsync(Run.Id, TestContext.Current.CancellationToken);

    /// <summary>重新读取持久化终态，避免使用运行前的对象快照作断言。</summary>
    public async Task<AgentRun> ReadRunAsync() =>
        (await Agents.FindRunByIdAsync(Run.Id, TestContext.Current.CancellationToken))!;

    /// <summary>读取已持久消息，供历史和内容块顺序对照。</summary>
    public Task<IReadOnlyList<AgentStoredMessage>> MessagesAsync() =>
        Agents.ListMessagesAsync(Run.DeviceId, Run.SessionId, TestContext.Current.CancellationToken);

    /// <summary>释放该 Agent 特征测试拥有的隔离数据库环境。</summary>
    public async ValueTask DisposeAsync()
    {
        if (serviceScope is { } scope) await scope.DisposeAsync();
        if (serviceProvider is not null) await serviceProvider.DisposeAsync();
        await Database.DisposeAsync();
    }

    /// <summary>合成模型只产生固定事件；每次保存不可变历史快照，避免后续循环污染基线。</summary>
    internal sealed class RecordedModel : IAgentModelDispatcher
    {
        /// <summary>实际请求与选择的协议，保持调用顺序。</summary>
        public List<(ProviderType Protocol, AgentModelRequest Request)> Calls { get; } = [];
        /// <summary>按一基调用序号返回合成事件。</summary>
        public Func<int, IReadOnlyList<AgentModelEvent>> Respond { get; set; } = _ =>
            [new(AgentModelEventKind.TextDelta, Text: "done"), new(AgentModelEventKind.Completed, StopReason: "stop", InputTokens: 3, OutputTokens: 5)];

        /// <summary>记录原协议、历史与工具定义的快照，再按调用次数回放配置的 Agent 事件并观察取消。</summary>
        public async IAsyncEnumerable<AgentModelEvent> StreamAsync(ProviderType protocol, AgentModelRequest request,
            [EnumeratorCancellation] CancellationToken ct)
        {
            Calls.Add((protocol, request with { Messages = request.Messages.ToArray(), Tools = request.Tools.ToArray() }));
            foreach (var item in Respond(Calls.Count))
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return item;
            }
        }
    }

    /// <summary>从原桥接收到调用后回送合成结果，同时核对通知前 SQL 已保存。</summary>
    internal sealed class RecordedBrowser : IAgentRealtimeSink
    {
        /// <summary>与生产运行器共享的原画布桥。</summary>
        public AgentCanvasBridge Bridge { get; set; } = null!;
        /// <summary>用于核对事件和审批状态的原仓储。</summary>
        public AgentRepository Agents { get; set; } = null!;
        /// <summary>所有临时和持久事件的到达顺序。</summary>
        public List<AgentEventEnvelope> Events { get; } = [];
        /// <summary>浏览器实际执行的工具 ID。</summary>
        public List<string> ExecutedTools { get; } = [];
        /// <summary>收到的审批 ID。</summary>
        public List<string> Approvals { get; } = [];
        /// <summary>合成用户对本次工具的审批决定。</summary>
        public bool Approve { get; set; } = true;
        /// <summary>在工具回执前注入数据库取消竞争，不通过等待制造竞态。</summary>
        public Func<Task>? BeforeToolResult { get; set; }
        /// <summary>模拟浏览器报告的通信错误，不执行实际画布或付费生成。</summary>
        public AgentCanvasToolResult ToolResult { get; set; } = new("{\"ok\":true}", false);
        /// <summary>审批返回前注入权限变化或取消，验证运行器的既有复验时点。</summary>
        public Func<Task>? BeforeApprovalResult { get; set; }
        /// <summary>在实时发送前观察数据库顺序，或模拟通知故障。</summary>
        public Func<AgentEventEnvelope, Task>? BeforeEvent { get; set; }

        /// <summary>在记录实时事件前确认正序号事件已持久化且载荷一致，锁定先保存后通知的顺序。</summary>
        public async Task SendEventAsync(DeviceId deviceId, AgentEventEnvelope payload, CancellationToken ct)
        {
            if (payload.Sequence > 0)
            {
                var stored = await Agents.ListEventsAsync(deviceId, new AgentRunId(Guid.Parse(payload.RunId)), payload.Sequence - 1, ct);
                Assert.Equal(payload.Type, Assert.Single(stored).Type);
                // PostgreSQL jsonb 原本就会规范化空白和对象属性顺序；这里只比较跨介质语义。
                Assert.True(JsonNode.DeepEquals(JsonNode.Parse(payload.Json!), JsonNode.Parse(Assert.Single(stored).PayloadJson)));
            }
            if (BeforeEvent is not null) await BeforeEvent(payload);
            Events.Add(payload);
        }

        /// <summary>断言工具只发给夹具执行器且运行状态正确，记录调用并通过原画布桥提交合成结果。</summary>
        public async Task SendToolCallAsync(string connectionId, AgentToolCallEnvelope payload, CancellationToken ct)
        {
            Assert.Equal("fixture-browser", connectionId);
            var run = (await Agents.FindRunByIdAsync(new AgentRunId(Guid.Parse(payload.RunId)), ct))!;
            // 有浏览器执行连接时旧实现仍为 running；仅缺少连接时才进入 awaiting_canvas。
            Assert.Equal(AgentRunStatus.Running, run.Status);
            ExecutedTools.Add(payload.ToolUseId);
            if (BeforeToolResult is not null) await BeforeToolResult();
            Assert.True(Bridge.Submit(run.Id, payload.ToolUseId, ToolResult.Json, ToolResult.IsError));
        }

        /// <summary>断言运行处于等待审批状态，记录审批标识并按夹具决策通过原画布桥返回结果。</summary>
        public async Task SendApprovalRequestAsync(string connectionId, AgentApprovalEnvelope payload, CancellationToken ct)
        {
            Assert.Equal("fixture-browser", connectionId);
            var run = (await Agents.FindRunByIdAsync(new AgentRunId(Guid.Parse(payload.RunId)), ct))!;
            Assert.Equal(AgentRunStatus.AwaitingApproval, run.Status);
            Approvals.Add(payload.ApprovalId);
            if (BeforeApprovalResult is not null) await BeforeApprovalResult();
            Assert.True(Bridge.SubmitApproval(run.Id, payload.ApprovalId, Approve));
        }
    }

    /// <summary>没有安装 MCP/Skill 的测试场景禁止一切外部工具和包操作。</summary>
    private sealed class NoExternalTools : IMcpRuntime, ISkillStore
    {
        /// <summary>拒绝该画布 Agent 场景之外的 MCP 工具目录请求。</summary>
        public Task<IReadOnlyList<McpRuntimeTool>> ListToolsAsync(McpServer server, CancellationToken ct) => throw new NotSupportedException();
        /// <summary>拒绝该画布 Agent 场景之外的 MCP 工具执行。</summary>
        public Task<McpRuntimeResult> CallToolAsync(McpServer server, string toolName, string argumentsJson, CancellationToken ct) => throw new NotSupportedException();
        /// <summary>拒绝 Agent 特征测试意外安装 Skill 包。</summary>
        public Task<string> InstallAsync(DeviceId deviceId, SkillId skillId, IReadOnlyList<SkillPackageFile> files, CancellationToken ct) => throw new NotSupportedException();
        /// <summary>拒绝 Agent 特征测试意外读取 Skill 文本。</summary>
        public Task<string> ReadTextAsync(string storagePath, string relativePath, int maxBytes, CancellationToken ct) => throw new NotSupportedException();
        /// <summary>拒绝 Agent 特征测试意外列出 Skill 包文件。</summary>
        public Task<IReadOnlyList<string>> ListFilesAsync(string storagePath, int maxEntries, CancellationToken ct) => throw new NotSupportedException();
        /// <summary>拒绝 Agent 特征测试意外读取完整 Skill 包。</summary>
        public Task<IReadOnlyList<SkillPackageFile>> ReadPackageAsync(string storagePath, int maxEntries, int maxBytes, CancellationToken ct) => throw new NotSupportedException();
        /// <summary>拒绝 Agent 特征测试意外删除 Skill 包。</summary>
        public Task DeleteAsync(string storagePath, CancellationToken ct) => throw new NotSupportedException();
    }
}
