namespace CreativeFlow.Application.Abstractions.Ai;

/// <summary>HTTP 节点的安全错误，不携带 URL、密钥、原始响应或内部异常。</summary>
/// <param name="code">稳定错误类别。</param>
/// <param name="message">可向调用方显示的解释。</param>
/// <param name="mayHaveExecuted">远端可能已受理；不得据此自动重发。</param>
public sealed class WorkflowHttpException(string code, string message, bool mayHaveExecuted = false) : Exception(message)
{
    /// <summary>不包含动态上游数据的错误分类。</summary>
    public string Code { get; } = code;
    /// <summary>未知、响应丢失及收到错误响应时保守视为 true。</summary>
    public bool MayHaveExecuted { get; } = mayHaveExecuted;
}
