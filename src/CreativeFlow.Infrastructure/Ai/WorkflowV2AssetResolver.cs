using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Domain.Identity;

namespace CreativeFlow.Infrastructure.Ai;

/// <summary>将新版工作流的受控 assetId 读取为模型图片内容；读取前后都按设备复验权限。</summary>
internal sealed class WorkflowV2AssetResolver(ICanvasAssetRepository assets, IAssetStore store) : IWorkflowV2AssetResolver
{
    private const long MaxImageBytes = 20 * 1024 * 1024;

    public async Task ValidateAsync(DeviceId deviceId, JsonNode value, CancellationToken ct)
    {
        var asset = await FindAsync(deviceId, value, ct);
        EnsureSize(asset);
    }

    public async Task<ReferenceImage> ReadAsync(DeviceId deviceId, JsonNode value, CancellationToken ct)
    {
        var asset = await FindAsync(deviceId, value, ct);
        EnsureSize(asset);
        await using var source = await store.OpenReadAsync(asset.StoragePath, ct)
            ?? throw new WorkflowV2Exception("图片素材内容不可用，请重新选择素材。");
        await using var buffer = new MemoryStream(capacity: checked((int)Math.Min(asset.ByteSize, MaxImageBytes)));
        await source.CopyToAsync(buffer, 128 * 1024, ct);
        if (buffer.Length > MaxImageBytes) throw new WorkflowV2Exception("图片素材超过 20 MiB 限制。");
        return new ReferenceImage(buffer.ToArray(), asset.MediaType);
    }

    private static void EnsureSize(CanvasAsset asset)
    {
        if (asset.ByteSize < 0) throw new WorkflowV2Exception("图片素材大小无效。");
        if (asset.ByteSize > MaxImageBytes)
            throw new WorkflowV2Exception("图片素材超过 20 MiB 限制。");
    }

    private async Task<CanvasAsset> FindAsync(DeviceId deviceId, JsonNode value, CancellationToken ct)
    {
        var idText = value is JsonObject image && image.Count == 1 && image["assetId"] is JsonValue json
            && json.TryGetValue<string>(out var text) ? text : null;
        if (!Guid.TryParse(idText, out var id) || id == Guid.Empty)
            throw new WorkflowV2Exception("图片输入必须是有效的 assetId。");
        var asset = await assets.FindAsync(deviceId, id, ct);
        if (asset is null || asset.IsRecycled || asset.Kind != "image" || !asset.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            throw new WorkflowV2Exception("图片素材不存在或无权访问。");
        return asset;
    }
}
