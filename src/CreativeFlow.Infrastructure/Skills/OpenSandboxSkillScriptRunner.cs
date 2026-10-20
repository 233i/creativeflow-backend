using System.Text;
using Microsoft.Extensions.Options;
using OpenSandbox;
using OpenSandbox.Config;
using OpenSandbox.Models;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Infrastructure.Options;

namespace CreativeFlow.Infrastructure.Skills;

/// <summary>在短生命周期的 OpenSandbox 实例中运行已批准的技能脚本。</summary>
internal sealed class OpenSandboxSkillScriptRunner(
    IOptions<SkillsOptions> configuredOptions) : ISkillScriptRunner
{
    /// <summary>当前组件使用的已绑定配置。</summary>
    private readonly SkillsOptions options = configuredOptions.Value;

    /// <summary>返回配置允许的 Skill 脚本最长执行秒数。</summary>
    public int MaximumTimeoutSeconds => options.RunnerTimeoutSeconds;
    /// <summary>返回执行器、上传和解包限制共同允许的最小包体字节上限。</summary>
    public int MaximumPackageBytes => Math.Min(
        options.RunnerMaxPackageBytes,
        Math.Min(options.MaxUploadBytes, options.MaxExtractedBytes));
    /// <summary>返回配置允许传入沙箱的最大包内文件数量。</summary>
    public int MaximumFileCount => options.MaxEntries;
    /// <summary>标准输入与既有 Skill 执行契约保持 256 KiB 上限。</summary>
    public int MaximumStdinBytes => 256 * 1024;

    /// <summary>在两秒请求超时内探测 OpenSandbox 健康端点，连接失败时返回不可用。</summary>
    public async Task<bool> IsAvailableAsync(CancellationToken ct)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        try
        {
            using var response = await client.GetAsync($"http://{options.OpenSandboxDomain}/health", ct);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
    }

    /// <summary>校验脚本请求后创建隔离沙箱、传输包并执行，在取消、超时或完成后释放运行环境。</summary>
    public async Task<SkillScriptResult> RunAsync(SkillScriptRequest request, CancellationToken ct)
    {
        // 沙箱创建、包传输、执行与释放共享取消和超时边界；失败后仍需清理已创建的运行环境。
        Validate(request);
        var connection = new ConnectionConfig(new ConnectionConfigOptions
        {
            Domain = options.OpenSandboxDomain,
            ApiKey = options.OpenSandboxApiKey,
            RequestTimeoutSeconds = request.TimeoutSeconds(),
        });
        await using var sandbox = await Sandbox.CreateAsync(new SandboxCreateOptions
        {
            ConnectionConfig = connection,
            Image = options.OpenSandboxImage,
            TimeoutSeconds = request.TimeoutSeconds(),
            NetworkPolicy = new NetworkPolicy { DefaultAction = NetworkRuleAction.Deny },
        }, ct);

        try
        {
            await sandbox.Files.WriteFilesAsync(
                request.Files.Select(file => new WriteEntry
                {
                    Path = $"/workspace/{file.Path}",
                    Data = file.Bytes,
                    Mode = 600,
                }), ct);
            if (request.Stdin is not null)
            {
                await sandbox.Files.WriteFilesAsync(
                    [new WriteEntry { Path = "/workspace/.creativeflow-stdin", Data = request.Stdin, Mode = 600 }],
                    ct);
            }

            var execution = await sandbox.Commands.RunAsync(
                BuildCommand(request),
                new RunCommandOptions
                {
                    WorkingDirectory = "/workspace",
                    TimeoutSeconds = request.TimeoutSeconds(),
                },
                handlers: null,
                ct);
            var stdout = Truncate(string.Concat(execution.Logs.Stdout.Select(message => message.Text)));
            var stderr = Truncate(string.Concat(execution.Logs.Stderr.Select(message => message.Text)));
            var succeeded = execution.ExitCode == 0;
            return new SkillScriptResult(
                succeeded ? "succeeded" : "failed",
                execution.ExitCode,
                stdout.Value,
                stderr.Value,
                stdout.Truncated,
                stderr.Truncated);
        }
        finally
        {
            await sandbox.KillAsync(ct);
        }
    }

    /// <summary>根据已验证的脚本类型构造隔离执行命令。</summary>
    private string BuildCommand(SkillScriptRequest request)
    {
        var interpreter = Path.GetExtension(request.ScriptPath).ToLowerInvariant() switch
        {
            ".sh" => "sh",
            ".py" => "python3",
            ".js" => "node",
            _ => throw new InvalidDataException("Unsupported Skill script type."),
        };
        var arguments = string.Join(' ', request.Arguments.Select(ShellQuote));
        var command = $"{interpreter} {ShellQuote(request.ScriptPath)}";
        if (arguments.Length > 0) command += $" {arguments}";
        return request.Stdin is null ? command : $"{command} < .creativeflow-stdin";
    }

    /// <summary>在创建沙箱前检查包大小、脚本路径、参数和执行超时。</summary>
    private void Validate(SkillScriptRequest request)
    {
        // 创建沙箱之前先验证脚本入口、参数、包大小及资源限制，避免无效输入消耗远端执行资源。
        if (request.Files.Count == 0 || request.Files.Count > options.MaxEntries)
        {
            throw new InvalidDataException("The Skill execution package has an invalid file count.");
        }
        if (request.Files.Sum(file => (long)file.Bytes.Length) > MaximumPackageBytes)
        {
            throw new InvalidDataException("The Skill execution package exceeds its size limit.");
        }
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in request.Files)
        {
            if (!paths.Add(ValidatePath(file.Path)))
            {
                throw new InvalidDataException("The Skill execution package contains duplicate paths.");
            }
        }
        var scriptPath = ValidatePath(request.ScriptPath);
        if (!paths.Contains(scriptPath)) throw new FileNotFoundException("Skill script not found.");
        if (Path.GetExtension(scriptPath).ToLowerInvariant() is not (".sh" or ".py" or ".js"))
        {
            throw new InvalidDataException("Only .sh, .py, and .js Skill scripts can run.");
        }
        if (request.Arguments.Count > 64 || request.Arguments.Any(argument => argument.Length > 4096))
        {
            throw new InvalidDataException("Skill script arguments exceed their limit.");
        }
        if (request.Stdin is { } stdin && Encoding.UTF8.GetByteCount(stdin) > MaximumStdinBytes)
        {
            throw new InvalidDataException("Skill script stdin exceeds 256 KiB.");
        }
        if (request.Timeout <= TimeSpan.Zero || request.Timeout > TimeSpan.FromSeconds(MaximumTimeoutSeconds))
        {
            throw new InvalidDataException("Skill script timeout exceeds the configured limit.");
        }
    }

    /// <summary>验证脚本执行所需路径位于允许的包范围。</summary>
    private static string ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || Path.IsPathRooted(path))
        {
            throw new InvalidDataException($"Invalid Skill path '{path}'.");
        }
        var segments = path.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."
                || segment.Length > 255 || segment.IndexOf('\0') >= 0))
        {
            throw new InvalidDataException($"Unsafe Skill path '{path}'.");
        }
        return path;
    }

    /// <summary>按当前组件的既有上限截断文本。</summary>
    private (string Value, bool Truncated) Truncate(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length <= options.RunnerMaxOutputBytes) return (value, false);
        return (Encoding.UTF8.GetString(bytes, 0, options.RunnerMaxOutputBytes), true);
    }

    /// <summary>将单个已验证参数编码为 shell 安全的字面量。</summary>
    private static string ShellQuote(string value) => $"'{value.Replace("'", "'\\\"'\\\"'", StringComparison.Ordinal)}'";
}

internal static class SkillScriptRequestExtensions
{
    /// <summary>将请求超时限制在运行器已经配置的允许范围。</summary>
    public static int TimeoutSeconds(this SkillScriptRequest request) =>
        checked((int)Math.Ceiling(request.Timeout.TotalSeconds));
}
