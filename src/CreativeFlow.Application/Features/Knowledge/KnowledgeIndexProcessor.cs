using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;

namespace CreativeFlow.Application.Features.Knowledge;

/// <summary>入库业务推进器，不运行工作流；使用持久向量检查点和显式未知状态防止盲目重发。</summary>
public sealed class KnowledgeIndexProcessor(IKnowledgeRepository repository, IKnowledgeEmbedding embedding, IKnowledgeIndexStore index)
{
    /// <summary>串行处理一个索引任务，锁被其他进程持有时立即退出。</summary>
    public async Task ProcessNextAsync(CancellationToken ct)
    {
        if (!index.Available) return;
        await using var lease = await repository.TryWorkerLockAsync(ct);
        if (lease is null) return;
        if (await repository.NextCleanupAsync(ct) is { } cleanup)
        {
            try
            {
                await index.DeleteAsync(cleanup.BaseId, cleanup.Id, ct);
                await repository.FinishCleanupAsync(cleanup.BaseId, cleanup.Id, ct);
            }
            catch (KnowledgeException error)
            { await repository.SaveProgressAsync(cleanup with { Status = "cleanup_failed", Error = error.Message }, ct); }
            return;
        }
        var version = await repository.NextAsync(ct);
        if (version is null) return;
        // 正常执行只有当前宿主持有锁；遗留 embedding 表示请求可能已发出，但没有落下完整检查点。
        if (version.Status == "embedding")
        {
            await repository.SaveProgressAsync(version with { Status = "unknown", Error = "向量化中断，受理状态待核对；未自动重发。可显式重新向量化未确认批次。" }, ct);
            return;
        }
        try
        {
            var library = await repository.FindAsync(version.BaseId, ct);
            if (library is not { Enabled: true }) throw new KnowledgeException("知识库已停用；当前可用索引未改变。");
            // jsonb 会重排属性并调整空白，配置一致性比较必须采用 JSON 结构而不是原始字符串。
            if (!JsonNode.DeepEquals(JsonNode.Parse(await embedding.CaptureAsync(library.ModelId, ct)), JsonNode.Parse(version.ModelSnapshot)))
                throw new KnowledgeException("模型或向量配置已变化，请创建新的候选版本，旧版本未被覆盖。");
            var snapshot = JsonNode.Parse(version.ModelSnapshot)!;
            var dimensions = snapshot["dimensions"]!.GetValue<int>();
            var batchSize = Math.Clamp(snapshot["batchSize"]!.GetValue<int>(), 1, 16);
            if (version.IndexSchema == "hybrid-v1") await index.PrepareHybridAsync(version.BaseId, version.Id, dimensions, ct);
            else await index.PrepareAsync(version.BaseId, version.Id, dimensions, ct);
            while (true)
            {
                var batch = await repository.PendingChunksAsync(version.BaseId, version.Id, batchSize, ct);
                if (batch.Length == 0) break;
                if ((await repository.FindAsync(version.BaseId, ct))?.Enabled != true) throw new KnowledgeException("知识库已停用。");
                version = version with { Status = "embedding", Error = null };
                await repository.SaveProgressAsync(version, ct);
                var result = await embedding.EmbedAsync(version.ModelSnapshot,
                    batch.Select(chunk => new EmbeddingInput(chunk.Id.ToString(), chunk.Text)).ToArray(), "document", ct);
                if (!result.Ok)
                {
                    var safeBeforeSend = result.Code is "busy" or "invalid_request" or "model_unavailable" or "unsupported_protocol" or "endpoint_not_allowed" or "invalid_endpoint";
                    var status = result.Code == "busy" ? "queued" : safeBeforeSend ? "failed" : "unknown";
                    await repository.SaveProgressAsync(version with { Status = status, Error = result.Error ?? "向量化未完成，未自动重试。" }, ct);
                    return;
                }
                if (result.Dimensions != dimensions || result.Vectors.Count != batch.Length
                    || result.Vectors.Where((v, i) => v.Id != batch[i].Id.ToString() || v.Values.Length != dimensions || v.Values.Any(n => !float.IsFinite(n))).Any())
                {
                    await repository.SaveProgressAsync(version with { Status = "unknown", Error = "向量响应与批次不匹配，未写入部分结果或自动重发。" }, ct);
                    return;
                }
                await repository.SaveVectorBatchAsync(version, result.Vectors, ct);
                version = version with { Status = "queued" };
            }
            version = version with { Status = "writing", Error = null };
            await repository.SaveProgressAsync(version, ct);
            while (true)
            {
                var batch = await repository.PendingWritesAsync(version.BaseId, version.Id, 16, ct);
                if (batch.Length == 0) break;
                if ((await repository.FindAsync(version.BaseId, ct))?.Enabled != true) throw new KnowledgeException("知识库已停用。");
                if (version.IndexSchema == "hybrid-v1")
                {
                    var chunks = await repository.ReadChunksAsync(version.BaseId, version.Id, batch.Select(v => Guid.Parse(v.Id)).ToArray(), null, 0, batch.Length, ct);
                    await index.WriteHybridAsync(version.BaseId, version.Id, batch, chunks, ct);
                }
                else await index.WriteAsync(version.BaseId, version.Id, batch, ct);
                await repository.MarkWrittenAsync(version.BaseId, version.Id, batch.Select(v => Guid.Parse(v.Id)).ToArray(), ct);
            }
            var indexed = version.IndexSchema == "hybrid-v1" ? await index.CountHybridAsync(version.BaseId, version.Id, ct) : await index.CountAsync(version.BaseId, version.Id, ct);
            if (indexed != version.Progress!.ChunkCount) throw new KnowledgeException("候选索引条数不完整，可从检查点重新写入；当前激活版本未改变。");
            await repository.SaveProgressAsync(version with { Status = "ready", Error = null }, ct);
        }
        catch (KnowledgeException error)
        { await repository.SaveProgressAsync(version with { Status = version.Status == "embedding" ? "unknown" : "failed", Error = error.Message }, ct); }
        // 取消、断连及未知异常故意保留最后状态；若发生在发送后，下次启动必须进入 unknown。
    }
}
