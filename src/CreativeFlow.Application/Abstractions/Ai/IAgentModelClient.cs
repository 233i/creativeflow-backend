using System.Runtime.CompilerServices;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Application.Abstractions.Ai;

/// <summary>
/// 流式输出一次模型轮次，其中可包含文本、思考和工具调用内容块。
/// </summary>
/// <remarks>
/// 与 <see cref="ITextGenerationClient"/> 保持分离。提示词改写刻意采用简短的非流式契约；
/// 智能体轮次包含多条消息，并为下一次迭代保留结构化内容块。若扩展该接口，
/// 会迫使每种协议实现其调用方根本用不到的功能。
/// </remarks>
public interface IAgentModelClient
{
    /// <summary>适配器实现的上游协议。</summary>
    ProviderType Protocol { get; }

    /// <summary>按 IAgentModelClient 对应的协议消费模型流，保持文本、思考与工具调用事件的顺序。</summary>
    IAsyncEnumerable<AgentModelEvent> StreamAsync(
        AgentModelRequest request,
        CancellationToken ct);
}

public interface IAgentModelDispatcher
{
    /// <summary>按 IAgentModelDispatcher 对应的协议消费模型流，保持文本、思考与工具调用事件的顺序。</summary>
    IAsyncEnumerable<AgentModelEvent> StreamAsync(
        ProviderType protocol,
        AgentModelRequest request,
        CancellationToken ct);
}

public enum AgentMessageRole
{
    /// <summary>用户或工具结果对应的模型输入角色。</summary>
    User,
    /// <summary>模型助手输出角色。</summary>
    Assistant,
}

public enum AgentContentKind
{
    /// <summary>普通文本内容或值类型。</summary>
    Text,
    /// <summary>图片数据、素材或模型类别。</summary>
    Image,
    /// <summary>模型返回的思考内容块。</summary>
    Thinking,
    /// <summary>模型发出的工具调用内容块。</summary>
    ToolUse,
    /// <summary>工具执行后返回的结果内容块。</summary>
    ToolResult,
}

/// <param name="Kind">当前资源或数据项的种类。</param>
/// <param name="Text">文本内容或本次流式文本片段。</param>
/// <param name="ToolUseId">一次模型工具调用的关联标识。</param>
/// <param name="ToolName">模型调用或界面展示的工具名称。</param>
/// <param name="Json">按既有契约保存或传递的 JSON 内容。</param>
/// <param name="IsError">工具结果或响应是否表示失败。</param>
/// <param name="ProviderItemId">上游消息/工具项标识，回传历史时保持关联。</param>
/// <param name="ImageUrl">图片引用地址。</param>
/// <param name="MediaType">内容的 MIME 类型，响应时沿用已验证的存储值。</param>
/// <param name="ImageBytes">图片的原始字节内容。</param>
public sealed record AgentContentBlock(
    AgentContentKind Kind,
    string? Text = null,
    string? ToolUseId = null,
    string? ToolName = null,
    string? Json = null,
    bool IsError = false,
    string? ProviderItemId = null,
    string? ImageUrl = null,
    string? MediaType = null,
    byte[]? ImageBytes = null);

/// <param name="Role">消息角色或模型角色，含义由所属契约定义。</param>
/// <param name="Content">正文或消息内容。</param>
public sealed record AgentModelMessage(
    AgentMessageRole Role,
    IReadOnlyList<AgentContentBlock> Content);

/// <param name="Name">实体或配置项的名称。</param>
/// <param name="Description">面向用户或模型展示的说明文本。</param>
/// <param name="InputSchemaJson">对外输入契约的 JSON schema。</param>
public sealed record AgentToolDefinition(
    string Name,
    string Description,
    string InputSchemaJson);

/// <param name="Provider">当前使用的渠道或身份提供方配置。</param>
/// <param name="ApiKey">渠道调用密钥；ApiKeySecret 类型为受保护的持久化表示，字符串类型仅用于请求或执行。</param>
/// <param name="ModelKey">上游识别的模型名称，不等同于本地模型配置标识。</param>
/// <param name="SystemPrompt">与用户正文分离的系统级模型指令。</param>
/// <param name="Messages">按会话顺序排列的消息集合。</param>
/// <param name="Tools">模型可用的工具或 MCP 工具目录。</param>
/// <param name="MaxOutputTokens">模型调用允许的最大输出 token 数量。</param>
/// <param name="SupportsThinking">模型是否声明支持思考内容。</param>
public sealed record AgentModelRequest(
    AiProvider Provider,
    string ApiKey,
    string ModelKey,
    string SystemPrompt,
    IReadOnlyList<AgentModelMessage> Messages,
    IReadOnlyList<AgentToolDefinition> Tools,
    int MaxOutputTokens,
    bool SupportsThinking = false);

public enum AgentModelEventKind
{
    /// <summary>当前模型流的文本增量事件。</summary>
    TextDelta,
    /// <summary>当前模型流的思考增量事件。</summary>
    ThinkingDelta,
    /// <summary>模型发出的工具调用内容块。</summary>
    ToolUse,
    /// <summary>模型流或处理步骤已经结束。</summary>
    Completed,
    /// <summary>已经失败并保存失败信息。</summary>
    Failed,
}

/// <param name="Json">
/// <see cref="AgentModelEventKind.ToolUse"/> 的完整工具输入 JSON。协议客户端会累积参数片段，
/// 只在完整 JSON 对象可用后才发出事件。
/// </param>
/// <param name="Kind">当前资源或数据项的种类。</param>
/// <param name="Text">文本内容或本次流式文本片段。</param>
/// <param name="ToolUseId">一次模型工具调用的关联标识。</param>
/// <param name="ToolName">模型调用或界面展示的工具名称。</param>
/// <param name="StopReason">上游模型结束当前轮次的原因。</param>
/// <param name="InputTokens">模型报告或运行累计的输入 token 数量。</param>
/// <param name="OutputTokens">模型报告或运行累计的输出 token 数量。</param>
/// <param name="Error">失败详情或错误分类，具体格式沿用所属契约。</param>
/// <param name="ProviderItemId">上游消息/工具项标识，回传历史时保持关联。</param>
public sealed record AgentModelEvent(
    AgentModelEventKind Kind,
    string? Text = null,
    string? ToolUseId = null,
    string? ToolName = null,
    string? Json = null,
    string? StopReason = null,
    int? InputTokens = null,
    int? OutputTokens = null,
    string? Error = null,
    string? ProviderItemId = null);
