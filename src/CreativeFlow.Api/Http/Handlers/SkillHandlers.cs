using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using CreativeFlow.Api.Middleware;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Application.Contracts.Skill;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Infrastructure.Options;

namespace CreativeFlow.Api.Http.Handlers;

/// <summary>上传并管理设备作用域的智能体技能。</summary>
internal static class SkillHandlers
{
    /// <summary>列出Skill 包，保留现有筛选、排序与访问范围。</summary>
    internal static async Task<IActionResult> ListAsync(
        HttpContext http,
        ISkillRepository skills,
        IOptions<SkillsOptions> configuredOptions,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId) return ApiResults.Unauthorized();
        var list = await skills.ListAsync(deviceId, ct);
        return ApiResults.Ok(new SkillListResponse(
            configuredOptions.Value.SandboxEnabled,
            [.. list.Select(ToResponse)]));
    }

    /// <summary>读取Skill 包，保留缺失资源的既有处理。</summary>
    internal static async Task<IActionResult> GetAsync(
        string id,
        HttpContext http,
        ISkillRepository skills,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId) return ApiResults.Unauthorized();
        if (!SkillId.TryParse(id, out var skillId)) return Invalid("malformed skill id");
        var skill = await skills.FindAsync(deviceId, skillId, ct);
        return skill is null ? ApiResults.NotFound() : ApiResults.Ok(ToResponse(skill));
    }

    /// <summary>在当前上传限制内扫描 Skill 包结构与风险。</summary>
    internal static async Task<IActionResult> ScanAsync(
        IFormFile file,
        HttpContext http,
        ISkillPackageScanner scanner,
        IOptions<SkillsOptions> configuredOptions,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is null) return ApiResults.Unauthorized();
        if (file.Length == 0) return Invalid("the uploaded skill package is empty");
        if (file.Length > configuredOptions.Value.MaxUploadBytes)
        {
            return Invalid($"the upload exceeds {configuredOptions.Value.MaxUploadBytes} bytes");
        }

        try
        {
            await using var stream = file.OpenReadStream();
            var scanned = await scanner.ScanAsync(stream, file.FileName, ct);
            return ApiResults.Ok(ToScanResponse(scanned));
        }
        catch (Exception exception) when (exception is InvalidDataException
            or IOException or NotSupportedException)
        {
            return Invalid(Bounded(exception.Message));
        }
    }

    /// <summary>保存已经确认的 Skill 包及其元数据。</summary>
    internal static async Task<IActionResult> InstallAsync(
        IFormFile file,
        bool confirmDanger,
        HttpContext http,
        ISkillPackageScanner scanner,
        ISkillStore store,
        ISkillRepository skills,
        IOptions<SkillsOptions> configuredOptions,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId) return ApiResults.Unauthorized();
        if (file.Length == 0) return Invalid("the uploaded skill package is empty");
        if (file.Length > configuredOptions.Value.MaxUploadBytes)
        {
            return Invalid($"the upload exceeds {configuredOptions.Value.MaxUploadBytes} bytes");
        }

        ScannedSkillPackage scanned;
        try
        {
            await using var stream = file.OpenReadStream();
            // 安装时会再次扫描上传的精确字节。扫描结果只是界面提示，不是能力令牌，
            // 因此在扫描与安装之间替换文件无法绕过风险控制。
            scanned = await scanner.ScanAsync(stream, file.FileName, ct);
        }
        catch (Exception exception) when (exception is InvalidDataException
            or IOException or NotSupportedException)
        {
            return Invalid(Bounded(exception.Message));
        }

        if (scanned.MaxRisk == SkillRiskLevel.Danger && !confirmDanger)
        {
            return Invalid("dangerous skill findings require explicit confirmation");
        }
        if (await skills.FindBySlugAsync(deviceId, scanned.Slug, ct) is not null)
        {
            return ApiResults.Conflict(new ErrorResponse(
                "duplicate_skill", [$"A skill with slug '{scanned.Slug}' is already installed."]));
        }

        var id = SkillId.New();
        string storagePath;
        try
        {
            storagePath = await store.InstallAsync(deviceId, id, scanned.Files, ct);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            return Invalid(Bounded(exception.Message));
        }

        var now = DateTimeOffset.UtcNow;
        var skill = new Skill(
            id,
            deviceId,
            scanned.Slug,
            scanned.Name,
            scanned.Description,
            storagePath,
            Enabled: true,
            scanned.HasScripts,
            SerializeRisks(scanned.Risks),
            scanned.MaxRisk,
            scanned.ByteSize,
            now,
            now);

        try
        {
            if (!await skills.InsertAsync(skill, ct))
            {
                await store.DeleteAsync(storagePath, CancellationToken.None);
                return ApiResults.Conflict(new ErrorResponse(
                    "duplicate_skill", [$"A skill with slug '{scanned.Slug}' is already installed."]));
            }
        }
        catch
        {
            await store.DeleteAsync(storagePath, CancellationToken.None);
            throw;
        }

        return ApiResults.Created($"/api/v1/skills/{id}", ToResponse(skill));
    }

    /// <summary>删除Skill 包，保留不存在或不可访问时的返回语义。</summary>
    internal static async Task<IActionResult> DeleteAsync(
        string id,
        HttpContext http,
        ISkillRepository skills,
        ISkillStore store,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId) return ApiResults.Unauthorized();
        if (!SkillId.TryParse(id, out var skillId)) return Invalid("malformed skill id");
        var skill = await skills.FindAsync(deviceId, skillId, ct);
        if (skill is null) return ApiResults.NotFound();

        if (!await skills.DeleteAsync(deviceId, skillId, ct)) return ApiResults.NotFound();

        try
        {
            await store.DeleteAsync(skill.StoragePath, ct);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            // 所有权记录已经删除，因此文件无法加载或执行。保留孤立目录比保留指向缺失文件的有效记录更安全。
            loggerFactory.CreateLogger("SkillEndpoints").LogWarning(
                "Deleted Skill metadata but could not remove its storage: {Error}",
                Bounded(exception.Message));
        }

        return ApiResults.NoContent();
    }

    /// <summary>将 Skill 包扫描结果投影为当前上传预览响应。</summary>
    private static SkillScanResponse ToScanResponse(ScannedSkillPackage scanned) => new(
        scanned.Name,
        scanned.Slug,
        scanned.Description,
        scanned.HasScripts,
        ToWire(scanned.MaxRisk),
        [.. scanned.Risks.Select(ToResponse)],
        [.. scanned.Files.Select(file => file.Path)],
        scanned.ByteSize);

    /// <summary>将业务记录映射为既有 HTTP 响应字段。</summary>
    private static SkillResponse ToResponse(Skill skill) => new(
        skill.Id.ToString(),
        skill.Slug,
        skill.Name,
        skill.Description,
        skill.Enabled,
        skill.HasScripts,
        ToWire(skill.MaxRisk),
        ParseRisks(skill.RiskJson),
        skill.ByteSize,
        skill.CreatedAt.ToString("O"),
        skill.UpdatedAt.ToString("O"));

    /// <summary>将业务记录映射为既有 HTTP 响应字段。</summary>
    private static SkillRiskFindingResponse ToResponse(SkillRiskFinding finding) => new(
        finding.Code,
        ToWire(finding.Severity),
        finding.Path,
        finding.Line,
        finding.Message);

    /// <summary>保存 Skill 风险列表的既有 JSON 格式。</summary>
    private static string SerializeRisks(IReadOnlyList<SkillRiskFinding> risks)
    {
        var array = new JsonArray();
        foreach (var risk in risks)
        {
            array.Add((JsonNode)new JsonObject
            {
                ["code"] = risk.Code,
                ["severity"] = ToWire(risk.Severity),
                ["path"] = risk.Path,
                ["line"] = risk.Line,
                ["message"] = risk.Message,
            });
        }
        return array.ToJsonString();
    }

    /// <summary>从已保存的 JSON 恢复 Skill 风险项。</summary>
    private static SkillRiskFindingResponse[] ParseRisks(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return [];
            var results = new List<SkillRiskFindingResponse>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                results.Add(new SkillRiskFindingResponse(
                    ReadString(item, "code") ?? "unknown",
                    ReadString(item, "severity") ?? "warning",
                    ReadString(item, "path") ?? string.Empty,
                    item.TryGetProperty("line", out var line) && line.TryGetInt32(out var number)
                        ? number
                        : null,
                    ReadString(item, "message") ?? string.Empty));
            }
            return [.. results];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>从 JSON 或配置读取字符串字段，保留缺失及类型错误时的原有回退。</summary>
    private static string? ReadString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>将业务枚举映射为既有存储或接口字符串。</summary>
    private static string ToWire(SkillRiskLevel level) => level switch
    {
        SkillRiskLevel.Safe => "safe",
        SkillRiskLevel.Warning => "warning",
        SkillRiskLevel.Danger => "danger",
        _ => throw new ArgumentOutOfRangeException(nameof(level)),
    };

    /// <summary>按既有长度限制截断对外文本。</summary>
    private static string Bounded(string message) =>
        message.Length <= 500 ? message : message[..500] + "…";

    /// <summary>将校验失败映射为当前功能既有的错误结果，保留详情和错误分类。</summary>
    private static IActionResult Invalid(string detail) =>
        ApiResults.BadRequest(new ErrorResponse("invalid_request", [detail]));
}
