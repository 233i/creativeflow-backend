using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Application.Abstractions.Realtime;

/// <summary>等待浏览器执行器完成一个画布本地智能体工具。</summary>
public interface IAgentCanvasBridge
{
    /// <summary>将画布工具交给认领运行的浏览器并等待返回结果。</summary>
    Task<AgentCanvasToolResult> ExecuteAsync(
        AgentRun run,
        string toolUseId,
        string toolName,
        string inputJson,
        TimeSpan timeout,
        CancellationToken ct);

    /// <summary>登记待审批操作并等待当前浏览器的用户决定。</summary>
    Task<bool> RequestApprovalAsync(
        AgentRun run,
        string approvalId,
        string toolName,
        string summary,
        string inputJson,
        TimeSpan timeout,
        CancellationToken ct);

    /// <summary>画布标签页接管运行时，重新发送未完成的调用和审批。</summary>
    Task AttachAsync(AgentRunId runId, string connectionId, CancellationToken ct);

    /// <summary>
    /// 停止向已不再是执行器的连接发送调用，并返回正在等待该标签页的运行。
    /// </summary>
    IReadOnlyList<AgentRunId> Detach(string connectionId);

    /// <summary>运行到达终止状态后清除执行器状态。</summary>
    void Release(AgentRunId runId);

    /// <summary>完成待处理调用。重复提交会被接受但不执行任何操作。</summary>
    bool Submit(
        AgentRunId runId,
        string toolUseId,
        string resultJson,
        bool isError);

    /// <summary>校验当前连接或等待项后提交用户审批结果。</summary>
    bool SubmitApproval(AgentRunId runId, string approvalId, bool approved);
}

/// <param name="Json">按既有契约保存或传递的 JSON 内容。</param>
/// <param name="IsError">工具结果或响应是否表示失败。</param>
public sealed record AgentCanvasToolResult(string Json, bool IsError);
