using Dapper;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Application.Features.Knowledge;

namespace CreativeFlow.Infrastructure.Persistence;

public sealed partial class KnowledgeRepository
{
    /// <summary>预检只返回引用计数与安全原因；清理提交不会信任客户端的预检结果。</summary>
    public async Task<KnowledgeCleanupPreview> CleanupPreviewAsync(Guid id, Guid version, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<KnowledgeCleanupRow>(CleanupSql, new { Id = id, Version = version });
        return CleanupResult(row);
    }

    /// <summary>显式确认后先封存版本，数据库引用触发器拒绝新的发布/运行竞态；远端删除由 Worker 执行。</summary>
    public async Task RequestCleanupAsync(Guid id, Guid version, Guid admin, string reason, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 300) throw new KnowledgeException("清理原因应为 1–300 字符。");
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteAsync("SELECT pg_advisory_xact_lock(721903263)", transaction: transaction);
        if (!await connection.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM creativeflow_admin WHERE admin_id = @Admin)", new { Admin = admin }, transaction))
            throw new KnowledgeException("清理需要有效管理员身份。");
        var row = await connection.QuerySingleOrDefaultAsync<KnowledgeCleanupRow>(CleanupSql, new { Id = id, Version = version }, transaction);
        if (row?.Status is "deleted" or "cleaning") return;
        var preview = CleanupResult(row);
        if (!preview.Allowed) throw new KnowledgeException(preview.Reason!);
        await connection.ExecuteAsync("UPDATE knowledge_version SET status = 'cleaning', error = NULL, updated_at = clock_timestamp() WHERE base_id = @Id AND id = @Version", new { Id = id, Version = version }, transaction);
        await connection.ExecuteAsync("INSERT INTO knowledge_cleanup_audit(id, base_id, version_id, admin_id, reason) VALUES (@Audit, @Id, @Version, @Admin, @Reason)",
            new { Audit = Guid.CreateVersion7(), Id = id, Version = version, Admin = admin, Reason = reason.Trim() }, transaction);
        await transaction.CommitAsync(ct);
    }

    /// <summary>仅显式进入 cleaning 的版本允许删除；失败任务不自动重试。</summary>
    public async Task<KnowledgeVersion?> NextCleanupAsync(CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        return (await connection.QuerySingleOrDefaultAsync<KnowledgeStoredVersionRow>("SELECT " + VersionColumns + " FROM knowledge_version WHERE status = 'cleaning' ORDER BY updated_at LIMIT 1"))?.ToModel();
    }

    /// <summary>向量集合确认删除后，在同一事务中释放分段/成员/孤立文档并保留审计墓碑。</summary>
    public async Task FinishCleanupAsync(Guid id, Guid version, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteAsync("SELECT pg_advisory_xact_lock(721903263)", transaction: transaction);
        var row = await connection.QuerySingleOrDefaultAsync<KnowledgeCleanupRow>(CleanupSql, new { Id = id, Version = version }, transaction);
        if (row?.Status != "cleaning" || row.Active || row.Pins != 0) throw new KnowledgeException("清理状态或引用已变化，已停止数据库清理。");
        await connection.ExecuteAsync("DELETE FROM knowledge_chunk WHERE version_id = @Version; DELETE FROM knowledge_version_document WHERE version_id = @Version;", new { Version = version }, transaction);
        await connection.ExecuteAsync("DELETE FROM knowledge_document d WHERE d.base_id = @Id AND NOT EXISTS (SELECT 1 FROM knowledge_version_document vd WHERE vd.document_id = d.id)", new { Id = id }, transaction);
        await connection.ExecuteAsync("UPDATE knowledge_version SET status = 'deleted', error = NULL, updated_at = clock_timestamp() WHERE id = @Version; UPDATE knowledge_cleanup_audit SET completed_at = clock_timestamp() WHERE version_id = @Version AND completed_at IS NULL;", new { Version = version }, transaction);
        await transaction.CommitAsync(ct);
    }

    /// <summary>同一固定 SQL 同时服务预检和事务内复验，防止状态判断漂移。</summary>
    private const string CleanupSql = """
        SELECT v.status, COALESCE(b.active_version = v.id, false) AS active,
            (SELECT count(*)::int FROM knowledge_version_pin WHERE version_id = v.id) AS pins, v.reserved_bytes
        FROM knowledge_version v JOIN knowledge_base b ON b.id = v.base_id WHERE b.id = @Id AND v.id = @Version
        """;

    /// <summary>活跃、激活或有任何历史引用的版本均保留；停用发布并不等于删除历史契约。</summary>
    private static KnowledgeCleanupPreview CleanupResult(KnowledgeCleanupRow? row)
    {
        var reason = row is null ? "索引版本不存在。" : row.Active ? "当前激活版本不能清理，请先切换版本。"
            : row.Pins > 0 ? "仍被工作流草稿、模板、发布版本或运行记录引用，不能清理。"
            : row.Status is "queued" or "embedding" or "writing" ? "索引任务尚未结束，不能清理。"
            : row.Status is "cleaning" or "deleted" ? "清理已受理或已完成。" : null;
        return new(reason is null, reason, row?.Pins ?? 0, row?.ReservedBytes ?? 0);
    }
}

/// <summary>清理安全检查的小型数据行，不加载文档与模型信息。</summary>
/// <param name="Status">版本状态。</param>
/// <param name="Active">是否当前激活。</param>
/// <param name="Pins">持久引用数。</param>
/// <param name="ReservedBytes">逻辑预算。</param>
internal sealed record KnowledgeCleanupRow(string Status, bool Active, int Pins, long ReservedBytes);
