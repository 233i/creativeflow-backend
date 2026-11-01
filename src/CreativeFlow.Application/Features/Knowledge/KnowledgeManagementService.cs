using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Contracts.Ai;

namespace CreativeFlow.Application.Features.Knowledge;

/// <summary>知识库归档与版本对比；只读取不可变正文，不调用向量、重排或聊天模型。</summary>
public sealed class KnowledgeManagementService(IKnowledgeManagementRepository repository)
{
    /// <summary>逐版本写入临时归档，内存不累计全库正文；流关闭时临时文件自动删除。</summary>
    public async Task<Stream> ExportAsync(Guid id, Guid? version, CancellationToken ct)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite,
            Share = FileShare.None, BufferSize = 65536, Options = FileOptions.Asynchronous | FileOptions.DeleteOnClose | FileOptions.SequentialScan };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var stream = new FileStream(Path.Combine(Path.GetTempPath(), "creativeflow-knowledge-" + Guid.NewGuid().ToString("N") + ".zip"), options);
        try
        {
            var versions = new List<KnowledgeExportVersion>(); KnowledgeBaseResponse? library = null;
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
            {
                await repository.ReadVersionsAsync(id, version, (metadata, _) => { library = metadata; return Task.CompletedTask; }, async (metadata, documents, token) =>
                {
                    var manifest = new List<KnowledgeExportDocument>();
                    for (var i = 0; i < documents.Length; i++)
                    {
                        token.ThrowIfCancellationRequested(); var document = documents[i];
                        // 原始名称只进入 JSON，归档路径使用服务器编号，禁止路径穿越或重名覆盖。
                        var path = $"versions/v{metadata.Number}-{metadata.Id:D}/documents/{i + 1:D4}{(document.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? ".md" : ".txt")}";
                        var bytes = Encoding.UTF8.GetBytes(document.Text);
                        await using var body = archive.CreateEntry(path, CompressionLevel.Fastest).Open();
                        await body.WriteAsync(bytes, token);
                        manifest.Add(new(document.Name, path, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length));
                    }
                    versions.Add(new(metadata, manifest.ToArray()));
                }, ct);
                await using var entry = archive.CreateEntry("manifest.json", CompressionLevel.Fastest).Open();
                await JsonSerializer.SerializeAsync(entry, new KnowledgeExportManifest(1, DateTimeOffset.UtcNow, library!, versions.ToArray()), KnowledgeJsonContext.Default.KnowledgeExportManifest, ct);
            }
            stream.Position = 0; return stream;
        }
        catch { await stream.DisposeAsync(); throw; }
    }

    /// <summary>按名称匹配两个完整版本，正文相同但切片数变化仍返回各自真实计数。</summary>
    public async Task<KnowledgeVersionComparison> CompareAsync(Guid id, Guid from, Guid to, CancellationToken ct)
    {
        var data = await repository.ComparisonAsync(id, from, to, ct);
        var old = data.FromDocuments.ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);
        var next = data.ToDocuments.ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);
        var oldMetadata = data.FromSummaries.ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);
        var nextMetadata = data.ToSummaries.ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);
        var documents = old.Keys.Concat(next.Keys).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).Select(name =>
        {
            var a = old.GetValueOrDefault(name); var b = next.GetValueOrDefault(name);
            var am = oldMetadata.GetValueOrDefault(name); var bm = nextMetadata.GetValueOrDefault(name);
            return new KnowledgeDocumentDifference(b?.Name ?? a!.Name,
                a is null ? "added" : b is null ? "removed" : a.Text == b.Text && a.Name == b.Name ? "unchanged" : "changed",
                am?.Id, bm?.Id, am?.ByteCount ?? 0, bm?.ByteCount ?? 0, am?.ChunkCount ?? 0, bm?.ChunkCount ?? 0);
        }).ToArray();
        return new(data.From, data.To, data.From.ChunkSize != data.To.ChunkSize || data.From.Overlap != data.To.Overlap
            || data.From.ParserVersion != data.To.ParserVersion, data.From.IndexSchema != data.To.IndexSchema, documents);
    }

    /// <summary>正文差异按文档和行数分页读取；旧、新版本都必须属于当前授权库。</summary>
    public async Task<KnowledgeDocumentComparison> CompareDocumentAsync(Guid id, Guid from, Guid to, string name, int offset, int limit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255) throw new KnowledgeException("请选择有效文档名称。");
        var data = await repository.ComparisonAsync(id, from, to, ct);
        var a = data.FromDocuments.SingleOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var b = data.ToDocuments.SingleOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (a is null && b is null) throw new KnowledgeException("文档不属于所选对比版本。");
        var diff = KnowledgeTextDiff.Page(a?.Text, b?.Text, offset, limit, ct);
        return new(b?.Name ?? a!.Name, from, to, diff.Total, diff.Simplified, offset, diff.Lines);
    }
}
