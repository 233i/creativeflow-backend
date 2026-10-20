using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Contracts.Ai;

namespace CreativeFlow.Application.Features.Knowledge;

/// <summary>管理员知识试点用例；普通用户和外部 Key 尚未获得授权，因此不开放这些资源。</summary>
public sealed partial class KnowledgeLibraryService(IKnowledgeRepository repository, IKnowledgeEmbedding embedding, IKnowledgeIndexStore index,
    KnowledgeRetrievalService retrieval, KnowledgeQuotas? configuredQuotas = null)
{
    /// <summary>安全元数据可供前端提示；配额只能由部署者配置。</summary>
    public KnowledgeQuotas Quotas => (configuredQuotas ?? new()).Validate();
    /// <summary>管理预览使用与入库相同的配额。</summary>
    public KnowledgePreview Preview(KnowledgeImportRequest request) => KnowledgeTextParser.Preview(request, Quotas);
    /// <summary>向管理页返回只读的索引可用标志。</summary>
    public bool IndexAvailable => index.Available;

    /// <summary>校验元数据和已保存模型维度后创建，不发送文档或模型请求。</summary>
    public async Task<KnowledgeBaseResponse> CreateAsync(CreateKnowledgeBaseRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 80 || request.Description is null || request.Description.Length > 1000)
            throw new KnowledgeException("名称应为 1–80 字符，用途说明最多 1000 字符。");
        await embedding.CaptureAsync(request.ModelId, ct);
        return await repository.CreateAsync(request with { Name = request.Name.Trim() }, ct);
    }

    /// <summary>重新生成预览并校验用户确认摘要，再幂等入队；创建请求不等待向量化。</summary>
    public async Task<KnowledgeVersionResponse> ImportAsync(Guid id, KnowledgeImportRequest request, CancellationToken ct)
    {
        if (!index.Available) throw new KnowledgeException("索引服务未配置，当前只能预览分段。");
        var library = await RequireLibraryAsync(id, ct);
        var preview = Preview(request);
        if (request.PreviewDigest != preview.Digest) throw new KnowledgeException("文档或分段参数已变更，请重新预览并确认。");
        var snapshot = await embedding.CaptureAsync(library.ModelId, ct);
        // 预览、正文存储和片段 offset 必须使用同一份规范化文本；否则 CRLF/BOM 会让来源位置漂移。
        var normalizedDocuments = request.Documents.Select(document => document with { Text = KnowledgeTextParser.NormalizeText(document.Text) }).ToArray();
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(preview.Digest + "\n" + snapshot + "\nhybrid-v1")));
        var version = new KnowledgeVersion(Guid.CreateVersion7(), id, digest, snapshot,
            JsonSerializer.Serialize(normalizedDocuments, KnowledgeJsonContext.Default.KnowledgeDocumentInputArray),
            JsonSerializer.Serialize(preview.Chunks, KnowledgeJsonContext.Default.KnowledgeChunkArray), "{}", "queued", null, DateTimeOffset.UtcNow)
            { ChunkSize = request.ChunkSize, Overlap = request.Overlap, ParserVersion = KnowledgeTextParser.ParserVersion };
        return Summary(await repository.InsertVersionAsync(version, ct));
    }

    /// <summary>显式恢复只处理缺失向量；unknown 必须确认可能再次调用模型。</summary>
    public async Task RecoverAsync(Guid id, Guid versionId, string action, CancellationToken ct)
    {
        await RequireLibraryAsync(id, ct);
        if (!index.Available) throw new KnowledgeException("索引服务未配置。");
        if (action is not ("continue" or "reembedUnknown")) throw new KnowledgeException("恢复动作无效。");
        await using var lease = await repository.TryWorkerLockAsync(ct);
        if (lease is null) throw new KnowledgeException("索引 Worker 正在处理任务，请稍后再试。");
        var version = await repository.FindVersionMetadataAsync(id, versionId, ct) ?? throw new KnowledgeException("索引版本不存在。");
        if (version.Status != "failed" && version.Status != "unknown") throw new KnowledgeException("只能恢复失败或待核对的候选版本。");
        if (version.Status == "unknown" && action != "reembedUnknown") throw new KnowledgeException("上次向量请求状态不明；需显式确认重新向量化未确认的批次，可能再次计费。");
        if ((await repository.VersionsAsync(id, ct)).Any(v => v.Status is "queued" or "embedding" or "writing"))
            throw new KnowledgeException("该库已有排队中的候选版本。");
        await repository.SaveProgressAsync(version with { Status = "queued", Error = null }, ct);
    }

    /// <summary>只查询当前激活版本，调用前后复验停用和版本切换，不返回旧可见性内容。</summary>
    public async Task<KnowledgeSearchResponse> SearchAsync(Guid id, KnowledgeSearchRequest request, CancellationToken ct)
    {
        var library = await RequireLibraryAsync(id, ct);
        if (library.ActiveVersion is not { } active) throw new KnowledgeException("请先完成索引并激活一个版本。");
        var version = await repository.FindReadableVersionAsync(id, active, ct) ?? throw new KnowledgeException("激活版本不存在。");
        if (version.Status != "ready") throw new KnowledgeException("当前版本不可检索。");
        var settings = request.Settings ?? new KnowledgeRetrievalSettings { TopK = request.TopK };
        var rerankSnapshot = await retrieval.CaptureAsync(version, settings, ct);
        return await retrieval.SearchAsync(version, request.Query, request.OriginalQuestion, settings, rerankSnapshot, Authorize, ct);

        // 每次外部调用和交付前复验库的启用状态、版本摘要及冻结重排配置。
        async Task Authorize(CancellationToken token)
        {
            if ((await RequireLibraryAsync(id, token)).ActiveVersion != active
                || await repository.FindReadyDigestAsync(id, active, token) != version.Digest)
                throw new KnowledgeException("检索期间激活版本或知识库状态已变化，请重新检索。");
        }
    }

    /// <summary>将已有正文与已确认向量复制为混合索引候选；不重新向量化、不切换激活版本。</summary>
    public async Task<KnowledgeVersionResponse> RebuildHybridAsync(Guid id, Guid sourceVersion, CancellationToken ct)
    {
        if (!index.Available) throw new KnowledgeException("索引服务未配置。");
        var library = await RequireLibraryAsync(id, ct);
        var baseline = await repository.FindReadableVersionAsync(id, sourceVersion, ct)
            ?? throw new KnowledgeException("来源索引版本不存在。");
        if (baseline.Status != "ready") throw new KnowledgeException("只允许升级已就绪的版本。");
        if (baseline.IndexSchema == "hybrid-v1") return Summary(baseline);
        var model = await embedding.CaptureAsync(library.ModelId, ct);
        if (!JsonNode.DeepEquals(JsonNode.Parse(model), JsonNode.Parse(baseline.ModelSnapshot)))
            throw new KnowledgeException("向量模型配置已变化，不能复用旧向量。请重新导入文档建立新版本。");
        var metadata = await repository.DocumentsAsync(id, sourceVersion, ct);
        var documents = new List<KnowledgeDocumentInput>(); var chunks = new List<KnowledgeChunk>();
        foreach (var document in metadata)
        {
            var original = await repository.DocumentAsync(id, sourceVersion, document.Id, ct)
                ?? throw new KnowledgeException("来源版本正文不可用。");
            documents.Add(new(document.Name, original.Text));
            chunks.AddRange(await repository.ReadChunksAsync(id, sourceVersion, null, document.Id, 0, 8192, ct));
        }
        if (chunks.Count != baseline.Progress?.ChunkCount || chunks.Count == 0)
            throw new KnowledgeException("来源版本片段不完整，未创建候选版本。");
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("hybrid-v1\n" + baseline.Digest)));
        var candidate = new KnowledgeVersion(Guid.CreateVersion7(), id, digest, model,
            JsonSerializer.Serialize(documents.ToArray(), KnowledgeJsonContext.Default.KnowledgeDocumentInputArray),
            JsonSerializer.Serialize(chunks.ToArray(), KnowledgeJsonContext.Default.KnowledgeChunkArray), "{}", "queued", null, DateTimeOffset.UtcNow)
        { SourceVersionId = sourceVersion, ParserVersion = baseline.ParserVersion, ChunkSize = baseline.ChunkSize, Overlap = baseline.Overlap };
        return Summary(await repository.InsertVersionAsync(candidate, ct));
    }

    /// <summary>每个需要正文或新调用的入口都检查即时停用。</summary>
    private async Task<KnowledgeBaseResponse> RequireLibraryAsync(Guid id, CancellationToken ct)
    {
        var library = await repository.FindAsync(id, ct);
        return library is { Enabled: true } ? library : throw new KnowledgeException("知识库不存在或已停用。");
    }

    /// <summary>从有界持久化记录读取来源片段，不使用向量库返回的正文。</summary>
    public static KnowledgeChunk[] Chunks(KnowledgeVersion version) =>
        JsonSerializer.Deserialize(version.ChunksJson, KnowledgeJsonContext.Default.KnowledgeChunkArray)!;

    /// <summary>向量只在后端内部反序列化。</summary>
    public static Dictionary<string, float[]> Vectors(KnowledgeVersion version) =>
        JsonSerializer.Deserialize(version.VectorsJson, KnowledgeJsonContext.Default.DictionaryStringSingleArray)!;

    /// <summary>转换为不包含原文、密钥或向量的任务进度。</summary>
    public static KnowledgeVersionResponse Summary(KnowledgeVersion version)
    {
        var progress = version.Progress ?? new(version.Id, version.Status, version.Error,
            JsonNode.Parse(version.DocumentsJson)!.AsArray().Count, Chunks(version).Length, Vectors(version).Count, version.CreatedAt);
        return progress with { ParserVersion = version.ParserVersion, IndexSchema = version.IndexSchema };
    }
}
