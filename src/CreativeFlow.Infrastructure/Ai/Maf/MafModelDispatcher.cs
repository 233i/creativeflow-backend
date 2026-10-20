using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Infrastructure.Ai.Maf;

/// <summary>既有三个协议与 ChatClientAgent 的无损边界；不安装工具循环、历史压缩或重试装饰器。</summary>
internal sealed class MafModelDispatcher(IAgentModelDispatcher inner, int maximumCalls) : IAgentModelDispatcher
{
    /// <summary>本次 Agent 运行的硬出站预算；框架异常不得造成第 21 次请求。</summary>
    private int calls;

    /// <summary>扣减本次运行的模型调用预算，通过 ChatClientAgent 传递完整原历史并逐条还原业务流事件。</summary>
    public async IAsyncEnumerable<AgentModelEvent> StreamAsync(ProviderType protocol, AgentModelRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (Interlocked.Increment(ref calls) > maximumCalls)
            throw new InvalidOperationException("The Agent model call budget was exceeded.");
        using var client = new ExistingProtocolClient(inner, protocol, request);
        var agent = new ChatClientAgent(client, new ChatClientAgentOptions
        {
            Id = "creativeflow-agent", Name = "CreativeFlow",
            UseProvidedChatClientAsIs = true,
            ChatOptions = new ChatOptions
            {
                ModelId = request.ModelKey, MaxOutputTokens = request.MaxOutputTokens,
                Tools = request.Tools.Select(tool => (AITool)new SchemaOnlyFunction(tool)).ToList(),
            },
        });
        // 每轮传入完整原历史，不持久化 SDK 会话；业务数据库始终是唯一历史来源。
        var messages = new List<ChatMessage> { new(ChatRole.System, request.SystemPrompt) };
        messages.AddRange(request.Messages.Select(ToChatMessage));
        await foreach (var update in agent.RunStreamingAsync(messages, cancellationToken: ct))
        {
            if (update.AsChatResponseUpdate().RawRepresentation is not AgentModelEvent original)
                throw new InvalidOperationException("The Agent stream lost its protocol representation.");
            yield return original;
        }
    }

    /// <summary>提供类型化框架内容并保留原块用于精确恢复：Json 空值、图片字节和 ProviderItemId 不重新解释。</summary>
    internal static ChatMessage ToChatMessage(AgentModelMessage message) => new(
        message.Role == AgentMessageRole.User ? ChatRole.User : ChatRole.Assistant,
        message.Content.Select(ToContent).ToList()) { RawRepresentation = message };

    /// <summary>只接受由本适配器产生的历史，不把框架合成消息静默加入原请求。</summary>
    internal static AgentModelMessage FromChatMessage(ChatMessage message) =>
        message.RawRepresentation as AgentModelMessage
        ?? throw new InvalidOperationException("The Agent history lost its protocol representation.");

    /// <summary>类型化内容用于框架运行；原始块仍是协议序列化的无损来源，绝不交给自动工具执行。</summary>
    private static AIContent ToContent(AgentContentBlock block)
    {
        AIContent content = block.Kind switch
        {
            AgentContentKind.Text => new TextContent(block.Text),
            AgentContentKind.Thinking => new TextReasoningContent(block.Text),
            AgentContentKind.Image => ImageContent(block),
            AgentContentKind.ToolUse => new FunctionCallContent(block.ToolUseId ?? "missing", block.ToolName ?? "missing", null),
            AgentContentKind.ToolResult => new FunctionResultContent(block.ToolUseId ?? "missing", block.Json),
            _ => throw new InvalidOperationException("Unsupported Agent content kind."),
        };
        content.RawRepresentation = block;
        return content;
    }

    /// <summary>框架视图容忍旧历史中不规范的 MIME；出站仍使用原始块，绝不修改存储或协议字段。</summary>
    private static DataContent ImageContent(AgentContentBlock block)
    {
        try { return new DataContent(block.ImageBytes ?? [], block.MediaType ?? "image/png"); }
        catch (ArgumentException) { return new DataContent(block.ImageBytes ?? [], "application/octet-stream"); }
    }

    /// <summary>框架只消费原协议的流；密钥只在这个运行内闭包中，不进入消息或框架持久化。</summary>
    private sealed class ExistingProtocolClient(IAgentModelDispatcher inner, ProviderType protocol, AgentModelRequest request) : IChatClient
    {
        /// <summary>校验框架未改写系统提示词、历史数量及模型参数，恢复原消息后把每个协议事件包装为一条流更新。</summary>
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var history = messages.ToArray();
            if (history.Length != request.Messages.Count + 1 || history[0].Role != ChatRole.System
                || history[0].Text != request.SystemPrompt || options?.ModelId != request.ModelKey
                || options.MaxOutputTokens != request.MaxOutputTokens)
                throw new InvalidOperationException("The Agent runtime changed the model request.");
            var restored = history.Skip(1).Select(FromChatMessage).ToArray();
            await foreach (var item in inner.StreamAsync(protocol, request with { Messages = restored }, cancellationToken))
            {
                // 原事件随一次 SDK update 往返；空文本、结束原因、用量、错误都不合并或补造。
                yield return new ChatResponseUpdate(ChatRole.Assistant, item.Text)
                {
                    RawRepresentation = item,
                };
            }
        }

        /// <summary>拒绝非流式请求，防止框架改走与原 Agent 不同的模型调用路径。</summary>
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The Agent runtime requires streaming.");
        /// <summary>仅在没有服务键且请求类型匹配当前适配器时返回自身，否则返回空。</summary>
        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
        /// <summary>不释放外层拥有的协议分发器；当前适配器没有需要单独释放的资源。</summary>
        public void Dispose() { }
    }

    /// <summary>完整工具 schema 交给框架但不授权 SDK 执行；工具始终经原审批/画布/MCP/Skill 路径。</summary>
    private sealed class SchemaOnlyFunction(AgentToolDefinition tool) : AIFunction
    {
        /// <summary>向框架提供原业务工具名称，不重命名工具。</summary>
        public override string Name => tool.Name;
        /// <summary>向框架提供原业务工具说明，不重新生成工具描述。</summary>
        public override string Description => tool.Description;
        /// <summary>解析并克隆原工具的完整输入 schema，使返回值不依赖临时 JSON 文档的生命周期。</summary>
        public override JsonElement JsonSchema
        {
            get
            {
                using var document = JsonDocument.Parse(tool.InputSchemaJson);
                return document.RootElement.Clone();
            }
        }
        /// <summary>拒绝由 SDK 直接执行工具，确保实际调用仍经过业务审批与画布桥接路径。</summary>
        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Agent tools must use the business approval bridge.");
    }
}
