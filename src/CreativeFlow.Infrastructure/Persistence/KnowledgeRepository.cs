using Dapper;
using Npgsql;
using System.Text.Json;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Application.Features.Knowledge;

namespace CreativeFlow.Infrastructure.Persistence;

/// <summary>知识资源专用 PostgreSQL 仓储；会话锁仅保护新索引队列，不占用旧任务锁。</summary>
public sealed partial class KnowledgeRepository(NpgsqlDataSource source, KnowledgeQuotas? configuredQuotas = null) : IKnowledgeRepository
{
    /// <summary>未显式配置时保持保守试点额度，注册层会验证部署者给定的上限。</summary>
    private KnowledgeQuotas Quotas => (configuredQuotas ?? new()).Validate();
    /// <summary>版本查询的固定列，不读取任意客户端 SQL。</summary>
    private const string VersionColumns = "id, base_id, digest, model_snapshot::text AS model_snapshot, status, error, created_at, document_count, chunk_count, embedded_count, written_count, reused_count, reserved_bytes, source_version_id, chunk_size, overlap, number, parser_version, index_schema";

    /// <summary>最多返回十个试点知识库的元数据。</summary>
    public async Task<KnowledgeBaseResponse[]> ListAsync(CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<KnowledgeBaseResponse>("SELECT id, name, description, model_id, enabled, active_version FROM knowledge_base ORDER BY created_at DESC LIMIT 100")).ToArray();
    }

    /// <summary>库不存在时不返回其他资源信息。</summary>
    public async Task<KnowledgeBaseResponse?> FindAsync(Guid id, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<KnowledgeBaseResponse>("SELECT id, name, description, model_id, enabled, active_version FROM knowledge_base WHERE id = @Id", new { Id = id });
    }

    /// <summary>串行检查试点配额并新增，不覆盖同名旧库。</summary>
    public async Task<KnowledgeBaseResponse> CreateAsync(CreateKnowledgeBaseRequest request, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteAsync("SELECT pg_advisory_xact_lock(721903261)", transaction: transaction);
        var count = await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM knowledge_base", transaction: transaction);
        if (count >= Quotas.MaxLibraries) throw new KnowledgeException($"已达到 {Quotas.MaxLibraries} 个知识库的配置上限。");
        var id = Guid.CreateVersion7();
        await connection.ExecuteAsync("INSERT INTO knowledge_base(id, name, description, model_id) VALUES (@Id, @Name, @Description, @ModelId)",
            new { Id = id, request.Name, request.Description, request.ModelId }, transaction);
        await transaction.CommitAsync(ct);
        return new(id, request.Name, request.Description, request.ModelId, true, null);
    }

    /// <summary>停用在数据库立即生效；不删除可恢复数据。</summary>
    public async Task SetEnabledAsync(Guid id, bool enabled, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await connection.ExecuteAsync("UPDATE knowledge_base SET enabled = @Enabled WHERE id = @Id", new { Id = id, Enabled = enabled });
    }

    /// <summary>只在数据库计算进度，列表不加载原文、向量或模型快照。</summary>
    public async Task<KnowledgeVersionResponse[]> VersionsAsync(Guid id, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<KnowledgeStoredVersionRow>("SELECT " + VersionColumns + " FROM knowledge_version WHERE base_id = @Id AND status <> 'deleted' ORDER BY created_at DESC LIMIT @Limit",
            new { Id = id, Limit = 100 })).Select(row => row.ToModel().Progress!).ToArray();
    }

    /// <summary>所有单版本查询都同时固定归属，防止跨库读取。</summary>
    public async Task<KnowledgeVersion?> FindVersionAsync(Guid id, Guid version, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        var metadata = (await connection.QuerySingleOrDefaultAsync<KnowledgeStoredVersionRow>("SELECT " + VersionColumns + " FROM knowledge_version WHERE base_id = @Id AND id = @Version", new { Id = id, Version = version }))?.ToModel();
        if (metadata is null) return null;
        var documents = (await connection.QueryAsync<KnowledgeDocumentInput>("SELECT d.name, d.body AS text FROM knowledge_version_document vd JOIN knowledge_document d ON d.id = vd.document_id WHERE vd.version_id = @Version ORDER BY vd.ordinal", new { Version = version })).ToArray();
        var chunks = await ReadChunksAsync(id, version, null, null, 0, 8192, ct);
        var vectors = (await connection.QueryAsync<KnowledgeVectorRow>("SELECT id, vector FROM knowledge_chunk WHERE version_id = @Version AND vector IS NOT NULL", new { Version = version })).ToDictionary(row => row.Id.ToString(), row => row.Vector);
        return metadata with { DocumentsJson = JsonSerializer.Serialize(documents, KnowledgeJsonContext.Default.KnowledgeDocumentInputArray),
            ChunksJson = JsonSerializer.Serialize(chunks, KnowledgeJsonContext.Default.KnowledgeChunkArray),
            VectorsJson = JsonSerializer.Serialize(vectors, KnowledgeJsonContext.Default.DictionaryStringSingleArray) };
    }

    /// <summary>恢复等管理动作读取小元数据，向量始终由 Worker 按批取用。</summary>
    public async Task<KnowledgeVersion?> FindVersionMetadataAsync(Guid id, Guid version, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        return (await connection.QuerySingleOrDefaultAsync<KnowledgeStoredVersionRow>("SELECT " + VersionColumns + " FROM knowledge_version WHERE base_id = @Id AND id = @Version", new { Id = id, Version = version }))?.ToModel();
    }

    /// <summary>RAG 读取只保留固定版本的模型和摘要，命中分段通过单独 ID 查询。</summary>
    public async Task<KnowledgeVersion?> FindReadableVersionAsync(Guid id, Guid version, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        return (await connection.QuerySingleOrDefaultAsync<KnowledgeStoredVersionRow>("SELECT " + VersionColumns + " FROM knowledge_version WHERE base_id = @Id AND id = @Version AND status = 'ready'", new { Id = id, Version = version }))?.ToModel();
    }

    /// <summary>权限复验只读取小摘要，不反序列化向量或片段。</summary>
    public async Task<string?> FindReadyDigestAsync(Guid id, Guid version, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<string>("SELECT digest FROM knowledge_version WHERE base_id = @Id AND id = @Version AND status = 'ready'", new { Id = id, Version = version });
    }

    /// <summary>按库加行锁后去重、检查十版本和单候选限额；重复确认返回原任务。</summary>
    public Task<KnowledgeVersion> InsertVersionAsync(KnowledgeVersion version, CancellationToken ct) => InsertNormalizedAsync(version, ct);

    /// <summary>原子切换至完整就绪的同库版本，失败候选永远不能替换当前索引。</summary>
    public async Task<bool> ActivateAsync(Guid id, Guid version, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteAsync("SELECT pg_advisory_xact_lock(721903263)", transaction: transaction);
        var changed = await connection.ExecuteAsync("""
            UPDATE knowledge_base b SET active_version = @Version WHERE b.id = @Id AND b.enabled
            AND EXISTS (SELECT 1 FROM knowledge_version v WHERE v.base_id = b.id AND v.id = @Version AND v.status = 'ready')
            """, new { Id = id, Version = version }, transaction) == 1;
        await transaction.CommitAsync(ct); return changed;
    }

    /// <summary>在独占连接上持有宿主锁，断连会释放；不依赖不可靠的进程内锁。</summary>
    public async Task<IAsyncDisposable?> TryWorkerLockAsync(CancellationToken ct)
    {
        var connection = await source.OpenConnectionAsync(ct);
        try
        {
            if (await connection.ExecuteScalarAsync<bool>("SELECT pg_try_advisory_lock(721903262)")) return new WorkerLease(connection);
            await connection.DisposeAsync(); return null;
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    /// <summary>仅在持有宿主锁时读取待处理任务。</summary>
    public async Task<KnowledgeVersion?> NextAsync(CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        return (await connection.QuerySingleOrDefaultAsync<KnowledgeStoredVersionRow>("SELECT " + VersionColumns + " FROM knowledge_version WHERE status IN ('queued','embedding','writing') ORDER BY created_at LIMIT 1"))?.ToModel();
    }

    /// <summary>状态与已完成向量同一语句保存，重启后可从检查点重写 Qdrant。</summary>
    public async Task SaveProgressAsync(KnowledgeVersion version, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        // 显式恢复 writing/failed 的完整向量版本时允许幂等重写全部索引，修复集合丢失或计数不一致。
        if (version.Status == "queued")
            await connection.ExecuteAsync("UPDATE knowledge_chunk c SET written = false FROM knowledge_version v WHERE c.version_id = v.id AND v.id = @Id AND v.base_id = @BaseId AND v.status IN ('failed','unknown')", new { version.Id, version.BaseId });
        await connection.ExecuteAsync("""
            UPDATE knowledge_version SET status = @Status, error = @Error,
                written_count = (SELECT count(*) FROM knowledge_chunk WHERE version_id = @Id AND written),
                updated_at = clock_timestamp() WHERE id = @Id AND base_id = @BaseId
            """, version);
    }

    /// <summary>释放 PostgreSQL 会话锁后归还连接；不能将带锁连接放回池。</summary>
    private sealed class WorkerLease(NpgsqlConnection connection) : IAsyncDisposable
    {
        /// <summary>显式解锁；若连接已失效则关闭它，不重复处理任何任务。</summary>
        public async ValueTask DisposeAsync()
        {
            try { await connection.ExecuteAsync("SELECT pg_advisory_unlock(721903262)"); }
            finally { await connection.DisposeAsync(); }
        }
    }
}

/// <summary>Npgsql 的 timestamptz 读取为 UTC DateTime，在持久化边界显式转换业务时间。</summary>
/// <param name="Id">版本标识。</param>
/// <param name="BaseId">归属库。</param>
/// <param name="Digest">组合摘要。</param>
/// <param name="ModelSnapshot">非敏感模型快照。</param>
/// <param name="Status">任务状态。</param>
/// <param name="Error">安全错误。</param>
/// <param name="CreatedAt">数据库 UTC 时间。</param>
/// <param name="DocumentCount">文档总数。</param>
/// <param name="ChunkCount">分段总数。</param>
/// <param name="EmbeddedCount">持久化向量数。</param>
/// <param name="WrittenCount">索引写入数。</param>
/// <param name="ReusedCount">复用向量数。</param>
/// <param name="ReservedBytes">保守存储预算。</param>
/// <param name="SourceVersionId">变更基线。</param>
/// <param name="ChunkSize">分段长度。</param>
/// <param name="Overlap">重叠字符。</param>
/// <param name="Number">固定版本序号。</param>
/// <param name="IndexSchema">固定索引的 dense-v1 或 hybrid-v1 布局。</param>
internal sealed record KnowledgeStoredVersionRow(Guid Id, Guid BaseId, string Digest, string ModelSnapshot,
    string Status, string? Error, DateTime CreatedAt, int DocumentCount, int ChunkCount, int EmbeddedCount,
    int WrittenCount, int ReusedCount, long ReservedBytes, Guid? SourceVersionId, int? ChunkSize, int? Overlap, int Number, string ParserVersion, string IndexSchema)
{
    /// <summary>保持公开业务时间为 DateTimeOffset，不依赖 Dapper.AOT 隐式强制转换。</summary>
    public KnowledgeVersion ToModel() => new(Id, BaseId, Digest, ModelSnapshot, "[]", "[]", "{}", Status, Error, new DateTimeOffset(CreatedAt))
    {
        SourceVersionId = SourceVersionId, ChunkSize = ChunkSize, Overlap = Overlap,
        Progress = new(Id, Status, Error, DocumentCount, ChunkCount, EmbeddedCount, new DateTimeOffset(CreatedAt))
        { WrittenCount = WrittenCount, ReusedCount = ReusedCount, SourceVersionId = SourceVersionId, ChunkSize = ChunkSize, Overlap = Overlap, ReservedBytes = ReservedBytes, Number = Number, ParserVersion = ParserVersion, IndexSchema = IndexSchema },
        ParserVersion = ParserVersion, IndexSchema = IndexSchema,
    };
}

/// <summary>向量批次内部行，不直接用于 HTTP 序列化。</summary>
/// <param name="Id">稳定片段标识。</param>
/// <param name="Vector">单片段浮点向量。</param>
internal sealed record KnowledgeVectorRow(Guid Id, float[] Vector);
