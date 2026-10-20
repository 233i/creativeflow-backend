using System.Text.Json.Nodes;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using CreativeFlow.Api.Middleware;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Application.Features.Knowledge;

namespace CreativeFlow.Api.Controllers;

/// <summary>管理员知识库管理；不向普通用户或外部 API Key 开放文档与检索。</summary>
[ApiController]
[Route("api/v1/admin/knowledge-bases")]
[Tags("admin")]
public sealed class AdminKnowledgeController(IKnowledgeRepository repository, KnowledgeLibraryService service,
    IConfiguration configuration, KnowledgeEvaluationService evaluation) : ControllerBase
{
    /// <summary>仅由受信任部署配置显式启用；默认关闭，环境名称不再覆盖运维开关。</summary>
    private bool Enabled => bool.TryParse(configuration["Knowledge:Enabled"], out var enabled) && enabled;

    /// <summary>公开给管理员的能力信息，不接触向量或模型服务。</summary>
    [HttpGet("capabilities", Name = "AdminKnowledgeCapabilities")]
    public IActionResult Capabilities()
    {
        if (HttpContext.GetAuthenticatedAdmin() is null) return Unauthorized();
        Response.Headers.CacheControl = "private, no-store";
        return Ok(new JsonObject { ["enabled"] = Enabled, ["indexAvailable"] = Enabled && service.IndexAvailable,
            ["quotas"] = JsonSerializer.SerializeToNode(service.Quotas, KnowledgeJsonContext.Default.KnowledgeQuotas) });
    }

    /// <summary>列出实现容量范围内的知识库安全元数据，降低创建配额不会隐藏已有库。</summary>
    [HttpGet(Name = "ListAdminKnowledgeBases")]
    public Task<IActionResult> List(CancellationToken ct) => GuardAsync(async () => Ok(await repository.ListAsync(ct)));

    /// <summary>创建库并绑定已保存的向量配置，不发送模型请求。</summary>
    [HttpPost(Name = "CreateAdminKnowledgeBase")]
    [RequestSizeLimit(16384)]
    public Task<IActionResult> Create([FromBody] CreateKnowledgeBaseRequest request, CancellationToken ct) =>
        GuardAsync(async () => Ok(await service.CreateAsync(request, ct)));

    /// <summary>读取版本进度，列表不返回向量、原文件正文或密钥。</summary>
    [HttpGet("{id:guid}", Name = "GetAdminKnowledgeBase")]
    public Task<IActionResult> Get(Guid id, CancellationToken ct) => GuardAsync(async () =>
    {
        var library = await repository.FindAsync(id, ct);
        return library is null ? NotFound() : Ok(new KnowledgeDetailResponse(library, await repository.VersionsAsync(id, ct)));
    });

    /// <summary>立即停用或恢复库；不会物理删除历史资料或自动重新入库。</summary>
    [HttpPut("{id:guid}/enabled", Name = "SetAdminKnowledgeEnabled")]
    [RequestSizeLimit(1024)]
    public Task<IActionResult> SetEnabled(Guid id, [FromBody] SetKnowledgeEnabledRequest request, CancellationToken ct) => GuardAsync(async () =>
    {
        if (await repository.FindAsync(id, ct) is null) return NotFound();
        await repository.SetEnabledAsync(id, request.Enabled, ct); return NoContent();
    });

    /// <summary>纯文本解析预览，不写数据库、抓取链接或调用向量服务。</summary>
    [HttpPost("preview", Name = "PreviewAdminKnowledgeDocuments")]
    [RequestSizeLimit(16777216)]
    public Task<IActionResult> Preview([FromBody] KnowledgeImportRequest request) =>
        GuardAsync(() => Task.FromResult<IActionResult>(Ok(service.Preview(request))));

    /// <summary>确认完整文档集的预览摘要并入队，立即返回索引版本兼任务 ID。</summary>
    [HttpPost("{id:guid}/versions", Name = "ImportAdminKnowledgeVersion")]
    [RequestSizeLimit(16777216)]
    public Task<IActionResult> Import(Guid id, [FromBody] KnowledgeImportRequest request, CancellationToken ct) => GuardAsync(async () =>
    {
        var version = await service.ImportAsync(id, request, ct);
        return Accepted($"/api/v1/admin/knowledge-bases/{id}", version);
    });

    /// <summary>读取管理员有权查看的分段；停用后即时阻止正文读取。</summary>
    [HttpGet("{id:guid}/versions/{versionId:guid}/chunks", Name = "GetAdminKnowledgeChunks")]
    public Task<IActionResult> Chunks(Guid id, Guid versionId, CancellationToken ct, [FromQuery] int offset = 0, [FromQuery] int limit = 128, [FromQuery] Guid? documentId = null) => GuardAsync(async () =>
    {
        if ((await repository.FindAsync(id, ct))?.Enabled != true) return NotFound();
        if (offset < 0 || limit is < 1 or > 128) throw new KnowledgeException("片段分页每页 1–128 条。");
        var chunks = await repository.ReadChunksAsync(id, versionId, null, documentId, offset, limit, ct);
        if ((await repository.FindAsync(id, ct))?.Enabled != true) return NotFound();
        return Ok(chunks);
    });

    /// <summary>文档列表只返回修订元数据，不再通过加载所有分段计算文档数。</summary>
    [HttpGet("{id:guid}/versions/{versionId:guid}/documents", Name = "ListAdminKnowledgeDocuments")]
    public Task<IActionResult> Documents(Guid id, Guid versionId, CancellationToken ct) => GuardAsync(async () =>
    {
        if ((await repository.FindAsync(id, ct))?.Enabled != true) return NotFound();
        var documents = await repository.DocumentsAsync(id, versionId, ct);
        if ((await repository.FindAsync(id, ct))?.Enabled != true) return NotFound();
        return Ok(documents);
    });

    /// <summary>管理员显式打开修订时才读取单篇原文，停用后不交付正文。</summary>
    [HttpGet("{id:guid}/versions/{versionId:guid}/documents/{documentId:guid}", Name = "GetAdminKnowledgeDocument")]
    public Task<IActionResult> Document(Guid id, Guid versionId, Guid documentId, CancellationToken ct) => GuardAsync(async () =>
    {
        if ((await repository.FindAsync(id, ct))?.Enabled != true) return NotFound();
        var document = await repository.DocumentAsync(id, versionId, documentId, ct);
        if ((await repository.FindAsync(id, ct))?.Enabled != true) return NotFound();
        return document is null ? NotFound() : Ok(document);
    });

    /// <summary>预览文档新增/替换/移除后的完整版本，不写数据或调用模型。</summary>
    [HttpPost("{id:guid}/document-changes/preview", Name = "PreviewAdminKnowledgeDocumentChanges")]
    [RequestSizeLimit(16777216)]
    public Task<IActionResult> PreviewChanges(Guid id, [FromBody] KnowledgeDocumentChangeRequest request, CancellationToken ct) =>
        GuardAsync(async () => Ok(await service.PreviewChangesAsync(id, request, ct)));

    /// <summary>以已确认基线生成新候选版本，原版本和其他未变文档均保持不变。</summary>
    [HttpPost("{id:guid}/document-changes", Name = "ApplyAdminKnowledgeDocumentChanges")]
    [RequestSizeLimit(16777216)]
    public Task<IActionResult> ChangeDocuments(Guid id, [FromBody] KnowledgeDocumentChangeRequest request, CancellationToken ct) => GuardAsync(async () =>
        Accepted($"/api/v1/admin/knowledge-bases/{id}", await service.ChangeDocumentsAsync(id, request, ct)));

    /// <summary>清理预检不会修改索引，只显示阻止条件与引用数量。</summary>
    [HttpGet("{id:guid}/versions/{versionId:guid}/cleanup", Name = "PreviewAdminKnowledgeCleanup")]
    public Task<IActionResult> CleanupPreview(Guid id, Guid versionId, CancellationToken ct) =>
        GuardAsync(async () => Ok(await repository.CleanupPreviewAsync(id, versionId, ct)));

    /// <summary>管理员确认原因后排队清理单版本；提交时复验全部引用，不立即删除其他资源。</summary>
    [HttpPost("{id:guid}/versions/{versionId:guid}/cleanup", Name = "RequestAdminKnowledgeCleanup")]
    [RequestSizeLimit(4096)]
    public Task<IActionResult> Cleanup(Guid id, Guid versionId, [FromBody] KnowledgeCleanupRequest request, CancellationToken ct) => GuardAsync(async () =>
    {
        if (!service.IndexAvailable) throw new KnowledgeException("索引服务未配置，暂时不能清理远端集合。");
        await repository.RequestCleanupAsync(id, versionId, HttpContext.GetAuthenticatedAdmin()!.Id, request.Reason, ct);
        return Accepted($"/api/v1/admin/knowledge-bases/{id}", new JsonObject
        { ["versionId"] = versionId.ToString(), ["accepted"] = true });
    });

    /// <summary>候选就绪后显式激活，原子替换当前索引指针。</summary>
    [HttpPost("{id:guid}/versions/{versionId:guid}/activate", Name = "ActivateAdminKnowledgeVersion")]
    public Task<IActionResult> Activate(Guid id, Guid versionId, CancellationToken ct) => GuardAsync(async () =>
    {
        if (!await repository.ActivateAsync(id, versionId, ct)) throw new KnowledgeException("只有未停用库中的就绪版本可以激活。");
        return NoContent();
    });

    /// <summary>区分检查点继续与明确确认的重新向量化，避免未知请求盲重发。</summary>
    [HttpPost("{id:guid}/versions/{versionId:guid}/recover", Name = "RecoverAdminKnowledgeVersion")]
    [RequestSizeLimit(1024)]
    public Task<IActionResult> Recover(Guid id, Guid versionId, [FromBody] RecoverKnowledgeVersionRequest request, CancellationToken ct) => GuardAsync(async () =>
    {
        await service.RecoverAsync(id, versionId, request.Action, ct); return NoContent();
    });

    /// <summary>从旧向量版本创建混合索引候选，不重新向量化或自动激活。</summary>
    [HttpPost("{id:guid}/versions/{versionId:guid}/rebuild", Name = "RebuildAdminKnowledgeHybridVersion")]
    public Task<IActionResult> Rebuild(Guid id, Guid versionId, CancellationToken ct) =>
        GuardAsync(async () => Accepted(await service.RebuildHybridAsync(id, versionId, ct)));

    /// <summary>按显式配置召回、融合及重排序，返回可见片段和诊断收据，不生成答案。</summary>
    [HttpPost("{id:guid}/search", Name = "SearchAdminKnowledgeBase")]
    [RequestSizeLimit(16384)]
    public Task<IActionResult> Search(Guid id, [FromBody] KnowledgeSearchRequest request, CancellationToken ct) =>
        GuardAsync(async () => Ok(await service.SearchAsync(id, request, ct)));

    /// <summary>在同一激活版本上比较人工标注题，仅显式确认时调用重排序模型。</summary>
    [HttpPost("{id:guid}/evaluate", Name = "EvaluateAdminKnowledgeRetrieval")]
    [RequestSizeLimit(131072)]
    public Task<IActionResult> Evaluate(Guid id, [FromBody] KnowledgeEvaluationRequest request, CancellationToken ct) =>
        GuardAsync(async () => Ok(await evaluation.EvaluateAsync(id, request, ct)));

    /// <summary>认证及功能开关在进入每个用例前检查；只返回已脱敏业务错误。</summary>
    private async Task<IActionResult> GuardAsync(Func<Task<IActionResult>> action)
    {
        if (HttpContext.GetAuthenticatedAdmin() is null) return Unauthorized();
        if (!Enabled) return NotFound();
        Response.Headers.CacheControl = "private, no-store";
        try { return await action(); }
        catch (KnowledgeException exception) { return BadRequest(new ErrorResponse("invalid_request", [exception.Message])); }
    }
}
