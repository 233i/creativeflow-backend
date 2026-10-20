using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Application.Features.Knowledge;

namespace CreativeFlow.Infrastructure.Persistence;

public sealed partial class KnowledgeRepository
{
    /// <summary>在事务内创建完整候选的文档关联与分段；同模型、同片段的已确认向量可安全复用。</summary>
    private async Task<KnowledgeVersion> InsertNormalizedAsync(KnowledgeVersion version, CancellationToken ct)
    {
        var documents = JsonSerializer.Deserialize(version.DocumentsJson, KnowledgeJsonContext.Default.KnowledgeDocumentInputArray)!;
        var chunks = KnowledgeLibraryService.Chunks(version);
        var dimensions = JsonNode.Parse(version.ModelSnapshot)?["dimensions"]?.GetValue<int>() ?? 0;
        if (dimensions is < 1 or > 8192) throw new KnowledgeException("索引模型维度无效。");
        // 除原文和稠密向量外，为稀疏词项与标识索引预留空间；这是逻辑配额，不宣称等于实际磁盘占用。
        var reserved = documents.Sum(d => (long)Encoding.UTF8.GetByteCount(d.Text)) + chunks.Sum(c =>
            (long)Encoding.UTF8.GetByteCount(c.Text) + dimensions * 4L + 256
            + (version.IndexSchema == "hybrid-v1" ? (c.Text.Length + c.SectionPath.Length) * 16L + 512 : 0));
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        // 与激活、清理、图引用捕获采用一致锁顺序，避免标记清理后新引用穿过。
        await connection.ExecuteAsync("SELECT pg_advisory_xact_lock(721903263)", transaction: transaction);
        if (!await connection.QuerySingleOrDefaultAsync<bool>("SELECT enabled FROM knowledge_base WHERE id = @BaseId FOR UPDATE", new { version.BaseId }, transaction))
            throw new KnowledgeException("知识库不存在或已停用。");
        var existing = await connection.QuerySingleOrDefaultAsync<KnowledgeStoredVersionRow>("SELECT " + VersionColumns + " FROM knowledge_version WHERE base_id = @BaseId AND digest = @Digest AND status <> 'deleted'", new { version.BaseId, version.Digest }, transaction);
        if (existing is not null)
        {
            if (existing.Status is "cleaning" or "cleanup_failed") throw new KnowledgeException("相同资料正在清理，请完成清理后重新预览。");
            return existing.ToModel();
        }
        if (version.SourceVersionId is { } parent && !await connection.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM knowledge_version WHERE base_id = @BaseId AND id = @Parent AND status = 'ready')", new { version.BaseId, Parent = parent }, transaction))
            throw new KnowledgeException("变更基线已不可用，请重新选择并预览文档。");
        var count = await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM knowledge_version WHERE base_id = @BaseId AND status <> 'deleted'", new { version.BaseId }, transaction);
        var active = await connection.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM knowledge_version WHERE base_id = @BaseId AND status IN ('queued','embedding','writing'))", new { version.BaseId }, transaction);
        var used = await connection.ExecuteScalarAsync<long>("SELECT COALESCE(sum(reserved_bytes), 0)::bigint FROM knowledge_version WHERE base_id = @BaseId AND status <> 'deleted'", new { version.BaseId }, transaction);
        if (active) throw new KnowledgeException("该库已有排队或执行中的索引，请先完成当前候选。");
        if (count >= Quotas.MaxVersions || used + reserved > Quotas.MaxLibraryReservedBytes)
            throw new KnowledgeException("知识库版本数或逻辑存储预留已达配置配额，请清理未引用版本或由部署者调整预算。");
        await connection.ExecuteAsync("""
            INSERT INTO knowledge_version(id, base_id, digest, model_snapshot, status, created_at, document_count, chunk_count, reserved_bytes, source_version_id, chunk_size, overlap, number, parser_version, index_schema)
            VALUES (@Id, @BaseId, @Digest, @ModelSnapshot::jsonb, 'queued', @CreatedAt, @DocumentCount, @ChunkCount, @Reserved, @SourceVersionId, @ChunkSize, @Overlap,
                (SELECT COALESCE(max(number), 0) + 1 FROM knowledge_version WHERE base_id = @BaseId), @ParserVersion, @IndexSchema)
            """, new { version.Id, version.BaseId, version.Digest, version.ModelSnapshot, version.CreatedAt, DocumentCount = documents.Length,
                ChunkCount = chunks.Length, Reserved = reserved, version.SourceVersionId, version.ChunkSize, version.Overlap, version.ParserVersion, version.IndexSchema }, transaction);
        // JSON 只作有界批量传输，不再持久化到版本行；三个批量 SQL 避免每分段一次远程往返。
        await connection.ExecuteAsync("""
            INSERT INTO knowledge_document(id, base_id, digest, name, body, byte_count)
            SELECT gen_random_uuid(), @BaseId, encode(sha256(convert_to(jsonb_build_array(d->>'name', d->>'text')::text, 'UTF8')), 'hex'),
                d->>'name', d->>'text', octet_length(d->>'text') FROM jsonb_array_elements(@DocumentsJson::jsonb) d
            ON CONFLICT(base_id, digest) DO NOTHING
            """, new { version.BaseId, version.DocumentsJson }, transaction);
        await connection.ExecuteAsync("""
            INSERT INTO knowledge_version_document(version_id, document_id, ordinal)
            SELECT @Id, d.id, item.ordinality::int FROM jsonb_array_elements(@DocumentsJson::jsonb) WITH ORDINALITY item(value, ordinality)
            JOIN knowledge_document d ON d.base_id = @BaseId
                AND d.digest = encode(sha256(convert_to(jsonb_build_array(item.value->>'name', item.value->>'text')::text, 'UTF8')), 'hex')
            """, new { version.Id, version.BaseId, version.DocumentsJson }, transaction);
        await connection.ExecuteAsync("""
            INSERT INTO knowledge_chunk(version_id, id, document_id, ordinal, start_line, end_line, body, vector, start_offset, end_offset, section_path, block_type, continuation)
            SELECT @Id, (item.value->>'id')::uuid, d.id, item.ordinality::int, (item.value->>'startLine')::int,
                (item.value->>'endLine')::int, item.value->>'text',
                (SELECT c.vector FROM knowledge_chunk c JOIN knowledge_version v ON v.id = c.version_id
                 WHERE c.id = (item.value->>'id')::uuid AND c.body = item.value->>'text' AND v.base_id = @BaseId
                     AND v.model_snapshot = @ModelSnapshot::jsonb AND c.vector IS NOT NULL AND v.status NOT IN ('cleaning','cleanup_failed','deleted')
                 ORDER BY v.created_at DESC LIMIT 1),
                COALESCE((item.value->>'startOffset')::int, 0), COALESCE((item.value->>'endOffset')::int, 0),
                COALESCE(item.value->>'sectionPath', ''), COALESCE(item.value->>'blockType', 'paragraph'),
                COALESCE((item.value->>'continuation')::boolean, false)
            FROM jsonb_array_elements(@ChunksJson::jsonb) WITH ORDINALITY item(value, ordinality)
            JOIN knowledge_version_document vd ON vd.version_id = @Id
            JOIN knowledge_document d ON d.id = vd.document_id AND d.name = item.value->>'document'
            """, new { version.Id, version.BaseId, version.ChunksJson, version.ModelSnapshot }, transaction);
        await connection.ExecuteAsync("UPDATE knowledge_version SET embedded_count = (SELECT count(*) FROM knowledge_chunk WHERE version_id = @Id AND vector IS NOT NULL), reused_count = (SELECT count(*) FROM knowledge_chunk WHERE version_id = @Id AND vector IS NOT NULL) WHERE id = @Id", new { version.Id }, transaction);
        var saved = await connection.QuerySingleAsync<KnowledgeStoredVersionRow>("SELECT " + VersionColumns + " FROM knowledge_version WHERE id = @Id", new { version.Id }, transaction);
        await transaction.CommitAsync(ct); return saved.ToModel();
    }

    /// <summary>文档页只聚合有界元数据，文本按需单篇读取。</summary>
    public async Task<KnowledgeDocumentSummary[]> DocumentsAsync(Guid id, Guid version, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<KnowledgeDocumentSummary>("""
            SELECT d.id, d.name, d.byte_count, (SELECT count(*)::int FROM knowledge_chunk c WHERE c.version_id = @Version AND c.document_id = d.id) AS chunk_count
            FROM knowledge_version_document vd JOIN knowledge_document d ON d.id = vd.document_id JOIN knowledge_version v ON v.id = vd.version_id
            WHERE vd.version_id = @Version AND d.base_id = @Id AND v.status NOT IN ('cleaning','cleanup_failed','deleted') ORDER BY vd.ordinal
            """, new { Id = id, Version = version })).ToArray();
    }

    /// <summary>修订正文同时固定知识库、版本成员关系和修订 ID。</summary>
    public async Task<KnowledgeDocumentInput?> DocumentAsync(Guid id, Guid version, Guid document, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<KnowledgeDocumentInput>("""
            SELECT d.name, d.body AS text FROM knowledge_document d JOIN knowledge_version_document vd ON vd.document_id = d.id JOIN knowledge_version v ON v.id = vd.version_id
            WHERE d.base_id = @Id AND vd.version_id = @Version AND d.id = @Document AND v.status NOT IN ('cleaning','cleanup_failed','deleted')
            """, new { Id = id, Version = version, Document = document });
    }

    /// <summary>检索通过特定 ID 查询，管理页分页查询；两者都不读取向量。</summary>
    public async Task<KnowledgeChunk[]> ReadChunksAsync(Guid id, Guid version, Guid[]? ids, Guid? document, int offset, int limit, CancellationToken ct)
    {
        if (offset < 0 || limit is < 1 or > 8192 || ids?.Length > 192) throw new KnowledgeException("分段读取范围无效。");
        await using var connection = await source.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<KnowledgeChunk>("""
            SELECT c.id, d.name AS document, c.start_line, c.end_line, c.body AS text,
                c.start_offset AS "StartOffset", c.end_offset AS "EndOffset", c.section_path AS "SectionPath",
                c.block_type AS "BlockType", c.continuation AS "Continuation", c.document_id AS "DocumentId", c.ordinal AS "Ordinal" FROM knowledge_chunk c
            JOIN knowledge_document d ON d.id = c.document_id JOIN knowledge_version v ON v.id = c.version_id
            WHERE c.version_id = @Version AND d.base_id = @Id AND v.status NOT IN ('cleaning','cleanup_failed','deleted') AND (@Ids::uuid[] IS NULL OR c.id = ANY(@Ids))
                AND (@Document::uuid IS NULL OR c.document_id = @Document)
            ORDER BY c.ordinal LIMIT @Limit OFFSET @Offset
            """, new { Id = id, Version = version, Ids = ids, Document = document, Offset = offset, Limit = limit })).ToArray();
    }

    /// <summary>按同一文档的稳定顺序读取命中片段邻接窗口，不跨文档或版本扩展。</summary>
    public async Task<KnowledgeChunk[]> ReadAdjacentChunksAsync(Guid id, Guid version, Guid[] seedIds, int radius, CancellationToken ct)
    {
        if (seedIds.Length is < 1 or > 32 || radius is < 1 or > 2) throw new KnowledgeException("邻接分段范围无效。");
        await using var connection = await source.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<KnowledgeChunk>("""
            WITH seeds AS (
                SELECT document_id, ordinal FROM knowledge_chunk
                WHERE version_id = @Version AND id = ANY(@Seeds)
            )
            SELECT DISTINCT c.id, d.name AS document, c.start_line, c.end_line, c.body AS text,
                c.start_offset AS "StartOffset", c.end_offset AS "EndOffset", c.section_path AS "SectionPath",
                c.block_type AS "BlockType", c.continuation AS "Continuation", c.document_id AS "DocumentId", c.ordinal AS "Ordinal"
            FROM knowledge_chunk c JOIN knowledge_document d ON d.id = c.document_id
            JOIN seeds s ON s.document_id = c.document_id AND c.ordinal BETWEEN s.ordinal - @Radius AND s.ordinal + @Radius
            WHERE c.version_id = @Version AND d.base_id = @Id
            ORDER BY c.ordinal
            """, new { Id = id, Version = version, Seeds = seedIds, Radius = radius })).ToArray();
    }

    /// <summary>待向量化批次最多十六段，内存占用不随已入库向量数量增长。</summary>
    public async Task<KnowledgeChunk[]> PendingChunksAsync(Guid id, Guid version, int limit, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<KnowledgeChunk>("""
            SELECT c.id, d.name AS document, c.start_line, c.end_line, c.body AS text,
                c.start_offset AS "StartOffset", c.end_offset AS "EndOffset", c.section_path AS "SectionPath",
                c.block_type AS "BlockType", c.continuation AS "Continuation", c.document_id AS "DocumentId", c.ordinal AS "Ordinal" FROM knowledge_chunk c
            JOIN knowledge_document d ON d.id = c.document_id WHERE c.version_id = @Version AND d.base_id = @Id AND c.vector IS NULL
            ORDER BY c.ordinal LIMIT @Limit
            """, new { Id = id, Version = version, Limit = Math.Clamp(limit, 1, 16) })).ToArray();
    }

    /// <summary>只有调用方核验完整批次后才写入，事务失败则保留 embedding 待核对语义。</summary>
    public async Task SaveVectorBatchAsync(KnowledgeVersion version, IReadOnlyList<EmbeddingVector> vectors, CancellationToken ct)
    {
        if (vectors.Count is < 1 or > 16) throw new KnowledgeException("向量检查点批次大小无效。");
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        foreach (var vector in vectors)
        {
            if (!Guid.TryParse(vector.Id, out var chunk) || vector.Values.Length is < 1 or > 8192 || vector.Values.Any(n => !float.IsFinite(n))) throw new KnowledgeException("向量检查点无效。");
            var updated = await connection.ExecuteAsync("""
                UPDATE knowledge_chunk c SET vector = @Vector FROM knowledge_version v
                WHERE c.version_id = v.id AND v.id = @Version AND v.base_id = @BaseId AND v.status = 'embedding' AND c.id = @Chunk AND c.vector IS NULL
                """, new { Vector = vector.Values, Version = version.Id, version.BaseId, Chunk = chunk }, transaction);
            if (updated != 1) throw new KnowledgeException("向量检查点与当前批次不一致。");
        }
        await connection.ExecuteAsync("UPDATE knowledge_version SET embedded_count = (SELECT count(*) FROM knowledge_chunk WHERE version_id = @Id AND vector IS NOT NULL), status = 'queued', error = NULL, updated_at = clock_timestamp() WHERE id = @Id", new { version.Id }, transaction);
        await transaction.CommitAsync(ct);
    }

    /// <summary>写入阶段只取一个未确认批次，重启后稳定 ID 的重复 upsert 无副作用。</summary>
    public async Task<EmbeddingVector[]> PendingWritesAsync(Guid id, Guid version, int limit, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<KnowledgeVectorRow>("""
            SELECT c.id, c.vector FROM knowledge_chunk c JOIN knowledge_version v ON v.id = c.version_id
            WHERE v.base_id = @Id AND v.id = @Version AND c.vector IS NOT NULL AND NOT c.written ORDER BY c.ordinal LIMIT @Limit
            """, new { Id = id, Version = version, Limit = Math.Clamp(limit, 1, 16) })).Select(row => new EmbeddingVector(row.Id.ToString(), row.Vector)).ToArray();
    }

    /// <summary>写入标记与摘要一起保存，不使用查询页面触发索引重写。</summary>
    public async Task MarkWrittenAsync(Guid id, Guid version, Guid[] chunks, CancellationToken ct)
    {
        if (chunks.Length is < 1 or > 16) throw new KnowledgeException("写入检查点批次大小无效。");
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteAsync("UPDATE knowledge_chunk c SET written = true FROM knowledge_version v WHERE c.version_id = v.id AND v.id = @Version AND v.base_id = @Id AND c.id = ANY(@Chunks) AND c.vector IS NOT NULL", new { Id = id, Version = version, Chunks = chunks }, transaction);
        await connection.ExecuteAsync("UPDATE knowledge_version SET written_count = (SELECT count(*) FROM knowledge_chunk WHERE version_id = @Version AND written), updated_at = clock_timestamp() WHERE base_id = @Id AND id = @Version", new { Id = id, Version = version }, transaction);
        await transaction.CommitAsync(ct);
    }
}
