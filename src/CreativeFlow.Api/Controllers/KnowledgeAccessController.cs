using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using CreativeFlow.Api.Middleware;
using CreativeFlow.Application.Abstractions.Identity;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Application.Features.Knowledge;
using CreativeFlow.Application.Features.Workflow;

namespace CreativeFlow.Api.Controllers;

/// <summary>知识库目录和显式账号/Key 授权；资源管理仅对独立管理员开放。</summary>
[ApiController]
[Tags("knowledge")]
public sealed class KnowledgeAccessController(KnowledgeWorkflowService knowledge, IKnowledgeAccessRepository access,
    IKnowledgeRepository libraries, IAccountContext accounts) : ControllerBase
{
    /// <summary>普通用户必须有匹配设备的账号会话，只列授权库的安全元数据。</summary>
    [HttpGet("api/v1/knowledge-bases/catalog", Name = "KnowledgeCatalog")]
    public async Task<IActionResult> Catalog(CancellationToken ct)
    {
        Response.Headers.CacheControl = "private, no-store";
        if (accounts.AccountId is not { } account || HttpContext.GetDeviceId() is not { } device) return Unauthorized();
        if (!knowledge.Enabled) return NotFound();
        return Ok(await knowledge.CatalogAsync(new("account", account.Value, device.Value), ct));
    }

    /// <summary>独立管理员目录用于受控 API 工作流调试，不替普通用户提升权限。</summary>
    [HttpGet("api/v1/admin/knowledge-bases/catalog", Name = "AdminKnowledgeCatalog")]
    public async Task<IActionResult> AdminCatalog(CancellationToken ct)
    {
        Response.Headers.CacheControl = "private, no-store";
        if (HttpContext.GetAuthenticatedAdmin() is not { } admin) return Unauthorized();
        if (!knowledge.Enabled) return NotFound();
        return Ok(await knowledge.CatalogAsync(new("admin", admin.Id, Guid.Empty), ct));
    }

    /// <summary>管理员读取当前授权及最近审计；编辑器目录不暴露此账号列表。</summary>
    [HttpGet("api/v1/admin/knowledge-bases/{id:guid}/grants", Name = "GetKnowledgeGrants")]
    public async Task<IActionResult> Grants(Guid id, CancellationToken ct)
    {
        Response.Headers.CacheControl = "private, no-store";
        if (HttpContext.GetAuthenticatedAdmin() is null) return Unauthorized();
        if (!knowledge.Enabled || await libraries.FindAsync(id, ct) is null) return NotFound();
        return Ok(await access.GrantsAsync(id, ct));
    }

    /// <summary>带必填原因的幂等授权/撤权；只影响明确指定的普通账号，不修改 API Key。</summary>
    [HttpPut("api/v1/admin/knowledge-bases/{id:guid}/grants/{account:guid}", Name = "SetKnowledgeGrant")]
    [RequestSizeLimit(4096)]
    public async Task<IActionResult> SetGrant(Guid id, Guid account, [FromBody] JsonObject request, CancellationToken ct)
    {
        Response.Headers.CacheControl = "private, no-store";
        if (HttpContext.GetAuthenticatedAdmin() is not { } admin) return Unauthorized();
        if (!knowledge.Enabled) return NotFound();
        if (request.Count != 2 || request["enabled"] is not JsonValue value || !value.TryGetValue<bool>(out var enabled)
            || request["reason"] is not JsonValue reason || !reason.TryGetValue<string>(out var message)) return BadRequest();
        try { await access.SetGrantAsync(id, account, admin.Id, enabled, message, ct); return NoContent(); }
        catch (KnowledgeException exception) { return BadRequest(new ErrorResponse("invalid_request", [exception.Message])); }
    }

    /// <summary>管理员单独授予工作流 Key 知识访问权；参数是后台 Key 记录 ID，不是秘密凭据。</summary>
    [HttpPut("api/v1/admin/knowledge-bases/{id:guid}/api-key-grants/{apiKey:guid}", Name = "SetKnowledgeApiKeyGrant")]
    [RequestSizeLimit(4096)]
    public async Task<IActionResult> SetApiKeyGrant(Guid id, Guid apiKey, [FromBody] JsonObject request, CancellationToken ct)
    {
        Response.Headers.CacheControl = "private, no-store";
        if (HttpContext.GetAuthenticatedAdmin() is not { } admin) return Unauthorized();
        if (!knowledge.Enabled) return NotFound();
        if (request.Count != 2 || request["enabled"] is not JsonValue value || !value.TryGetValue<bool>(out var enabled)
            || request["reason"] is not JsonValue reason || !reason.TryGetValue<string>(out var message)) return BadRequest();
        try { await access.SetApiKeyGrantAsync(id, apiKey, admin.Id, enabled, message, ct); return NoContent(); }
        catch (KnowledgeException exception) { return BadRequest(new ErrorResponse("invalid_request", [exception.Message])); }
    }
}
