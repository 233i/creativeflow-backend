using System.Text.Json.Nodes;

namespace CreativeFlow.Application.Abstractions.Ai;

/// <summary>通用编排脚本节点的受限执行端口；实现必须在独立沙箱中运行，不得回退到 API 进程。</summary>
public interface IWorkflowScriptRunner
{
    /// <summary>当前部署是否允许脚本节点执行；关闭时编辑器也不会公布该节点。</summary>
    bool Enabled { get; }

    /// <summary>单次脚本执行允许的最长秒数。</summary>
    int MaximumTimeoutSeconds { get; }

    /// <summary>脚本源码允许的最大 UTF-8 字节数。</summary>
    int MaximumCodeBytes { get; }

    /// <summary>传给脚本标准输入的 JSON 最大 UTF-8 字节数。</summary>
    int MaximumInputBytes { get; }

    /// <summary>脚本标准输出允许的最大 UTF-8 字节数。</summary>
    int MaximumOutputBytes { get; }

    /// <summary>执行一段只读 JSON 转换脚本；脚本只通过 stdin/stdout 与节点交互。</summary>
    Task<WorkflowScriptResult> RunAsync(WorkflowScriptRequest request, CancellationToken ct);
}

/// <param name="Language">受支持的脚本语言，当前为 python 或 javascript。</param>
/// <param name="Code">脚本源码；脚本从 stdin 读取 JSON 对象并向 stdout 输出一个 JSON 对象。</param>
/// <param name="Input">已经按节点输入字段契约绑定的输入对象。</param>
/// <param name="Timeout">本次执行的超时时间。</param>
public sealed record WorkflowScriptRequest(
    string Language,
    string Code,
    JsonObject Input,
    TimeSpan Timeout);

/// <param name="Status">沙箱进程状态，成功时为 succeeded。</param>
/// <param name="ExitCode">沙箱进程退出码。</param>
/// <param name="Stdout">脚本标准输出。</param>
/// <param name="Stderr">脚本标准错误输出，仅用于受限诊断。</param>
/// <param name="StdoutTruncated">标准输出是否达到沙箱输出上限。</param>
/// <param name="StderrTruncated">标准错误是否达到沙箱输出上限。</param>
public sealed record WorkflowScriptResult(
    string Status,
    int? ExitCode,
    string Stdout,
    string Stderr,
    bool StdoutTruncated,
    bool StderrTruncated);
