using System.Text;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Features.Workflow;
using CreativeFlow.Infrastructure.Options;

namespace CreativeFlow.Infrastructure.Skills;

/// <summary>把通用编排脚本映射到既有 OpenSandbox Skill 运行器，不在 API 进程执行调用方代码。</summary>
internal sealed class OpenSandboxWorkflowScriptRunner(
    ISkillScriptRunner sandbox,
    SkillsOptions configuredOptions) : IWorkflowScriptRunner
{
    private readonly ISkillScriptRunner sandbox = sandbox;
    private readonly SkillsOptions options = configuredOptions;

    /// <summary>脚本能力开关与 Skill 沙箱使用同一部署边界。</summary>
    public bool Enabled => options.SandboxEnabled;

    /// <summary>脚本节点取沙箱设置与节点公开上限中的较小值。</summary>
    public int MaximumTimeoutSeconds => Math.Min(WorkflowScriptValues.MaxTimeoutSeconds, sandbox.MaximumTimeoutSeconds);

    /// <summary>脚本节点不允许使用 Skill 包的大上传上限。</summary>
    public int MaximumCodeBytes => WorkflowScriptValues.MaxCodeBytes;

    /// <summary>节点输入受通用字段和底层 Skill 标准输入上限共同约束。</summary>
    public int MaximumInputBytes => Math.Min(WorkflowV2Values.MaxBytes, sandbox.MaximumStdinBytes);

    /// <summary>标准输出沿用沙箱的统一输出上限，防止执行结果占满运行快照。</summary>
    public int MaximumOutputBytes => options.RunnerMaxOutputBytes;

    /// <summary>将单文件脚本和绑定输入交给沙箱；不传递环境变量、密钥或外部参数。</summary>
    public async Task<WorkflowScriptResult> RunAsync(WorkflowScriptRequest request, CancellationToken ct)
    {
        if (!Enabled) throw new InvalidOperationException("Workflow script sandbox is disabled.");
        if (request.Language is not ("python" or "javascript"))
            throw new InvalidDataException("Workflow script language is not supported.");
        var codeBytes = Encoding.UTF8.GetBytes(request.Code);
        if (codeBytes.Length == 0 || codeBytes.Length > MaximumCodeBytes || request.Code.Contains('\0'))
            throw new InvalidDataException("Workflow script code exceeds its size limit.");
        var input = request.Input.ToJsonString();
        if (Encoding.UTF8.GetByteCount(input) > MaximumInputBytes)
            throw new InvalidDataException("Workflow script input exceeds its size limit.");
        if (request.Timeout <= TimeSpan.Zero || request.Timeout > TimeSpan.FromSeconds(MaximumTimeoutSeconds))
            throw new InvalidDataException("Workflow script timeout exceeds its configured limit.");

        var scriptPath = request.Language == "python" ? "main.py" : "main.js";
        var result = await sandbox.RunAsync(
            new SkillScriptRequest(
                [new SkillPackageFile(scriptPath, codeBytes)],
                scriptPath,
                [],
                input,
                request.Timeout),
            ct);
        return new WorkflowScriptResult(
            result.Status,
            result.ExitCode,
            result.Stdout,
            result.Stderr,
            result.StdoutTruncated,
            result.StderrTruncated);
    }
}
