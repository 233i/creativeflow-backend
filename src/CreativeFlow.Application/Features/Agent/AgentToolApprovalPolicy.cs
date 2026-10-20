using System.Text.Json;

namespace CreativeFlow.Application.Features.Agent;

/// <summary>集中定义 MCP、脚本和画布工具的审批规则；不读写运行或发起工具调用。</summary>
public sealed class AgentToolApprovalPolicy
{
    /// <summary>MCP 调用始终审批，展示服务端原始工具名而非模型侧别名。</summary>
    internal string McpSummary(AgentMcpBinding binding) =>
        $"Allow the Agent to call MCP tool '{binding.OriginalName}' on '{binding.Server.Name}'?";

    /// <summary>有效脚本调用始终审批；无效参数应在请求审批之前返回工具错误。</summary>
    internal string ScriptSummary(AgentSkillScriptInput input) =>
        $"Allow the Agent to run '{input.ScriptPath}' from Skill '{input.Skill!.Name}' with {input.Arguments.Count} argument(s)?";

    /// <summary>仅在描述符或具体操作要求时返回审批说明，空值表示原本无需审批。</summary>
    internal string? CanvasSummary(AgentToolDescriptor descriptor, string inputJson) =>
        RequiresApproval(descriptor, inputJson)
            ? $"Allow the Agent to run the destructive canvas tool '{descriptor.Name}'?"
            : null;

    /// <summary>按工具描述及具体破坏性操作判断是否需要用户批准。</summary>
    private static bool RequiresApproval(AgentToolDescriptor descriptor, string inputJson)
    {
        if (descriptor.Approval == AgentApprovalPolicy.Always) return true;
        if (descriptor.Approval != AgentApprovalPolicy.DestructiveAction) return false;
        try
        {
            using var document = JsonDocument.Parse(inputJson);
            return document.RootElement.TryGetProperty("action", out var action)
                && action.ValueKind == JsonValueKind.String
                && action.GetString() is "clear" or "import" or "replace" or "delete_canvas";
        }
        catch (JsonException)
        {
            return false;
        }
    }

}
