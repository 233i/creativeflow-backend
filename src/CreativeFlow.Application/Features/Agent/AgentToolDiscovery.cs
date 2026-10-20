using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Application.Features.Agent;

/// <summary>为一次运行捕获工具目录，保留发现顺序、可见名称和各 MCP 服务的失败隔离。</summary>
public sealed class AgentToolDiscovery(
    ISkillRepository skills,
    ISkillScriptRunner skillRunner,
    IMcpServerRepository mcpServers,
    IMcpRuntime mcpRuntime)
{
    /// <summary>仅加载当前设备范围内启用的 Skill 和 MCP 工具，脚本不可用时不向模型声明。</summary>
    internal async Task<AgentToolSet> PrepareAsync(AgentRun run, CancellationToken ct)
    {
        var installedSkills = await skills.ListAsync(run.DeviceId, ct);
        var enabledSkills = installedSkills.Where(skill => skill.Enabled).ToList();
        var scriptRunnerAvailable = enabledSkills.Any(skill => skill.HasScripts)
            && await skillRunner.IsAvailableAsync(ct);
        var tools = AgentToolCatalog.All
            .Where(tool => tool.Name != "run_skill_script" || scriptRunnerAvailable)
            .Select(tool => tool.Name switch
            {
                "load_skill" => AgentSkillTools.SkillToolDefinition(enabledSkills),
                "run_skill_script" => AgentSkillTools.SkillScriptToolDefinition(
                    enabledSkills, skillRunner.MaximumTimeoutSeconds),
                _ => tool.ToModelDefinition(),
            })
            .ToList();
        var mcpBindings = await LoadMcpToolsAsync(run, tools, ct);
        return new AgentToolSet(tools, enabledSkills, mcpBindings);
    }

    /// <summary>发现已启用 MCP 服务的工具并建立运行内名称绑定。</summary>
    private async Task<Dictionary<string, AgentMcpBinding>> LoadMcpToolsAsync(
        AgentRun run,
        List<AgentToolDefinition> definitions,
        CancellationToken ct)
    {
        // 各 MCP 服务器独立加载工具目录；保持当前失败处理与工具命名规则，避免不同服务器的工具互相覆盖。
        var bindings = new Dictionary<string, AgentMcpBinding>(StringComparer.Ordinal);
        var servers = await mcpServers.ListAsync(run.DeviceId, ct);
        foreach (var server in servers.Where(candidate =>
                     candidate.Enabled
                     && candidate.Transport is McpTransport.Sse or McpTransport.StreamableHttp))
        {
            IReadOnlyList<McpRuntimeTool> discovered;
            try
            {
                discovered = await mcpRuntime.ListToolsAsync(server, ct);
            }
            catch (Exception exception) when (exception is InvalidOperationException
                or NotSupportedException or HttpRequestException or TaskCanceledException)
            {
                continue;
            }

            foreach (var tool in discovered)
            {
                var visible = McpToolName(server.Id, tool.Name);
                if (bindings.ContainsKey(visible)) continue;
                bindings[visible] = new AgentMcpBinding(server, tool.Name);
                definitions.Add(new AgentToolDefinition(
                    visible,
                    $"MCP server '{server.Name}': {tool.Description}",
                    tool.InputSchemaJson));
            }
        }
        return bindings;
    }

    /// <summary>将 MCP 服务标识与原工具名编码为模型侧稳定名称。</summary>
    private static string McpToolName(McpServerId serverId, string original)
    {
        var sanitized = new string(original.Select(character =>
                char.IsAsciiLetterOrDigit(character) || character == '_' ? character : '_')
            .ToArray()).Trim('_');
        if (sanitized.Length == 0) sanitized = "tool";
        if (sanitized.Length > 38) sanitized = sanitized[..38];
        return $"mcp__{serverId.Value:N}"[..13] + $"__{sanitized}";
    }

}

/// <param name="Definitions">按原目录顺序发送给模型的工具定义。</param>
/// <param name="EnabledSkills">运行开始时的可用 Skill 快照，执行端仍按原规则重新验证。</param>
/// <param name="McpBindings">模型侧名称到服务端原始工具的运行内绑定。</param>
internal sealed record AgentToolSet(
    IReadOnlyList<AgentToolDefinition> Definitions,
    IReadOnlyList<Skill> EnabledSkills,
    IReadOnlyDictionary<string, AgentMcpBinding> McpBindings);

/// <param name="Server">当前 MCP 服务配置。</param>
/// <param name="OriginalName">MCP 服务端原始工具名，区别于组合后的模型工具名。</param>
internal sealed record AgentMcpBinding(McpServer Server, string OriginalName);
