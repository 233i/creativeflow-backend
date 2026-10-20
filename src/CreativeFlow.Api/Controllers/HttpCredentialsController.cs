using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using CreativeFlow.Api.Middleware;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Identity;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Abstractions.Security;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Application.Features.Workflow;
using CreativeFlow.Domain.Ai;
using static CreativeFlow.Application.Features.Workflow.WorkflowV2Values;

namespace CreativeFlow.Api.Controllers;

/// <summary>管理员维护HTTP 认证凭据；普通用户只能读取已授权凭据的字段目录。</summary>
[ApiController]
[Tags("http-credentials")]
public sealed class HttpCredentialsController(IHttpCredentialRepository repository, HttpRequestService service, IApiKeyProtector protector, IAccountContext accounts) : ControllerBase
{
    /// <summary>普通账号目录提供已授权凭据的站点、版本和头名称，不返回秘密值。</summary>
    [HttpGet("api/v1/http-credentials/catalog", Name = "HttpCredentialCatalog")]
    public async Task<IActionResult> Catalog(CancellationToken ct)
    {
        NoCache();
        if (accounts.AccountId is not { } account || HttpContext.GetDeviceId() is not { } device) return Unauthorized();
        return !service.Enabled ? NotFound() : Ok(await service.CatalogAsync(new("account", account.Value, device.Value), ct));
    }

    /// <summary>管理员编辑器也使用不含连接凭据的安全目录。</summary>
    [HttpGet("api/v1/admin/http-credentials/catalog", Name = "AdminHttpCredentialCatalog")]
    public async Task<IActionResult> AdminCatalog(CancellationToken ct)
    {
        NoCache();
        if (HttpContext.GetAuthenticatedAdmin() is not { } admin) return Unauthorized();
        return !service.Enabled ? NotFound() : Ok(await service.CatalogAsync(new("admin", admin.Id, Guid.Empty), ct));
    }

    /// <summary>管理页列表包含状态和当前版本，不包含任何鉴权材料。</summary>
    [HttpGet("api/v1/admin/http-credentials", Name = "ListHttpCredentials")]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        NoCache();
        if (HttpContext.GetAuthenticatedAdmin() is null) return Unauthorized();
        return !service.Enabled ? NotFound() : Ok(await repository.ListAsync(ct));
    }

    /// <summary>管理员读取不可变配置版本，密钥只能看到是否保存和遮盖提示。</summary>
    [HttpGet("api/v1/admin/http-credentials/{id:guid}", Name = "GetHttpCredential")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        NoCache();
        if (HttpContext.GetAuthenticatedAdmin() is null) return Unauthorized();
        return !service.Enabled || await repository.DetailsAsync(id, ct) is not { } detail ? NotFound() : Ok(detail);
    }

    /// <summary>创建凭据首版，不发送业务请求。</summary>
    [HttpPost("api/v1/admin/http-credentials", Name = "CreateHttpCredential")]
    [RequestSizeLimit(49152)]
    public Task<IActionResult> Create([FromBody] JsonObject request, CancellationToken ct) => SaveAsync(Guid.CreateVersion7(), request, create: true, ct);

    /// <summary>更新始终追加版本，不影响已运行和已发布工作流引用的旧配置。</summary>
    [HttpPut("api/v1/admin/http-credentials/{id:guid}", Name = "SaveHttpCredentialRevision")]
    [RequestSizeLimit(49152)]
    public Task<IActionResult> Save(Guid id, [FromBody] JsonObject request, CancellationToken ct) => SaveAsync(id, request, create: false, ct);

    /// <summary>立即停用或恢复凭据；停用不删除固定版本，但执行和结果读取会重新检查状态。</summary>
    [HttpPut("api/v1/admin/http-credentials/{id:guid}/state", Name = "SetHttpCredentialState")]
    [RequestSizeLimit(1024)]
    public async Task<IActionResult> State(Guid id, [FromBody] JsonObject request, CancellationToken ct)
    {
        NoCache();
        if (HttpContext.GetAuthenticatedAdmin() is null) return Unauthorized();
        if (!service.Enabled) return NotFound();
        if (request.Count != 1 || request["enabled"] is not JsonValue value || !value.TryGetValue<bool>(out var enabled)) return Invalid("只接受 enabled 布尔值。");
        return await repository.SetEnabledAsync(id, enabled, ct) ? NoContent() : NotFound();
    }

    /// <summary>读取账号和 API Key 的独立授权以及调整原因审计。</summary>
    [HttpGet("api/v1/admin/http-credentials/{id:guid}/grants", Name = "GetHttpCredentialGrants")]
    public async Task<IActionResult> Grants(Guid id, CancellationToken ct)
    {
        NoCache();
        if (HttpContext.GetAuthenticatedAdmin() is null) return Unauthorized();
        return !service.Enabled || await repository.DetailsAsync(id, ct) is null ? NotFound() : Ok(await repository.GrantsAsync(id, ct));
    }

    /// <summary>仅针对明确主体授权或撤权，调用工作流本身的 Key 许可不等价于凭据许可。</summary>
    [HttpPut("api/v1/admin/http-credentials/{id:guid}/grants/{kind}/{subject:guid}", Name = "SetHttpCredentialGrant")]
    [RequestSizeLimit(4096)]
    public async Task<IActionResult> SetGrant(Guid id, string kind, Guid subject, [FromBody] JsonObject request, CancellationToken ct)
    {
        NoCache();
        if (HttpContext.GetAuthenticatedAdmin() is not { } admin) return Unauthorized();
        if (!service.Enabled) return NotFound();
        if (request.Count != 2 || request["enabled"] is not JsonValue value || !value.TryGetValue<bool>(out var enabled) || request["reason"]?.GetValueKind() != System.Text.Json.JsonValueKind.String) return Invalid("请填写 enabled 和调整原因 reason。");
        try { await repository.SetGrantAsync(id, kind, subject, admin.Id, enabled, Text(request["reason"]), ct); return NoContent(); }
        catch (WorkflowHttpException exception) { return Failure(exception); }
    }

    /// <summary>凭据单独加密并追加版本，任何读取接口都不返回已保存的值。</summary>
    private async Task<IActionResult> SaveAsync(Guid id, JsonObject request, bool create, CancellationToken ct)
    {
        NoCache();
        if (HttpContext.GetAuthenticatedAdmin() is not { } admin) return Unauthorized();
        if (!service.Enabled) return NotFound();
        if (request.Any(pair => pair.Key is not ("name" or "description" or "origin" or "headers" or "requests"))
            || Text(request["name"]).Trim().Length is < 1 or > 100 || Text(request["description"]).Length > 1000
            || request["headers"] is not JsonArray rows || rows.Count > 8) return Invalid("请填写名称、目标站点，凭据请求头最多 8 个。");
        if (!Uri.TryCreate(Text(request["origin"]), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/"
            || Text(request["origin"]).Contains('{')) return Invalid("凭据目标需填写站点源，例如 https://api.example.com，可带端口，不包含路径、查询参数或变量。");
        var origin = uri.GetLeftPart(UriPartial.Authority);
        if (!HttpCredentialPolicy.ValidRequests(request["requests"], origin)) return Invalid("请配置 1–32 个授权方法和完整固定接口地址，不允许站点通配或路径变量。");
        var existing = create ? null : await repository.DetailsAsync(id, ct);
        if (!create && existing is null) return NotFound();
        try
        {
            JsonObject? previousDefinition = null;
            if (Guid.TryParse(Text(existing?["currentRevision"]), out var currentId) && await repository.FindRevisionAsync(id, currentId, ct) is { } current)
                previousDefinition = JsonNode.Parse(current.DefinitionJson)!.AsObject();
            // 同一凭据的旧授权不能被新版本扩展；更换接口或头身份必须创建新资源并重新授权。
            if (previousDefinition is not null && (Text(previousDefinition["origin"]) != origin
                || !JsonNode.DeepEquals(previousDefinition["requests"], request["requests"])))
                return Invalid("现有凭据的接口授权范围不可扩大或替换。请新建凭据并单独授权；当前凭据可轮换密钥。");
            // 只有沿用空值时才解密旧版本；轮换全部值可修复密钥更换后的旧密文。
            JsonObject previousHeaders = [];
            if (rows.OfType<JsonObject>().Any(row => Text(row["value"]).Length == 0)
                && Guid.TryParse(Text(existing?["currentRevision"]), out var previousId) && await repository.FindRevisionAsync(id, previousId, ct) is { Secret: not null } previous
                && Text(JsonNode.Parse(previous.DefinitionJson)?["origin"]) == origin)
                previousHeaders = JsonNode.Parse(protector.Unprotect(previous.Secret))!.AsObject();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var headers = new JsonObject(); var metadata = new JsonArray();
            foreach (var row in rows)
            {
                if (row is not JsonObject item || item.Count != 2 || item["name"]?.GetValueKind() != System.Text.Json.JsonValueKind.String
                    || item["value"]?.GetValueKind() != System.Text.Json.JsonValueKind.String) return Invalid("凭据请求头需要 name 和 value 字符串。");
                var name = Text(item["name"]); var value = Text(item["value"]);
                if (!HttpRequestDefinition.ValidHeader(name) || name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) || !names.Add(name)) return Invalid("凭据请求头名称无效、重复或属于保留传输头。");
                if (value.Length == 0) value = Text(previousHeaders.FirstOrDefault(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value);
                if (value.Length is < 1 or > 4096 || value.Any(char.IsControl)) return Invalid("请填写 1–4096 字符的凭据值。新站点或新增请求头不能沿用旧值。");
                headers[name] = value; metadata.Add(name);
            }
            if (previousDefinition is not null && !JsonNode.DeepEquals(previousDefinition["headers"], metadata))
                return Invalid("现有凭据的请求头身份不可替换。请新建凭据并单独授权；当前凭据可轮换密钥。");
            var definition = new JsonObject { ["origin"] = origin, ["headers"] = metadata, ["requests"] = request["requests"]!.DeepClone() };
            if (previousDefinition?["requestDigest"] is { } locked) definition["requestDigest"] = locked.DeepClone();
            var protectedValue = protector.Protect(headers.ToJsonString());
            var secret = new ApiKeySecret(protectedValue.Cipher, protectedValue.Nonce, protectedValue.Tag, "••••");
            var revision = await repository.SaveAsync(id, Text(request["name"]).Trim(), Text(request["description"]).Trim(), definition.ToJsonString(),
                HttpRequestDefinition.Digest(definition), secret, admin.Id, ct);
            return Ok(new JsonObject { ["id"] = id.ToString(), ["revisionId"] = revision.ToString() });
        }
        catch (WorkflowHttpException exception) { return Failure(exception); }
        catch (Exception exception) when (exception is CryptographicException or System.Text.Json.JsonException or InvalidOperationException)
        { return Invalid("旧凭据无法读取，请为所有请求头重新填写值或创建新凭据。"); }
    }

    /// <summary>配置与查询结果禁止浏览器和中间代理缓存。</summary>
    private void NoCache() => Response.Headers.CacheControl = "private, no-store";
    /// <summary>保留项目统一错误体并说明凭据配置问题。</summary>
    private IActionResult Invalid(string message) => BadRequest(new ErrorResponse("HTTP_CONFIG_INVALID", [message]));
    /// <summary>测试请求显式返回是否可能已执行，界面不得据此自动重发。</summary>
    private IActionResult Failure(WorkflowHttpException exception) => BadRequest(new JsonObject
    { ["error"] = exception.Code, ["details"] = new JsonArray(exception.Message), ["mayHaveExecuted"] = exception.MayHaveExecuted });
}
