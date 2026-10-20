using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;

namespace CreativeFlow.UnitTests.Support;

/// <summary>记录 MCP 与 Skill 边界的纯内存替身，不连接网络、不启动脚本。</summary>
internal sealed class AgentToolPorts : IMcpRuntime, ISkillStore, ISkillScriptRunner
{
    /// <summary>模型侧目录发现的服务顺序。</summary>
    public List<McpServer> Discovered { get; } = [];
    /// <summary>实际 MCP 调用及未经改写的参数。</summary>
    public List<(McpServer Server, string Name, string Json)> Calls { get; } = [];
    /// <summary>可按服务模拟发现失败或目录冲突。</summary>
    public Func<McpServer, IReadOnlyList<McpRuntimeTool>> Discover { get; set; } = _ =>
        [new("lookup.item", "Synthetic lookup", "{\"type\":\"object\"}")];
    /// <summary>执行边界的结果或异常，调用前先记录一次。</summary>
    public Func<McpRuntimeResult> Call { get; set; } = () => new("{\"value\":42}", false);
    /// <summary>脚本执行请求的顺序快照。</summary>
    public List<SkillScriptRequest> Scripts { get; } = [];
    /// <summary>读取的文本资源路径。</summary>
    public List<string> TextReads { get; } = [];
    /// <summary>执行脚本前读取包的次数。</summary>
    public int PackageReads { get; private set; }
    /// <summary>模拟运行器可用性在审批前后变化。</summary>
    public bool Available { get; set; } = true;
    /// <summary>合成脚本只接受既有超时上限。</summary>
    public int MaximumTimeoutSeconds => 60;
    /// <summary>测试包的最大字节数。</summary>
    public int MaximumPackageBytes => 4096;
    /// <summary>测试包的最大文件数。</summary>
    public int MaximumFileCount => 8;
    /// <summary>测试运行器的标准输入上限。</summary>
    public int MaximumStdinBytes => 256 * 1024;
    /// <summary>脚本运行结果可模拟超时和基础设施错误。</summary>
    public Func<SkillScriptResult> RunScript { get; set; } = () => new("succeeded", 0, "ok", "", true, false);

    /// <summary>保存发现顺序并返回合成目录。</summary>
    public Task<IReadOnlyList<McpRuntimeTool>> ListToolsAsync(McpServer server, CancellationToken ct)
    {
        Discovered.Add(server);
        return Task.FromResult(Discover(server));
    }

    /// <summary>记录一次真实执行边界后返回结果；替身不隐式重试。</summary>
    public Task<McpRuntimeResult> CallToolAsync(McpServer server, string toolName, string argumentsJson, CancellationToken ct)
    {
        Calls.Add((server, toolName, argumentsJson));
        return Task.FromResult(Call());
    }

    /// <summary>按开关返回脚本运行器可用性。</summary>
    public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(Available);

    /// <summary>只记录脚本参数，不执行文件。</summary>
    public Task<SkillScriptResult> RunAsync(SkillScriptRequest request, CancellationToken ct)
    {
        Scripts.Add(request);
        return Task.FromResult(RunScript());
    }

    /// <summary>拒绝运行器测试中意外安装新 Skill。</summary>
    public Task<string> InstallAsync(DeviceId deviceId, SkillId skillId, IReadOnlyList<SkillPackageFile> files, CancellationToken ct) =>
        throw new NotSupportedException();

    /// <summary>返回带 frontmatter 的合成说明，并验证原读取限制。</summary>
    public Task<string> ReadTextAsync(string storagePath, string relativePath, int maxBytes, CancellationToken ct)
    {
        Assert.Equal(256 * 1024, maxBytes);
        TextReads.Add(relativePath);
        return Task.FromResult("---\nname: synthetic\n---\nRead this instruction.");
    }

    /// <summary>返回固定文件目录并验证数量上限。</summary>
    public Task<IReadOnlyList<string>> ListFilesAsync(string storagePath, int maxEntries, CancellationToken ct)
    {
        Assert.Equal(1000, maxEntries);
        return Task.FromResult<IReadOnlyList<string>>(["SKILL.md", "scripts/test.py"]);
    }

    /// <summary>验证审批后按运行器资源限制读取一次包。</summary>
    public Task<IReadOnlyList<SkillPackageFile>> ReadPackageAsync(string storagePath, int maxEntries, int maxBytes, CancellationToken ct)
    {
        Assert.Equal(MaximumFileCount, maxEntries);
        Assert.Equal(MaximumPackageBytes, maxBytes);
        PackageReads++;
        return Task.FromResult<IReadOnlyList<SkillPackageFile>>([new("scripts/test.py", [1, 2, 3])]);
    }

    /// <summary>工具运行不能删除 Skill 安装内容。</summary>
    public Task DeleteAsync(string storagePath, CancellationToken ct) => throw new NotSupportedException();
}
