using System.Text.Json;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Contracts.Admin;

namespace CreativeFlow.Application.Features.Administration;

/// <summary>把现有运行快照组织为管理员可查看的请求和安全产物地址。</summary>
public sealed class RequestHistoryService(IRequestHistoryRepository repository)
{
    /// <summary>解析一份快照；历史数据损坏时保留其他节点的查看能力。</summary>
    private static JsonElement Parse(string? json)
    {
        try
        {
            using var document = JsonDocument.Parse(json ?? "{}");
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            using var empty = JsonDocument.Parse("{}");
            return empty.RootElement.Clone();
        }
    }

    /// <summary>读取字符串属性，忽略旧版本中不兼容的字段类型。</summary>
    private static string? Text(JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var item)
            && item.ValueKind == JsonValueKind.String ? item.GetString() : null;

    /// <summary>只识别站内素材标识，外部地址不会被提升为管理员资源或自动下载。</summary>
    private static Guid? AssetId(string? value)
    {
        const string prefix = "/api/v1/canvas/assets/";
        var candidate = value?.StartsWith(prefix, StringComparison.Ordinal) == true ? value[prefix.Length..] : value;
        return Guid.TryParse(candidate, out var id) && id != Guid.Empty ? id : null;
    }

    /// <summary>读取画布响应或工作流节点产物，保留缺失素材的占位。</summary>
    private static List<ArtifactReference> References(string? json)
    {
        var value = Parse(json);
        var result = new List<ArtifactReference>();
        if (Text(value, "text") is { } text) result.Add(new("text", text, null));
        if (Text(value, "assetId") is { } asset) result.Add(new("video", null, AssetId(asset)));
        if (value.ValueKind != JsonValueKind.Object) return result;
        if (value.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            foreach (var item in assets.EnumerateArray())
                result.Add(new("image", null, AssetId(Text(item, "assetId") ?? Text(item, "url"))));
        // 工作流沿用 texts/images/videos 契约，不把任意 JSON 字符串当作可访问文件。
        foreach (var (key, kind) in new[] { ("texts", "text"), ("images", "image"), ("videos", "video") })
            if (value.TryGetProperty(key, out var values) && values.ValueKind == JsonValueKind.Array)
                foreach (var item in values.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String)
                        result.Add(new(kind, kind == "text" ? item.GetString() : null,
                            kind == "text" ? null : AssetId(item.GetString())));
        return result;
    }

    /// <summary>获取记录的全部输出引用，用于预览与媒体读取使用相同的授权范围。</summary>
    private static IEnumerable<ArtifactReference> AllReferences(RequestHistoryDocument document) =>
        References(document.OutputJson).Concat(document.Nodes.SelectMany(node => References(node.OutputJson)));

    /// <summary>构造仅绑定当前管理员记录的素材地址，不暴露存储键或供应商签名链接。</summary>
    private static AdminRequestArtifact[] Artifacts(IEnumerable<ArtifactReference> references,
        RequestHistoryDocument document, IReadOnlyDictionary<Guid, CanvasAsset> assets) =>
        references.Select(reference => reference.Kind == "text"
            ? new AdminRequestArtifact("text", reference.Text, null, null, null, false)
            : reference.Id is { } id && assets.TryGetValue(id, out var asset)
                ? new AdminRequestArtifact(asset.Kind, null, id.ToString(),
                    $"/api/v1/admin/requests/{document.Summary.Source}/{document.Summary.Id}/assets/{id}", asset.MediaType, false)
                : new AdminRequestArtifact(reference.Kind, null, reference.Id?.ToString(), null, null, true)).ToArray();

    /// <summary>读取完整请求详情；工作流节点输入来自运行快照，而非当前已修改的画布。</summary>
    public async Task<AdminRequestDetail?> GetAsync(string source, Guid id, CancellationToken ct)
    {
        var document = await repository.FindAsync(source, id, ct);
        if (document is null) return null;
        var ids = AllReferences(document).Where(item => item.Id is not null).Select(item => item.Id!.Value).Distinct().ToArray();
        var accountId = Guid.TryParse(document.Summary.AccountId, out var account) ? account : (Guid?)null;
        var assets = (await repository.FindArtifactsAsync(document.DeviceId, accountId, ids, ct)).ToDictionary(asset => asset.AssetId);
        var input = Parse(document.InputJson);
        var inputs = new Dictionary<string, JsonElement>();
        if (input.ValueKind == JsonValueKind.Object && input.TryGetProperty("nodes", out var graphNodes)
            && graphNodes.ValueKind == JsonValueKind.Array)
            foreach (var node in graphNodes.EnumerateArray())
                if (Text(node, "id") is { } nodeId && node.TryGetProperty("data", out var data)) inputs[nodeId] = data.Clone();

        var nodes = document.Nodes.Select(node => new AdminRequestNode(node.Id, node.Type, node.Status, node.Reused,
            inputs.GetValueOrDefault(node.Id, Parse(null)), Artifacts(References(node.OutputJson), document, assets),
            node.Error, node.StartedAt, node.FinishedAt)).ToArray();
        // 结果节点代表工作流交付物；失败运行没有结果节点时仍可查看已完成节点的产物。
        var finalNodes = nodes.Where(node => node.Type is "imageOutput" or "videoOutput").ToArray();
        var outputs = source == "canvas" ? Artifacts(References(document.OutputJson), document, assets)
            : (finalNodes.Any(node => node.Artifacts.Length > 0) ? finalNodes : nodes).SelectMany(node => node.Artifacts).Distinct().ToArray();
        return new(document.Summary, source == "canvas" ? input : Parse(null), outputs, nodes);
    }

    /// <summary>只交付当前记录实际引用且属于执行用户的素材，拒绝猜测其他素材标识。</summary>
    public async Task<CanvasAsset?> GetAssetAsync(string source, Guid id, Guid assetId, CancellationToken ct)
    {
        var document = await repository.FindAsync(source, id, ct);
        if (document is null || !AllReferences(document).Any(reference => reference.Id == assetId)) return null;
        var account = Guid.TryParse(document.Summary.AccountId, out var parsed) ? parsed : (Guid?)null;
        return (await repository.FindArtifactsAsync(document.DeviceId, account, [assetId], ct)).FirstOrDefault();
    }

    /// <param name="Kind">产物类型。</param>
    /// <param name="Text">文本产物内容。</param>
    /// <param name="Id">站内媒体标识，无法解析时为空。</param>
    private sealed record ArtifactReference(string Kind, string? Text, Guid? Id);
}
