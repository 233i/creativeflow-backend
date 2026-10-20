namespace CreativeFlow.Application.Features.Knowledge;

/// <summary>部署者维护的知识容量预算；API 只能读取，不能通过导入参数提高限制。</summary>
public sealed record KnowledgeQuotas
{
    /// <summary>库总数上限，默认保持已有开发试点预算。</summary>
    public int MaxLibraries { get; init; } = 10;
    /// <summary>每库未清理版本上限；已清理的审计墓碑不占用版本预算。</summary>
    public int MaxVersions { get; init; } = 10;
    /// <summary>每个完整索引版本的文档数上限。</summary>
    public int MaxDocuments { get; init; } = 8;
    /// <summary>单篇 UTF-8 文档的原始字节上限。</summary>
    public int MaxDocumentBytes { get; init; } = 65536;
    /// <summary>每个完整版本的 UTF-8 文档总字节上限。</summary>
    public int MaxVersionBytes { get; init; } = 65536;
    /// <summary>每个索引版本的总分段数上限。</summary>
    public int MaxChunks { get; init; } = 128;
    /// <summary>每库全部未清理版本的保守逻辑存储预留预算，包含向量和分段，不等同于物理磁盘用量。</summary>
    public long MaxLibraryReservedBytes { get; init; } = 67108864;

    /// <summary>启动时拒绝超出实现安全边界或互相矛盾的配置，不静默放宽预算。</summary>
    public KnowledgeQuotas Validate()
    {
        if (MaxLibraries is < 1 or > 100 || MaxVersions is < 1 or > 100 || MaxDocuments is < 1 or > 128
            || MaxDocumentBytes is < 1 or > 1048576 || MaxVersionBytes < MaxDocumentBytes || MaxVersionBytes > 2097152
            || MaxChunks is < 1 or > 8192 || MaxLibraryReservedBytes is < 65536 or > 2147483648)
            throw new KnowledgeException("知识配额配置无效，请检查 Knowledge:Quotas；未自动扩大容量。");
        return this;
    }
}
