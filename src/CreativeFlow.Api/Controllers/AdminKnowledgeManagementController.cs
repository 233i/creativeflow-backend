using Microsoft.AspNetCore.Mvc;
using CreativeFlow.Api.Middleware;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Application.Features.Knowledge;

namespace CreativeFlow.Api.Controllers;

/// <summary>管理员知识库元数据、删除影响、归档与版本对比；普通账号及外部 Key 不获得正文管理权。</summary>
[ApiController]
[Route("api/v1/admin/knowledge-bases/{id:guid}")]
[Tags("admin")]
public sealed class AdminKnowledgeManagementController(IKnowledgeManagementRepository repository,
    KnowledgeManagementService service, IKnowledgeIndexStore index, IConfiguration configuration) : ControllerBase
{
    /// <summary>重命名或修改用途，在事务内保存审计及并发修订。</summary>
    [HttpPatch(Name = "UpdateAdminKnowledgeMetadata")]
    [RequestSizeLimit(16384)]
    public Task<IActionResult> Update(Guid id, UpdateKnowledgeMetadataRequest request, CancellationToken ct) =>
        GuardAsync(async () => Ok(await repository.UpdateMetadataAsync(id, HttpContext.GetAuthenticatedAdmin()!.Id, request, ct)));

    /// <summary>返回草稿、模板、发布版本及运行引用和完整删除影响，不改变资源。</summary>
    [HttpGet("deletion", Name = "PreviewAdminKnowledgeDeletion")]
    public Task<IActionResult> Deletion(Guid id, CancellationToken ct) => GuardAsync(async () => Ok(await repository.PreviewDeletionAsync(id, ct)));

    /// <summary>显式确认名称、原因与影响摘要；实际索引清理由已有持久 Worker 完成。</summary>
    [HttpDelete(Name = "DeleteAdminKnowledgeBase")]
    [RequestSizeLimit(4096)]
    public Task<IActionResult> Delete(Guid id, DeleteKnowledgeRequest request, CancellationToken ct) => GuardAsync(async () =>
    {
        if (!index.Available && (await repository.PreviewDeletionAsync(id, ct)).Versions > 0)
            throw new KnowledgeException("索引服务未配置，无法确认清理远端向量。");
        return Accepted($"/api/v1/admin/knowledge-bases/{id}", await repository.DeleteLibraryAsync(id, HttpContext.GetAuthenticatedAdmin()!.Id, request, ct));
    });

    /// <summary>已删除库仍保留可核对的管理审计，不返回已删除正文。</summary>
    [HttpGet("audit", Name = "GetAdminKnowledgeAudit")]
    public Task<IActionResult> Audit(Guid id, CancellationToken ct) => GuardAsync(async () => Ok(await repository.AuditAsync(id, ct)));

    /// <summary>导出所有可读版本或指定版本；归档包含原文及清单，不包含凭据、向量或授权。</summary>
    [HttpGet("export", Name = "ExportAdminKnowledgeBase")]
    public Task<IActionResult> Export(Guid id, CancellationToken ct, [FromQuery] Guid? versionId = null) => GuardAsync(async () =>
    {
        var stream = await service.ExportAsync(id, versionId, ct);
        try
        {
            await repository.RecordExportAsync(id, HttpContext.GetAuthenticatedAdmin()!.Id, versionId, ct);
            return File(stream, "application/zip", $"knowledge-{id:D}.zip");
        }
        catch { await stream.DisposeAsync(); throw; }
    });

    /// <summary>对比同库两个不可变版本的文档集合与分段配置。</summary>
    [HttpGet("compare", Name = "CompareAdminKnowledgeVersions")]
    public Task<IActionResult> Compare(Guid id, Guid from, Guid to, CancellationToken ct) =>
        GuardAsync(async () => Ok(await service.CompareAsync(id, from, to, ct)));

    /// <summary>只返回所选文档的差异行页，正文作为不可信纯文本交付。</summary>
    [HttpGet("compare/document", Name = "CompareAdminKnowledgeDocument")]
    public Task<IActionResult> CompareDocument(Guid id, Guid from, Guid to, string name, CancellationToken ct, int offset = 0, int limit = 100) =>
        GuardAsync(async () => Ok(await service.CompareDocumentAsync(id, from, to, name, offset, limit, ct)));

    /// <summary>所有管理动作共用管理员、功能开关、私有缓存及可读错误边界。</summary>
    private async Task<IActionResult> GuardAsync(Func<Task<IActionResult>> action)
    {
        if (HttpContext.GetAuthenticatedAdmin() is null) return Unauthorized();
        if (!bool.TryParse(configuration["Knowledge:Enabled"], out var enabled) || !enabled) return NotFound();
        Response.Headers.CacheControl = "private, no-store";
        try { return await action(); }
        catch (KnowledgeManagementConflict error) { return Conflict(new ErrorResponse("knowledge_state_changed", [error.Message])); }
        catch (KnowledgeException error) { return BadRequest(new ErrorResponse("invalid_request", [error.Message])); }
    }
}
