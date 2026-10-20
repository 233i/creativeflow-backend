namespace CreativeFlow.Application.Abstractions.Ai;

/// <summary>在 CreativeFlow.Api 信任边界之外执行已安装的技能脚本。</summary>
public interface ISkillScriptRunner
{
    /// <summary>脚本运行器接受的最大超时秒数。</summary>
    int MaximumTimeoutSeconds { get; }
    /// <summary>运行器接受的最大包字节数。</summary>
    int MaximumPackageBytes { get; }
    /// <summary>运行器接受的最大包文件数量。</summary>
    int MaximumFileCount { get; }
    /// <summary>运行器接受的标准输入最大字节数。</summary>
    int MaximumStdinBytes { get; }

    /// <summary>检查当前脚本运行器是否可用。</summary>
    Task<bool> IsAvailableAsync(CancellationToken ct);

    /// <summary>在既有隔离和资源边界下执行调用方已经批准的 Skill 脚本。</summary>
    Task<SkillScriptResult> RunAsync(SkillScriptRequest request, CancellationToken ct);
}

/// <param name="Files">包内文件或读取结果集合。</param>
/// <param name="ScriptPath">Skill 包内待执行脚本的相对路径。</param>
/// <param name="Arguments">调用脚本或工具时传入的参数。</param>
/// <param name="Stdin">传给外部脚本的标准输入。</param>
/// <param name="Timeout">操作允许等待的时长。</param>
public sealed record SkillScriptRequest(
    IReadOnlyList<SkillPackageFile> Files,
    string ScriptPath,
    IReadOnlyList<string> Arguments,
    string? Stdin,
    TimeSpan Timeout);

/// <param name="Status">当前对象的业务状态，存储和接口名称保持既有映射。</param>
/// <param name="ExitCode">外部进程返回的退出码。</param>
/// <param name="Stdout">外部进程的标准输出。</param>
/// <param name="Stderr">外部进程的标准错误输出。</param>
/// <param name="StdoutTruncated">标准输出是否因长度限制被截断。</param>
/// <param name="StderrTruncated">标准错误是否因长度限制被截断。</param>
public sealed record SkillScriptResult(
    string Status,
    int? ExitCode,
    string Stdout,
    string Stderr,
    bool StdoutTruncated,
    bool StderrTruncated);
