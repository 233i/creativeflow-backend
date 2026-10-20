using System.Text;
using Microsoft.Extensions.Options;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Infrastructure.Options;
using CreativeFlow.Infrastructure.Skills;

namespace CreativeFlow.UnitTests.Ai;

public sealed class SkillScriptRunnerTests
{
    /// <summary>回归场景：Disabled runner never executes scripts。</summary>
    [Fact]
    public async Task Disabled_runner_never_executes_scripts()
    {
        var runner = new DisabledSkillScriptRunner(new SkillsOptions());

        Assert.False(await runner.IsAvailableAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(
            new SkillScriptRequest([], "script.py", [], null, TimeSpan.FromSeconds(1)),
            CancellationToken.None));
    }

    /// <summary>回归场景：Missing socket reports unavailable。</summary>
    [Fact]
    public async Task Missing_socket_reports_unavailable()
    {
        var runner = Runner();

        Assert.False(await runner.IsAvailableAsync(CancellationToken.None));
    }

    /// <summary>回归场景：Unsafe or unsupported script paths are rejected。</summary>
    [Theory]
    [InlineData("../script.py")]
    [InlineData("/script.py")]
    [InlineData("script.exe")]
    public async Task Unsafe_or_unsupported_script_paths_are_rejected(string scriptPath)
    {
        var runner = Runner();
        var request = new SkillScriptRequest(
            [new SkillPackageFile(scriptPath, Encoding.UTF8.GetBytes("print('unsafe')"))],
            scriptPath,
            [],
            null,
            TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            runner.RunAsync(request, CancellationToken.None));
    }

    /// <summary>回归场景：Script must belong to the submitted package。</summary>
    [Fact]
    public async Task Script_must_belong_to_the_submitted_package()
    {
        var runner = Runner();
        var request = new SkillScriptRequest(
            [new SkillPackageFile("SKILL.md", Encoding.UTF8.GetBytes("instructions"))],
            "script.py",
            [],
            null,
            TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            runner.RunAsync(request, CancellationToken.None));
    }

    /// <summary>回归场景：Timeout cannot exceed configured limit。</summary>
    [Fact]
    public async Task Timeout_cannot_exceed_configured_limit()
    {
        var runner = Runner();
        var request = new SkillScriptRequest(
            [new SkillPackageFile("script.py", Encoding.UTF8.GetBytes("print('ok')"))],
            "script.py",
            [],
            null,
            TimeSpan.FromSeconds(31));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            runner.RunAsync(request, CancellationToken.None));
    }

    /// <summary>组装使用测试 HTTP 响应的沙箱脚本运行器。</summary>
    private static OpenSandboxSkillScriptRunner Runner() => new(
        Microsoft.Extensions.Options.Options.Create(new SkillsOptions
        {
            OpenSandboxDomain = "localhost:8090",
            RunnerTimeoutSeconds = 30,
            RunnerMaxOutputBytes = 256 * 1024,
            MaxEntries = 100,
            MaxExtractedBytes = 2 * 1024 * 1024,
        }));
}
