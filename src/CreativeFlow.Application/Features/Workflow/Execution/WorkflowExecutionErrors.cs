using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Features.Knowledge;

namespace CreativeFlow.Application.Features.Workflow;

/// <summary>节点业务已脱敏的失败，允许沿用现有客户端错误提示。</summary>
public sealed class WorkflowExecutionException : Exception
{
    /// <summary>携带可对用户显示的业务失败原因。</summary>
    public WorkflowExecutionException(string message) : base(message) { }

    /// <summary>保留内部异常供诊断，但对外仅使用已脱敏的业务消息。</summary>
    public WorkflowExecutionException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>标记持久化取消已赢得竞争，阻止后续节点及成功状态写入。</summary>
public sealed class WorkflowCancelledException : Exception { }

/// <summary>集中保留原工作流错误交付规则，未知异常不泄漏上游地址或凭据。</summary>
public static class WorkflowExecutionErrors
{
    /// <summary>仅交付已脱敏业务错误；保留 HTTP 错误码以及知识服务的诊断提示。</summary>
    public static string UserFacingError(Exception exception) => exception is WorkflowHttpException external
        ? $"[{external.Code}] {external.Message}" : exception is WorkflowExecutionException or WorkflowV2Exception or KnowledgeException
            ? exception.Message
            : "The workflow node could not be completed.";
}
