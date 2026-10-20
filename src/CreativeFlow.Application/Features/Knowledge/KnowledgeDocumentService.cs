using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Contracts.Ai;

namespace CreativeFlow.Application.Features.Knowledge;

public sealed partial class KnowledgeLibraryService
{
    /// <summary>将差量操作合成为完整新版本并预览，保留所有未提及的文档，不触发任何向量请求。</summary>
    public async Task<KnowledgePreview> PreviewChangesAsync(Guid id, KnowledgeDocumentChangeRequest request, CancellationToken ct)
    {
        var input = await ComposeChangesAsync(id, request, ct);
        return ChangePreview(request.SourceVersionId, Preview(input));
    }

    /// <summary>确认摘要绑定基线及变更后的完整资料，重复提交复用同一个候选，不修改当前激活指针。</summary>
    public async Task<KnowledgeVersionResponse> ChangeDocumentsAsync(Guid id, KnowledgeDocumentChangeRequest request, CancellationToken ct)
    {
        if (!index.Available) throw new KnowledgeException("索引服务未配置，当前只能预览。");
        var input = await ComposeChangesAsync(id, request, ct);
        var preview = ChangePreview(request.SourceVersionId, Preview(input));
        if (preview.Digest != request.PreviewDigest) throw new KnowledgeException("文档、基线或分段规则已变化，请重新预览确认。");
        var library = await RequireLibraryAsync(id, ct);
        var snapshot = await embedding.CaptureAsync(library.ModelId, ct);
        // 与原始导入保持同一规范化边界，确保新版本保存的正文与 chunk offset 一一对应。
        var normalizedDocuments = input.Documents.Select(document => document with { Text = KnowledgeTextParser.NormalizeText(document.Text) }).ToArray();
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(preview.Digest + "\n" + snapshot + "\nhybrid-v1")));
        var version = new KnowledgeVersion(Guid.CreateVersion7(), id, digest, snapshot,
            JsonSerializer.Serialize(normalizedDocuments, KnowledgeJsonContext.Default.KnowledgeDocumentInputArray),
            JsonSerializer.Serialize(preview.Chunks, KnowledgeJsonContext.Default.KnowledgeChunkArray), "{}", "queued", null, DateTimeOffset.UtcNow)
        { SourceVersionId = request.SourceVersionId, ChunkSize = request.ChunkSize, Overlap = request.Overlap, ParserVersion = KnowledgeTextParser.ParserVersion };
        return Summary(await repository.InsertVersionAsync(version, ct));
    }

    /// <summary>原文从指定版本成员关系读取；新增不可覆盖、替换/删除需同时匹配原修订 ID 和名称。</summary>
    private async Task<KnowledgeImportRequest> ComposeChangesAsync(Guid id, KnowledgeDocumentChangeRequest request, CancellationToken ct)
    {
        await RequireLibraryAsync(id, ct);
        if (await repository.FindReadyDigestAsync(id, request.SourceVersionId, ct) is null) throw new KnowledgeException("请选择已就绪的完整版本作为变更基线。");
        if (request.Changes is not { Length: > 0 } || request.Changes.Length > Quotas.MaxDocuments) throw new KnowledgeException("文档变更数量超出配置范围。");
        var originals = await repository.DocumentsAsync(id, request.SourceVersionId, ct);
        // 基线来自用户明确选择的版本，不跟随激活指针；替换/删除同时核验名称和修订 ID，防止覆盖另一版资料。
        var remaining = originals.ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);
        var replacements = new Dictionary<string, KnowledgeDocumentInput>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in request.Changes)
        {
            if (change is null || string.IsNullOrWhiteSpace(change.Name) || !seen.Add(change.Name)) throw new KnowledgeException("一个文件只能提交一次变更。");
            if (change.Action == "add")
            {
                if (remaining.ContainsKey(change.Name) || change.DocumentId is not null || change.Text is null) throw new KnowledgeException("新增文档重名或参数无效；更新原文件请使用替换操作。");
                replacements.Add(change.Name, new(change.Name, change.Text));
            }
            else if (change.Action is "replace" or "remove")
            {
                if (!remaining.TryGetValue(change.Name, out var original) || original.Id != change.DocumentId || original.Name != change.Name)
                    throw new KnowledgeException("原文档修订与所选基线不一致，请重新读取文档列表。");
                remaining.Remove(change.Name);
                if (change.Action == "replace")
                {
                    if (change.Text is null) throw new KnowledgeException("替换文档需要正文。");
                    replacements.Add(change.Name, new(change.Name, change.Text));
                }
                else if (change.Text is not null) throw new KnowledgeException("移除文档不能同时提交正文。");
            }
            else throw new KnowledgeException("文档操作只支持 add、replace、remove。");
        }
        if (remaining.Count + replacements.Count == 0) throw new KnowledgeException("不能生成空索引版本；需停止提供全部知识时请停用知识库。");
        var documents = new List<KnowledgeDocumentInput>();
        foreach (var original in originals)
        {
            if (replacements.Remove(original.Name, out var replacement)) documents.Add(replacement);
            else if (remaining.ContainsKey(original.Name)) documents.Add(await repository.DocumentAsync(id, request.SourceVersionId, original.Id, ct)
                ?? throw new KnowledgeException("基线文档已不可读，请重新选择版本。"));
        }
        documents.AddRange(replacements.Values);
        if (await repository.FindReadyDigestAsync(id, request.SourceVersionId, ct) is null) throw new KnowledgeException("基线版本已不可用。");
        return new(documents.ToArray(), request.ChunkSize, request.Overlap);
    }

    /// <summary>摘要绑定明确基线，防止同内容在另一个旧版本上被误确认。</summary>
    private static KnowledgePreview ChangePreview(Guid source, KnowledgePreview preview) => preview with
    { Digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("document-change-v1\n" + source + "\n" + preview.Digest))) };
}
