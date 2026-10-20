using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Infrastructure.Options;

namespace CreativeFlow.Infrastructure.Skills;

/// <summary>未配置隔离沙箱时拒绝执行技能脚本。</summary>
internal sealed class DisabledSkillScriptRunner(SkillsOptions options) : ISkillScriptRunner
{
    /// <summary>暴露配置允许的脚本最长执行秒数，但不表示当前已启用执行器。</summary>
    public int MaximumTimeoutSeconds => options.RunnerTimeoutSeconds;
    /// <summary>返回执行器、上传和解包限制共同允许的最小包体字节上限。</summary>
    public int MaximumPackageBytes => Math.Min(
        options.RunnerMaxPackageBytes,
        Math.Min(options.MaxUploadBytes, options.MaxExtractedBytes));
    /// <summary>返回配置允许的最大包内文件数量。</summary>
    public int MaximumFileCount => options.MaxEntries;
    /// <summary>保持禁用实现与实际沙箱的标准输入契约一致。</summary>
    public int MaximumStdinBytes => 256 * 1024;

    /// <summary>明确返回沙箱执行器不可用，不探测或启动外部服务。</summary>
    public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(false);

    /// <summary>拒绝执行 Skill 脚本并返回沙箱未启用的异常，不回退到宿主执行。</summary>
    public Task<SkillScriptResult> RunAsync(SkillScriptRequest request, CancellationToken ct) =>
        Task.FromException<SkillScriptResult>(new InvalidOperationException(
            "Skill script execution requires an enabled sandbox."));
}
