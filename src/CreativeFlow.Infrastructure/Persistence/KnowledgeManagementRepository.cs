using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Npgsql;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Application.Features.Knowledge;

namespace CreativeFlow.Infrastructure.Persistence;

public sealed partial class KnowledgeRepository
{
    /// <summary>库查询统一包含并发修订和删除回执字段。</summary>
    private const string BaseColumns = "id, name, description, model_id, enabled, active_version, revision, deletion_status, deletion_error, deleted_at";

    /// <summary>行锁及修订同时校验，更新与审计不可分别提交。</summary>
    public async Task<KnowledgeBaseResponse> UpdateMetadataAsync(Guid id, Guid admin, UpdateKnowledgeMetadataRequest request, CancellationToken ct)
    {
        if (request.Revision < 1 || string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 80 || request.Description is null
            || request.Description.Length > 1000 || request.Name.Contains('\0') || request.Description.Contains('\0'))
            throw new KnowledgeException("名称应为 1–80 字符，用途说明最多 1000 字符。");
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteAsync("SELECT pg_advisory_xact_lock(721903263)", transaction: transaction);
        var before = await ReadBaseAsync(connection, transaction, id);
        if (before.Revision != request.Revision || before.DeletionStatus != "none") throw Stale();
        await connection.ExecuteAsync("UPDATE knowledge_base SET name = @Name, description = @Description WHERE id = @Id",
            new { Id = id, Name = request.Name.Trim(), Description = request.Description.Trim() }, transaction);
        var after = await ReadBaseAsync(connection, transaction, id);
        if (before != after)
            await WriteAuditAsync(connection, transaction, id, admin, "metadata_updated", "", before, after);
        await transaction.CommitAsync(ct); return after;
    }

    /// <summary>预览读取与所有图引用变更互斥；返回有限引用清单及完整计数。</summary>
    public async Task<KnowledgeDeletionPreview> PreviewDeletionAsync(Guid id, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteAsync("SELECT pg_advisory_xact_lock_shared(721903263)", transaction: transaction);
        var result = await BuildDeletionPreviewAsync(connection, transaction, id);
        await transaction.CommitAsync(ct); return result;
    }

    /// <summary>提交时重新检查全部持久引用和索引状态，封存后新引用由数据库触发器拒绝。</summary>
    public async Task<KnowledgeBaseResponse> DeleteLibraryAsync(Guid id, Guid admin, DeleteKnowledgeRequest request, CancellationToken ct)
    {
        if (request.Revision < 1 || string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length > 300 || request.Reason.Contains('\0'))
            throw new KnowledgeException("删除原因应为 1–300 字符。");
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteAsync("SELECT pg_advisory_xact_lock(721903263)", transaction: transaction);
        var before = await ReadBaseAsync(connection, transaction, id);
        if (request.ConfirmName != before.Name) throw new KnowledgeException("确认名称与当前知识库名称不一致。");
        if (before.DeletionStatus is "deleting" or "deleted") return before;
        var preview = await BuildDeletionPreviewAsync(connection, transaction, id);
        if (before.Revision != request.Revision || preview.ImpactToken != request.ImpactToken) throw Stale();
        if (!preview.Allowed) throw new KnowledgeManagementConflict(string.Join(" ", preview.Blockers));
        await connection.ExecuteAsync("""
            UPDATE knowledge_base SET enabled = false, active_version = NULL, deletion_status = 'deleting', deletion_error = NULL WHERE id = @Id;
            UPDATE knowledge_version SET status = 'cleaning', error = NULL, updated_at = clock_timestamp()
                WHERE base_id = @Id AND status <> 'deleted';
            """, new { Id = id }, transaction);
        await WriteAuditAsync(connection, transaction, id, admin, before.DeletionStatus == "delete_failed" ? "delete_retried" : "delete_requested", request.Reason.Trim(), before, null);
        await connection.ExecuteAsync("""
            UPDATE knowledge_base SET deletion_status = 'deleted', deleted_at = clock_timestamp()
            WHERE id = @Id AND NOT EXISTS (SELECT 1 FROM knowledge_version WHERE base_id = @Id AND status <> 'deleted');
            INSERT INTO knowledge_management_audit(id, base_id, action)
            SELECT @Audit, id, 'delete_completed' FROM knowledge_base WHERE id = @Id AND deletion_status = 'deleted';
            """, new { Id = id, Audit = Guid.CreateVersion7() }, transaction);
        var after = await ReadBaseAsync(connection, transaction, id);
        await transaction.CommitAsync(ct); return after;
    }

    /// <summary>审计查询允许读取删除墓碑，不再加载已清理正文。</summary>
    public async Task<KnowledgeManagementAudit[]> AuditAsync(Guid id, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<KnowledgeManagementAuditRow>("""
            SELECT a.id, a.admin_id, u.username AS admin_name, a.action, a.reason,
                a.before_json::text AS before_json, a.after_json::text AS after_json, a.created_at
            FROM knowledge_management_audit a LEFT JOIN creativeflow_admin u ON u.admin_id = a.admin_id
            WHERE a.base_id = @Id ORDER BY a.created_at DESC, a.id DESC LIMIT 100
            """, new { Id = id })).Select(row => row.ToModel()).ToArray();
    }

    /// <summary>导出返回前再次检查停用及删除状态，同时保存安全导出审计。</summary>
    public async Task RecordExportAsync(Guid id, Guid admin, Guid? version, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteAsync("SELECT pg_advisory_xact_lock_shared(721903263)", transaction: transaction);
        var library = await ReadBaseAsync(connection, transaction, id); RequireReadable(library);
        await WriteAuditAsync(connection, transaction, id, admin, "exported", version?.ToString() ?? "全部可读版本", library, null);
        await transaction.CommitAsync(ct);
    }

    /// <summary>共享引用锁和库行共享锁覆盖整个归档读取；每次最多载入一个受限版本的原文。</summary>
    public async Task ReadVersionsAsync(Guid id, Guid? version, Func<KnowledgeBaseResponse, CancellationToken, Task> begin,
        Func<KnowledgeVersionResponse, KnowledgeDocumentInput[], CancellationToken, Task> read, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteAsync("SELECT pg_advisory_xact_lock_shared(721903263)", transaction: transaction);
        var library = await ReadBaseAsync(connection, transaction, id);
        RequireReadable(library);
        var versions = (await connection.QueryAsync<KnowledgeStoredVersionRow>("SELECT " + VersionColumns + " FROM knowledge_version WHERE base_id = @Id AND status NOT IN ('cleaning','cleanup_failed','deleted') AND (@Version IS NULL OR id = @Version) ORDER BY number",
            new { Id = id, Version = version }, transaction)).ToArray();
        if (version is not null && versions.Length != 1) throw new KnowledgeException("所选版本不存在、已清理或不属于当前知识库。");
        await begin(library, ct);
        foreach (var entry in versions)
            await read(entry.ToModel().Progress!, await ReadBodiesAsync(connection, transaction, id, entry.Id), ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>同一边界读取两个版本；不存在或清理中的版本不产生伪造的全量新增/删除差异。</summary>
    public async Task<KnowledgeComparisonSource> ComparisonAsync(Guid id, Guid from, Guid to, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteAsync("SELECT pg_advisory_xact_lock_shared(721903263)", transaction: transaction);
        RequireReadable(await ReadBaseAsync(connection, transaction, id));
        var rows = (await connection.QueryAsync<KnowledgeStoredVersionRow>("SELECT " + VersionColumns + " FROM knowledge_version WHERE base_id = @Id AND id = ANY(@Ids) AND status NOT IN ('cleaning','cleanup_failed','deleted')",
            new { Id = id, Ids = new[] { from, to } }, transaction)).ToDictionary(row => row.Id);
        if (!rows.ContainsKey(from) || !rows.ContainsKey(to)) throw new KnowledgeException("对比版本不存在、已清理或不属于当前知识库。");
        var result = new KnowledgeComparisonSource(rows[from].ToModel().Progress!, rows[to].ToModel().Progress!,
            await ReadBodiesAsync(connection, transaction, id, from), await ReadBodiesAsync(connection, transaction, id, to),
            await ReadSummariesAsync(connection, transaction, id, from), await ReadSummariesAsync(connection, transaction, id, to));
        await transaction.CommitAsync(ct); return result;
    }

    /// <summary>读取元数据并持有库行共享锁，防止导出期间停用或封存。</summary>
    private static async Task<KnowledgeBaseResponse> ReadBaseAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id) =>
        (await connection.QuerySingleOrDefaultAsync<KnowledgeManagementBaseRow>("SELECT " + BaseColumns + " FROM knowledge_base WHERE id = @Id FOR SHARE", new { Id = id }, transaction))?.ToModel()
            ?? throw new KnowledgeException("知识库不存在。");

    /// <summary>停用或删除中的库不再向导出与对比交付正文。</summary>
    private static void RequireReadable(KnowledgeBaseResponse library)
    {
        if (!library.Enabled || library.DeletionStatus != "none") throw new KnowledgeException("知识库已停用或进入删除流程，不能导出或对比正文。");
    }

    /// <summary>按版本关联顺序读取规范化原文，禁止跨库读取同名文档。</summary>
    private static async Task<KnowledgeDocumentInput[]> ReadBodiesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, Guid version) =>
        (await connection.QueryAsync<KnowledgeDocumentInput>("SELECT d.name, d.body AS text FROM knowledge_document d JOIN knowledge_version_document vd ON vd.document_id = d.id WHERE d.base_id = @Id AND vd.version_id = @Version ORDER BY vd.ordinal",
            new { Id = id, Version = version }, transaction)).ToArray();

    /// <summary>读取版本实际文档大小及切片数，不沿用旧版缓存计数。</summary>
    private static async Task<KnowledgeDocumentSummary[]> ReadSummariesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, Guid version) =>
        (await connection.QueryAsync<KnowledgeDocumentSummary>("SELECT d.id, d.name, d.byte_count, (SELECT count(*)::int FROM knowledge_chunk c WHERE c.version_id = @Version AND c.document_id = d.id) AS chunk_count FROM knowledge_document d JOIN knowledge_version_document vd ON vd.document_id = d.id WHERE d.base_id = @Id AND vd.version_id = @Version ORDER BY vd.ordinal",
            new { Id = id, Version = version }, transaction)).ToArray();

    /// <summary>在业务事务内追加安全元数据审计，绝不保存正文或模型密钥。</summary>
    private static async Task WriteAuditAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, Guid admin,
        string action, string reason, KnowledgeBaseResponse before, KnowledgeBaseResponse? after) =>
        await connection.ExecuteAsync("INSERT INTO knowledge_management_audit(id,base_id,admin_id,action,reason,before_json,after_json) VALUES (@Audit,@Id,@Admin,@Action,@Reason,@Before::jsonb,@After::jsonb)",
            new { Audit = Guid.CreateVersion7(), Id = id, Admin = admin, Action = action, Reason = reason,
                Before = JsonSerializer.Serialize(before, KnowledgeJsonContext.Default.KnowledgeBaseResponse),
                After = after is null ? null : JsonSerializer.Serialize(after, KnowledgeJsonContext.Default.KnowledgeBaseResponse) }, transaction);

    /// <summary>过期修订或影响摘要要求管理员重新读取，禁止自动重提。</summary>
    private static KnowledgeManagementConflict Stale() => new("知识库或引用状态已经变化，请重新读取并确认；未自动提交。");

    /// <summary>完整计数所有保留引用，展示清单限一百项，确认摘要覆盖未展示项。</summary>
    private static async Task<KnowledgeDeletionPreview> BuildDeletionPreviewAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id)
    {
        var library = await ReadBaseAsync(connection, transaction, id);
        var versions = (await connection.QueryAsync<KnowledgeDeletionVersion>("SELECT id, status, digest FROM knowledge_version WHERE base_id = @Id AND status <> 'deleted' ORDER BY id", new { Id = id }, transaction)).ToArray();
        var pins = (await connection.QueryAsync<KnowledgeManagementReference>("""
            SELECT p.owner_kind AS kind, p.owner_id AS id,
                COALESCE(w.name, t.name, dw.name, rw.name, p.owner_id::text) AS name, count(*)::int AS versions
            FROM knowledge_version_pin p JOIN knowledge_version v ON v.id = p.version_id
            LEFT JOIN workflow w ON p.owner_kind = 'workflow' AND w.workflow_id = p.owner_id
            LEFT JOIN workflow_template t ON p.owner_kind = 'workflow_template' AND t.template_id = p.owner_id
            LEFT JOIN workflow_deployment dep ON p.owner_kind = 'workflow_deployment' AND dep.deployment_id = p.owner_id
            LEFT JOIN workflow dw ON dw.workflow_id = dep.workflow_id
            LEFT JOIN workflow_run r ON p.owner_kind = 'workflow_run' AND r.run_id = p.owner_id
            LEFT JOIN workflow rw ON rw.workflow_id = r.workflow_id
            WHERE v.base_id = @Id GROUP BY p.owner_kind, p.owner_id, w.name, t.name, dw.name, rw.name ORDER BY p.owner_kind, p.owner_id
            """, new { Id = id }, transaction)).ToArray();
        var documents = await connection.ExecuteScalarAsync<int>("SELECT count(*)::int FROM knowledge_document WHERE base_id = @Id", new { Id = id }, transaction);
        var chunks = await connection.ExecuteScalarAsync<int>("SELECT count(*)::int FROM knowledge_chunk c JOIN knowledge_version v ON v.id = c.version_id WHERE v.base_id = @Id", new { Id = id }, transaction);
        var tasks = versions.Count(v => v.Status is "queued" or "embedding" or "writing" or "cleaning");
        var blockers = new List<string>();
        if (pins.Length > 0) blockers.Add("仍被工作流草稿、模板、发布版本或运行记录引用；请先处理引用，历史运行也受保护。");
        if (tasks > 0) blockers.Add("仍有排队、执行或清理中的索引任务，请等待任务结束。");
        if (library.DeletionStatus is "deleting" or "deleted") blockers.Add("删除已受理或已完成。");
        var token = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(library.Revision + "\n" +
            string.Join('\n', versions.Select(v => $"{v.Id}:{v.Status}:{v.Digest}")) + "\n" +
            string.Join('\n', pins.Select(p => $"{p.Kind}:{p.Id}:{p.Versions}")))));
        return new(id, library.Name, library.Revision, token, blockers.Count == 0, blockers.ToArray(), versions.Length,
            documents, chunks, tasks, pins.Length, pins.Take(100).ToArray());
    }
}
/// <summary>删除影响哈希所需的最小版本状态。</summary>
/// <param name="Id">参与删除影响摘要的版本标识。</param>
/// <param name="Status">预检时版本的持久状态。</param>
/// <param name="Digest">完整文档集摘要，用于防止预检后变化。</param>

internal sealed record KnowledgeDeletionVersion(Guid Id, string Status, string Digest);

/// <summary>Npgsql 的 UTC timestamptz 先按 DateTime 读取，再显式转换公共偏移时间，兼容 Dapper AOT。</summary>
/// <param name="Id">知识库主键。</param>
/// <param name="Name">当前库名。</param>
/// <param name="Description">用途说明。</param>
/// <param name="ModelId">固定向量模型标识。</param>
/// <param name="Enabled">是否允许读取知识内容。</param>
/// <param name="ActiveVersion">当前激活版本，可为空。</param>
/// <param name="Revision">管理乐观锁修订。</param>
/// <param name="DeletionStatus">整库清理状态。</param>
/// <param name="DeletionError">最近一次清理的安全错误。</param>
/// <param name="DeletedAt">数据库返回的 UTC 删除时间。</param>
internal sealed record KnowledgeManagementBaseRow(Guid Id, string Name, string Description, Guid ModelId, bool Enabled,
    Guid? ActiveVersion, long Revision, string DeletionStatus, string? DeletionError, DateTime? DeletedAt)
{
    /// <summary>将 Npgsql UTC 时间显式转成 API 偏移时间。</summary>
    internal KnowledgeBaseResponse ToModel() => new(Id, Name, Description, ModelId, Enabled, ActiveVersion)
    { Revision = Revision, DeletionStatus = DeletionStatus, DeletionError = DeletionError,
        DeletedAt = DeletedAt is { } date ? new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Utc)) : null };
}

/// <summary>审计时间遵循数据库 UTC 类型，不依赖反射类型处理器。</summary>
/// <param name="Id">审计主键。</param>
/// <param name="AdminId">管理员标识，后台任务为空。</param>
/// <param name="AdminName">当前管理员名称。</param>
/// <param name="Action">持久化管理动作代码。</param>
/// <param name="Reason">操作理由或导出范围。</param>
/// <param name="BeforeJson">操作前安全元数据 JSON。</param>
/// <param name="AfterJson">操作后安全元数据 JSON。</param>
/// <param name="CreatedAt">数据库返回的 UTC 事件时间。</param>
internal sealed record KnowledgeManagementAuditRow(Guid Id, Guid? AdminId, string? AdminName, string Action,
    string Reason, string? BeforeJson, string? AfterJson, DateTime CreatedAt)
{
    /// <summary>映射审计字段并保留 UTC 时间语义。</summary>
    internal KnowledgeManagementAudit ToModel() => new(Id, AdminId, AdminName, Action, Reason, BeforeJson, AfterJson,
        new DateTimeOffset(DateTime.SpecifyKind(CreatedAt, DateTimeKind.Utc)));
}
