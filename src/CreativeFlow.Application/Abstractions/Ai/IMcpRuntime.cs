using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Application.Abstractions.Ai;

public interface IMcpRuntime
{
    /// <summary>读取当前可用工具目录。</summary>
    Task<IReadOnlyList<McpRuntimeTool>> ListToolsAsync(McpServer server, CancellationToken ct);

    /// <summary>通过已配置的 MCP 服务器调用指定工具，返回协议执行结果。</summary>
    Task<McpRuntimeResult> CallToolAsync(
        McpServer server,
        string toolName,
        string argumentsJson,
        CancellationToken ct);
}

/// <param name="Name">实体或配置项的名称。</param>
/// <param name="Description">面向用户或模型展示的说明文本。</param>
/// <param name="InputSchemaJson">对外输入契约的 JSON schema。</param>
public sealed record McpRuntimeTool(
    string Name,
    string Description,
    string InputSchemaJson);

/// <param name="Json">按既有契约保存或传递的 JSON 内容。</param>
/// <param name="IsError">工具结果或响应是否表示失败。</param>
public sealed record McpRuntimeResult(string Json, bool IsError);
