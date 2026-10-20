using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Contracts.Admin;

namespace CreativeFlow.UnitTests.Support;

/// <summary>请求记录替身，保留输入与终态并按用户设备限制素材返回。</summary>
internal sealed class RecordingRequestHistory : IRequestHistoryRepository
{
    /// <summary>已记录的请求开始参数。</summary>
    public List<GenerationHistoryStart> Starts { get; } = [];
    /// <summary>已记录的终态。</summary>
    public List<Completion> Finishes { get; } = [];
    /// <summary>可供读取的请求快照。</summary>
    public RequestHistoryDocument? Document { get; set; }
    /// <summary>素材元数据，包括用于验证隔离的其他用户素材。</summary>
    public List<CanvasAsset> Assets { get; } = [];
    /// <summary>最后一次列表筛选参数。</summary>
    public RequestHistoryQuery? Query { get; private set; }
    /// <summary>是否模拟记录系统故障。</summary>
    public bool ThrowOnWrite { get; set; }
    /// <summary>最后一个写入令牌是否已取消。</summary>
    public bool WriteWasCancelled { get; private set; }
    /// <summary>读取次数，用于确认无管理员权限时完全不进入数据层。</summary>
    public int ReadCount { get; private set; }

    /// <summary>记录合成请求起始数据及写入时取消状态，可按开关模拟历史存储不可用。</summary>
    public Task StartAsync(GenerationHistoryStart request, CancellationToken ct)
    {
        if (ThrowOnWrite) throw new IOException("Test history unavailable");
        WriteWasCancelled = ct.IsCancellationRequested;
        Starts.Add(request);
        return Task.CompletedTask;
    }

    /// <summary>记录合成请求终态、输出和关联视频任务，可按开关模拟写入失败。</summary>
    public Task FinishAsync(Guid id, string status, string? output, string? error, Guid? videoJobId, CancellationToken ct)
    {
        if (ThrowOnWrite) throw new IOException("Test history unavailable");
        WriteWasCancelled = ct.IsCancellationRequested;
        Finishes.Add(new(id, status, output, error, videoJobId));
        return Task.CompletedTask;
    }

    /// <summary>记录查询次数与分页条件，将预设文档摘要作为内存分页结果返回。</summary>
    public Task<AdminRequestPage> ListAsync(RequestHistoryQuery query, CancellationToken ct)
    {
        ReadCount++;
        Query = query;
        return Task.FromResult(new AdminRequestPage(Document is { } d ? [d.Summary] : [], Document is null ? 0 : 1, query.Offset, query.Limit));
    }

    /// <summary>累计读取次数，仅在来源和标识匹配时返回预设请求详情。</summary>
    public Task<RequestHistoryDocument?> FindAsync(string source, Guid id, CancellationToken ct)
    {
        ReadCount++;
        return Task.FromResult(Document?.Summary.Id == id.ToString() && Document.Summary.Source == source ? Document : null);
    }

    /// <summary>累计读取次数，从内存素材中筛选请求设备拥有且标识匹配的产物，不模拟账号跨设备共享。</summary>
    public Task<IReadOnlyList<CanvasAsset>> FindArtifactsAsync(Guid deviceId, Guid? accountId, Guid[] ids, CancellationToken ct)
    {
        ReadCount++;
        return Task.FromResult<IReadOnlyList<CanvasAsset>>(Assets.Where(asset => asset.DeviceId.Value == deviceId && ids.Contains(asset.AssetId)).ToArray());
    }

    /// <summary>构造包含用户和归属资料的快照，具体测试只覆盖相关部分。</summary>
    public static RequestHistoryDocument CreateDocument(string source = "canvas", string kind = "image", string? output = null) =>
        new(new(Guid.NewGuid().ToString(), source, kind, "succeeded", Guid.NewGuid().ToString(), "测试用户", "test@example.invalid",
            Guid.NewGuid().ToString(), "测试项目", null, "test-model", "测试渠道", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null, false),
            Guid.NewGuid(), "{\"prompt\":\"原始输入\"}", output, []);

    /// <param name="Id">被完成的请求。</param>
    /// <param name="Status">执行终态或异步运行中。</param>
    /// <param name="Output">持久化响应。</param>
    /// <param name="Error">错误说明。</param>
    /// <param name="VideoJobId">已接受的视频任务。</param>
    internal sealed record Completion(Guid Id, string Status, string? Output, string? Error, Guid? VideoJobId);
}
