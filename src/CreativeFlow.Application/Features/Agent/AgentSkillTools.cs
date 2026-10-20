using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Abstractions.Realtime;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;
using static CreativeFlow.Application.Features.Agent.AgentMessageJson;

namespace CreativeFlow.Application.Features.Agent;

/// <summary>构造 Skill 工具契约，验证输入并按当前权限读取资源或执行已批准脚本。</summary>
public sealed class AgentSkillTools(
    ISkillRepository skills,
    ISkillStore skillStore,
    ISkillScriptRunner skillRunner)
{
    /// <summary>根据当前启用的 Skill 构造按需加载工具的参数 schema。</summary>
    internal static AgentToolDefinition SkillToolDefinition(IReadOnlyList<Skill> enabledSkills)
    {
        // 工具 schema 是提供给模型的调用契约，字段名和必填项必须与后续参数解析保持一致。
        var slugs = new JsonArray();
        foreach (var skill in enabledSkills)
        {
            slugs.Add((JsonNode)JsonValue.Create(skill.Slug));
        }
        var slugSchema = new JsonObject { ["type"] = "string" };
        if (enabledSkills.Count > 0) slugSchema["enum"] = slugs;
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["slug"] = slugSchema,
                ["path"] = new JsonObject
                {
                    ["type"] = "string",
                    ["default"] = "SKILL.md",
                },
            },
            ["required"] = new JsonArray(JsonValue.Create("slug")),
            ["additionalProperties"] = false,
        };
        var available = enabledSkills.Count == 0
            ? "No Skills are installed."
            : string.Join("\n", enabledSkills.Select(skill =>
                $"- {skill.Slug}: {skill.Name} — {skill.Description}"));
        return new AgentToolDefinition(
            "load_skill",
            "Load an installed Skill's full instructions or a named UTF-8 resource. "
            + "Use SKILL.md for the instructions. Available Skills:\n" + available,
            schema.ToJsonString());
    }

    /// <summary>根据当前 Skill 和运行器限制构造脚本执行工具 schema。</summary>
    internal static AgentToolDefinition SkillScriptToolDefinition(
        IReadOnlyList<Skill> enabledSkills,
        int maximumTimeoutSeconds)
    {
        // 脚本工具声明限制模型可提交的入口与参数，实际权限和资源边界仍由执行端再次验证。
        var slugs = new JsonArray();
        foreach (var skill in enabledSkills.Where(skill => skill.HasScripts))
        {
            slugs.Add((JsonNode)JsonValue.Create(skill.Slug));
        }
        return new AgentToolDefinition(
            "run_skill_script",
            "Run one .sh, .py, or .js script in the isolated Skill runner. Every execution requires "
            + "explicit approval. Use load_skill to inspect the Skill and its resources first.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["slug"] = new JsonObject { ["type"] = "string", ["enum"] = slugs },
                    ["scriptPath"] = new JsonObject { ["type"] = "string" },
                    ["arguments"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject { ["type"] = "string" },
                        ["maxItems"] = 64,
                    },
                    ["stdin"] = new JsonObject { ["type"] = "string", ["maxLength"] = 262_144 },
                    ["timeoutSeconds"] = new JsonObject
                    {
                        ["type"] = "integer", ["minimum"] = 1,
                        ["maximum"] = maximumTimeoutSeconds,
                    },
                },
                ["required"] = new JsonArray(
                    JsonValue.Create("slug"), JsonValue.Create("scriptPath")),
                ["additionalProperties"] = false,
            }.ToJsonString());
    }

    /// <summary>验证 Skill 脚本路径、参数、输入和超时选项。</summary>
    internal AgentSkillScriptInput ParseSkillScriptInput(
        IReadOnlyList<Skill> enabledSkills,
        string inputJson)
    {
        // 模型生成的工具参数不能视为可信配置；解析失败返回现有错误，避免把异常输入交给沙箱。
        try
        {
            using var document = JsonDocument.Parse(inputJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return AgentSkillScriptInput.Failed("invalid_skill_script_input");
            }
            var slug = Read(root, "slug");
            var scriptPath = Read(root, "scriptPath");
            if (string.IsNullOrWhiteSpace(slug) || string.IsNullOrWhiteSpace(scriptPath))
            {
                return AgentSkillScriptInput.Failed("skill_slug_and_script_required");
            }
            var skill = enabledSkills.FirstOrDefault(candidate =>
                candidate.HasScripts && string.Equals(candidate.Slug, slug, StringComparison.Ordinal));
            if (skill is null) return AgentSkillScriptInput.Failed("skill_not_found_or_disabled");

            var arguments = new List<string>();
            if (root.TryGetProperty("arguments", out var values))
            {
                if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() > 64)
                {
                    return AgentSkillScriptInput.Failed("invalid_skill_script_arguments");
                }
                foreach (var value in values.EnumerateArray())
                {
                    if (value.ValueKind != JsonValueKind.String
                        || value.GetString() is not { Length: <= 4096 } argument)
                    {
                        return AgentSkillScriptInput.Failed("invalid_skill_script_arguments");
                    }
                    arguments.Add(argument);
                }
            }
            var stdin = Read(root, "stdin");
            if (stdin is not null && Encoding.UTF8.GetByteCount(stdin) > 256 * 1024)
            {
                return AgentSkillScriptInput.Failed("skill_script_stdin_too_large");
            }
            var timeout = Math.Min(30, skillRunner.MaximumTimeoutSeconds);
            if (root.TryGetProperty("timeoutSeconds", out var timeoutValue))
            {
                if (!timeoutValue.TryGetInt32(out timeout)
                    || timeout < 1 || timeout > skillRunner.MaximumTimeoutSeconds)
                {
                    return AgentSkillScriptInput.Failed("invalid_skill_script_timeout");
                }
            }
            return new AgentSkillScriptInput(skill, scriptPath, arguments, stdin, timeout, null);
        }
        catch (JsonException)
        {
            return AgentSkillScriptInput.Failed("invalid_skill_script_input");
        }
    }

    /// <summary>在已批准且已验证的输入上调用隔离脚本运行器。</summary>
    internal async Task<AgentCanvasToolResult> RunSkillScriptAsync(
        DeviceId deviceId,
        AgentSkillScriptInput input,
        CancellationToken ct)
    {
        // 脚本执行与结果包装沿用当前审批后的路径，结果需要同时保留失败状态和受限输出。
        if (!await skillRunner.IsAvailableAsync(ct))
        {
            return SkillError("skill_script_execution_disabled");
        }

        var skill = await skills.FindAsync(deviceId, input.Skill!.Id, ct);
        if (skill is null || !skill.Enabled || !skill.HasScripts)
        {
            return SkillError("skill_not_found_or_disabled");
        }
        try
        {
            var files = await skillStore.ReadPackageAsync(
                skill.StoragePath,
                skillRunner.MaximumFileCount,
                skillRunner.MaximumPackageBytes,
                ct);
            var result = await skillRunner.RunAsync(
                new SkillScriptRequest(
                    files, input.ScriptPath, input.Arguments, input.Stdin,
                    TimeSpan.FromSeconds(input.TimeoutSeconds)),
                ct);
            return new AgentCanvasToolResult(
                new JsonObject
                {
                    ["status"] = result.Status,
                    ["exitCode"] = result.ExitCode,
                    ["stdout"] = result.Stdout,
                    ["stderr"] = result.Stderr,
                    ["stdoutTruncated"] = result.StdoutTruncated,
                    ["stderrTruncated"] = result.StderrTruncated,
                }.ToJsonString(),
                IsError: !string.Equals(result.Status, "succeeded", StringComparison.Ordinal));
        }
        catch (Exception exception) when (exception is InvalidDataException
            or IOException or HttpRequestException or InvalidOperationException
            or TaskCanceledException)
        {
            return SkillError(exception is TaskCanceledException
                ? "skill_script_timed_out"
                : "skill_runner_unavailable");
        }
    }

    /// <summary>读取已安装 Skill 的说明或资源并返回有界工具结果。</summary>
    internal async Task<AgentCanvasToolResult> LoadSkillAsync(
        DeviceId deviceId,
        IReadOnlyList<Skill> enabledSkills,
        string inputJson,
        CancellationToken ct)
    {
        string? slug;
        string path;
        try
        {
            using var document = JsonDocument.Parse(inputJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return SkillError("invalid_skill_input");
            }
            slug = Read(document.RootElement, "slug");
            path = Read(document.RootElement, "path") ?? "SKILL.md";
        }
        catch (JsonException)
        {
            return SkillError("invalid_skill_input");
        }

        if (string.IsNullOrWhiteSpace(slug)) return SkillError("skill_slug_required");
        var skill = enabledSkills.FirstOrDefault(candidate =>
            string.Equals(candidate.Slug, slug, StringComparison.Ordinal));
        if (skill is null) return SkillError("skill_not_found_or_disabled");

        // 按作用域标识重新读取，防止运行开始时捕获的过期元数据跨越所有权边界。
        skill = await skills.FindAsync(deviceId, skill.Id, ct);
        if (skill is null || !skill.Enabled) return SkillError("skill_not_found_or_disabled");

        try
        {
            var content = await skillStore.ReadTextAsync(
                skill.StoragePath, path, maxBytes: 256 * 1024, ct);
            if (string.Equals(path, "SKILL.md", StringComparison.OrdinalIgnoreCase))
            {
                content = StripFrontmatter(content);
            }
            var fileNames = await skillStore.ListFilesAsync(skill.StoragePath, 1_000, ct);
            var files = new JsonArray();
            foreach (var fileName in fileNames)
            {
                files.Add((JsonNode)JsonValue.Create(fileName));
            }
            return new AgentCanvasToolResult(
                new JsonObject
                {
                    ["status"] = "ok",
                    ["slug"] = skill.Slug,
                    ["name"] = skill.Name,
                    ["path"] = path,
                    ["content"] = content,
                    ["files"] = files,
                }.ToJsonString(), IsError: false);
        }
        catch (Exception exception) when (exception is InvalidDataException
            or IOException or UnauthorizedAccessException)
        {
            return SkillError(exception is FileNotFoundException
                ? "skill_resource_not_found"
                : "skill_resource_unreadable");
        }
    }

    /// <summary>以现有工具协议包装 Skill 读取或执行错误。</summary>
    internal static AgentCanvasToolResult SkillError(string error) => new(
        new JsonObject { ["status"] = "error", ["error"] = error }.ToJsonString(),
        IsError: true);

    /// <summary>从 Skill 说明中分离 YAML frontmatter 与正文。</summary>
    private static string StripFrontmatter(string markdown)
    {
        using var reader = new StringReader(markdown);
        if (!string.Equals(reader.ReadLine()?.Trim(), "---", StringComparison.Ordinal))
        {
            return markdown;
        }
        while (reader.ReadLine() is { } line)
        {
            if (string.Equals(line.Trim(), "---", StringComparison.Ordinal))
            {
                return reader.ReadToEnd().Trim();
            }
        }
        return markdown;
    }

}

/// <param name="Skill">当前安装或加载的 Skill 记录。</param>
/// <param name="ScriptPath">Skill 包内待执行脚本的相对路径。</param>
/// <param name="Arguments">调用脚本或工具时传入的参数。</param>
/// <param name="Stdin">传给外部脚本的标准输入。</param>
/// <param name="TimeoutSeconds">脚本或网络操作超时，单位为秒。</param>
/// <param name="Error">失败详情或错误分类，具体格式沿用所属契约。</param>
internal sealed record AgentSkillScriptInput(
    Skill? Skill,
    string ScriptPath,
    IReadOnlyList<string> Arguments,
    string? Stdin,
    int TimeoutSeconds,
    string? Error)
{
    /// <summary>构造当前认证、执行或协议处理的失败结果。</summary>
    public static AgentSkillScriptInput Failed(string error) =>
        new(null, string.Empty, [], null, 0, error);
}
