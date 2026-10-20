using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Application.Abstractions.Ai;

/// <summary>向后台宿主通知持久运行，并取消正在进行的模型流。</summary>
public interface IAgentRunControl
{
    /// <summary>唤醒后台宿主处理已持久化的 Agent 运行。</summary>
    void Enqueue(AgentRunId runId);

    /// <summary>请求取消本进程正在执行的指定 Agent 运行，并返回是否找到该运行。</summary>
    bool Cancel(AgentRunId runId);
}
