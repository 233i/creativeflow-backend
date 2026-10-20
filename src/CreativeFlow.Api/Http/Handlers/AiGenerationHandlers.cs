using CreativeFlow.Api.Middleware;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Application.Features.Generation;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Api.Http.Handlers;

/// <summary>图片与文本生成的 HTTP 适配，以及现有素材管理和交付。</summary>
/// <remarks>生成入口只传递已验证身份并映射用例结果；素材管理按治理计划继续逐切片迁移。</remarks>
internal static class AiGenerationHandlers
{
    /// <summary>
    /// 上传文件大小上限。校验签名后流式保存，避免每个并发请求分配完整图片缓冲区。
    /// </summary>
    private const long MaxUploadBytes = 20 * 1024 * 1024;

    /// <summary>
    /// 允许上传的图片类型。
    /// </summary>
    /// <remarks>
    /// 刻意排除 SVG。它是可携带脚本的 XML；浏览器直接访问已存储 SVG 时会按同源执行脚本，
    /// 从而让素材路由成为存储型 XSS。此处允许的光栅格式不存在该风险。
    /// </remarks>
    private static readonly string[] AllowedUploadTypes =
        ["image/png", "image/jpeg", "image/webp", "image/gif"];

    /// <summary>素材库默认显示的素材数量。</summary>
    private const int DefaultAssetPageSize = 60;

    /// <summary>用户创建的素材分组名称最大长度。</summary>
    private const int MaxAssetGroupNameLength = 64;

    /// <summary>单次批量分配可操作的最大素材数量。</summary>
    private const int MaxAssetsPerAssignment = 200;

    /// <summary>
    /// 单次列表查询的硬上限。素材库是选择器而非归档浏览器，
    /// 拥有数千次生成记录的设备不应能一次请求全部内容。
    /// </summary>
    private const int MaxAssetPageSize = 200;

    /// <summary>提取并缓存自有视频素材的最后一个可解码帧。</summary>
    internal static async Task<IActionResult> ExtractLastFrameAsync(
        string id,
        HttpContext http,
        IVideoFrameService frames,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId)
        {
            return ApiResults.Unauthorized();
        }

        if (!Guid.TryParse(id, out var assetId))
        {
            return ApiResults.NotFound();
        }

        var result = await frames.ExtractLastFrameAsync(deviceId, assetId, ct);
        return result.Status switch
        {
            VideoFrameAssetStatus.Succeeded when result.AssetId is { } frameId
                && result.MediaType is { } mediaType =>
                ApiResults.Ok(new GeneratedAsset(
                    $"/api/v1/canvas/assets/{frameId}", mediaType, frameId.ToString())),
            VideoFrameAssetStatus.NotFound => ApiResults.NotFound(),
            VideoFrameAssetStatus.InvalidSource => Invalid(
                result.Error ?? "the asset is not a decodable video"),
            _ => ApiResults.InternalServerError(
                new ErrorResponse(
                    "frame_extraction_failed",
                    [result.Error ?? "the video frame could not be extracted"])),
        };
    }

    /// <summary>
    /// 存储用户提供的图片，并返回画布应引用的 URL。
    /// </summary>
    /// <remarks>
    /// 若无此功能，上传图片只会作为对象 URL 存在于单个标签页中：刷新后失效，
    /// 且生成端点会静默忽略该参考图，因为服务器端只解析 <c>/api/v1/canvas/assets/</c> URL。
    /// </remarks>
    internal static async Task<IActionResult> UploadAssetAsync(
        IFormFile file,
        HttpContext http,
        IAssetStore assetStore,
        ICanvasAssetRepository assets,
        CancellationToken ct)
    {
        if (http.GetWorkflowDeviceId() is not { } deviceId)
        {
            return ApiResults.Unauthorized();
        }

        if (file.Length == 0)
        {
            return Invalid("the uploaded file is empty");
        }

        if (file.Length > MaxUploadBytes)
        {
            return Invalid($"the file exceeds the {MaxUploadBytes / (1024 * 1024)} MB limit");
        }

        var mediaType = file.ContentType?.Split(';')[0].Trim().ToLowerInvariant() ?? string.Empty;

        if (!AllowedUploadTypes.Contains(mediaType))
        {
            return Invalid($"unsupported image type '{mediaType}'");
        }

        var bytes = new byte[12];
        int headerLength;
        await using (var header = file.OpenReadStream())
            headerLength = await header.ReadAtLeastAsync(bytes, bytes.Length, throwOnEndOfStream: false, ct);

        // Content-Type 由客户端提供且很容易伪造，因此要将声明类型与实际文件签名比对。
        // 否则任意内容都可冒充图片媒体类型存储并按该类型提供。
        if (!SignatureMatches(bytes[..headerLength], mediaType))
        {
            return Invalid("the file contents do not match the declared image type");
        }

        await using var input = file.OpenReadStream();
        var stored = await assetStore.SaveAsync(input, mediaType, ExtensionFor(mediaType), MaxUploadBytes, ct);
        var assetId = Guid.CreateVersion7();

        await assets.InsertAsync(
            new CanvasAsset(
                assetId,
                deviceId,
                "image",
                stored.MediaType,
                stored.StoragePath,
                stored.ByteSize,
                null,
                DateTimeOffset.UtcNow) { Name = Path.GetFileName(file.FileName)[..Math.Min(Path.GetFileName(file.FileName).Length,200)], Origin = "upload" },
            ct);

        return ApiResults.Ok(new GeneratedAsset(
            $"/api/v1/canvas/assets/{assetId}", stored.MediaType, assetId.ToString()));
    }

    /// <summary>根据声明的媒体类型检查开头魔数。</summary>
    private static bool SignatureMatches(byte[] bytes, string mediaType) => mediaType switch
    {
        "image/png" => bytes.Length >= 8
            && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47,

        "image/jpeg" => bytes.Length >= 3
            && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF,

        "image/gif" => bytes.Length >= 6
            && bytes[0] == (byte)'G' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F',

        // RIFF 容器，偏移量 8 处包含 WEBP 四字符代码。
        "image/webp" => bytes.Length >= 12
            && bytes[0] == (byte)'R' && bytes[1] == (byte)'I'
            && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
            && bytes[8] == (byte)'W' && bytes[9] == (byte)'E'
            && bytes[10] == (byte)'B' && bytes[11] == (byte)'P',

        _ => false,
    };

    /// <summary>按原有模型和素材策略生成图片并保存结果。</summary>
    internal static async Task<IActionResult> GenerateImagesAsync(
        GenerateImageRequest request,
        HttpContext http,
        GenerateImagesUseCase useCase,
        CancellationToken ct)
    {
        // HTTP 身份拒绝仍发生在一切渠道查找和生成副作用之前。
        if (http.GetDeviceId() is not { } deviceId) return ApiResults.Unauthorized();

        var result = await useCase.HandleAsync(deviceId, request, ct);
        return result.Status switch
        {
            GenerateImagesStatus.Succeeded => ApiResults.Ok(result.Response!),
            GenerateImagesStatus.NotFound => ApiResults.NotFound(),
            GenerateImagesStatus.InvalidRequest => Invalid(result.Error!),
            _ => throw new InvalidOperationException("Unknown image generation outcome."),
        };
    }

    /// <summary>读取已验证的 HTTP 身份并映射文本用例结果，保留原有状态码和错误体。</summary>
    internal static async Task<IActionResult> GenerateTextAsync(
        GenerateTextRequest request,
        HttpContext http,
        GenerateTextUseCase useCase,
        CancellationToken ct)
    {
        // 身份拒绝仍先于渠道校验、素材读取和上游生成；应用用例只接收已验证设备。
        if (http.GetDeviceId() is not { } deviceId) return ApiResults.Unauthorized();

        var result = await useCase.HandleAsync(deviceId, request, ct);
        return result.Status switch
        {
            GenerateTextStatus.Succeeded => ApiResults.Ok(result.Response!),
            GenerateTextStatus.NotFound => ApiResults.NotFound(),
            GenerateTextStatus.InvalidRequest => Invalid(result.Error!),
            _ => throw new InvalidOperationException("Unknown text generation outcome."),
        };
    }

    /// <summary>
    /// 为素材库列出设备已存储的媒体。
    /// </summary>
    internal static async Task<IActionResult>
        ListAssetsAsync(
            HttpContext http,
            ICanvasAssetRepository assets,
            CancellationToken ct,
            string? kind = null,
            int? limit = null)
    {
        if (http.GetWorkflowDeviceId() is not { } deviceId)
        {
            return ApiResults.Unauthorized();
        }

        // 已知类型之外的任何值都视为“不筛选”而非错误；
        // 参数来自查询字符串，拼写错误不应让选择器失败。
        var filter = kind is "image" or "video" ? kind : null;
        var take = Math.Clamp(limit ?? DefaultAssetPageSize, 1, MaxAssetPageSize);

        var list = await assets.ListAsync(deviceId, filter, take, ct);

        return ApiResults.Ok(new CanvasAssetListResponse(
        [
            .. list.Select(asset => new CanvasAssetSummary(
                asset.AssetId.ToString(),
                $"/api/v1/canvas/assets/{asset.AssetId}",
                asset.Kind,
                asset.MediaType,
                asset.ByteSize,
                asset.Prompt,
                asset.CreatedAt.ToString("O"),
                asset.GroupId?.ToString())),
        ]));
    }

    /// <summary>列出设备素材库中的文件夹。</summary>
    internal static async Task<IActionResult>
        ListAssetGroupsAsync(
            HttpContext http,
            ICanvasAssetRepository assets,
            CancellationToken ct)
    {
        if (http.GetWorkflowDeviceId() is not { } deviceId)
        {
            return ApiResults.Unauthorized();
        }

        var groups = await assets.ListGroupsAsync(deviceId, ct);

        return ApiResults.Ok(new CanvasAssetGroupListResponse(
        [
            .. groups.Select(ToGroupSummary),
        ]));
    }

    /// <summary>创建文件夹，暂不向其中移动任何媒体。</summary>
    internal static async Task<IActionResult> CreateAssetGroupAsync(
        CreateCanvasAssetGroupRequest request,
        HttpContext http,
        ICanvasAssetRepository assets,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId)
        {
            return ApiResults.Unauthorized();
        }

        if (!TryNormalizeGroupName(request.Name, out var name, out var error))
        {
            return Invalid(error);
        }

        var now = DateTimeOffset.UtcNow;
        var groupId = Guid.CreateVersion7();
        var created = await assets.InsertGroupAsync(
            new CanvasAssetGroup(groupId, deviceId, name, 0, now, now), ct);

        return created is null
            ? ApiResults.Conflict(new ErrorResponse(
                "duplicate_name", [$"An asset group named '{name}' already exists."]))
            : ApiResults.Created(
                $"/api/v1/canvas/assets/groups/{groupId}", ToGroupSummary(created));
    }

    /// <summary>重命名文件夹，同时保留其全部素材归属。</summary>
    internal static async Task<IActionResult> RenameAssetGroupAsync(
        string id,
        UpdateCanvasAssetGroupRequest request,
        HttpContext http,
        ICanvasAssetRepository assets,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId)
        {
            return ApiResults.Unauthorized();
        }

        if (!Guid.TryParse(id, out var groupId) || groupId == Guid.Empty)
        {
            return ApiResults.NotFound();
        }

        if (!TryNormalizeGroupName(request.Name, out var name, out var error))
        {
            return Invalid(error);
        }

        if (await assets.FindGroupAsync(deviceId, groupId, ct) is null)
        {
            return ApiResults.NotFound();
        }

        var renamed = await assets.RenameGroupAsync(deviceId, groupId, name, DateTimeOffset.UtcNow, ct);
        return renamed is null
            ? ApiResults.Conflict(new ErrorResponse(
                "duplicate_name", [$"An asset group named '{name}' already exists."]))
            : ApiResults.Ok(ToGroupSummary(renamed));
    }

    /// <summary>删除文件夹，并将其中媒体保留在未分组集合中。</summary>
    internal static async Task<IActionResult>
        DeleteAssetGroupAsync(
            string id,
            HttpContext http,
            ICanvasAssetRepository assets,
            CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId)
        {
            return ApiResults.Unauthorized();
        }

        if (!Guid.TryParse(id, out var groupId) || groupId == Guid.Empty)
        {
            return ApiResults.NotFound();
        }

        return await assets.DeleteGroupAsync(deviceId, groupId, ct)
            ? ApiResults.NoContent()
            : ApiResults.NotFound();
    }

    /// <summary>将所选内容移入文件夹；groupId 为空时清除其文件夹归属。</summary>
    internal static async Task<IActionResult> AssignAssetsToGroupAsync(
        AssignCanvasAssetsRequest request,
        HttpContext http,
        ICanvasAssetRepository assets,
        CancellationToken ct)
    {
        // 先检查数量、素材 ID 格式及目标分组归属；具体素材的可访问范围由仓储更新条件约束。
        if (http.GetDeviceId() is not { } deviceId)
        {
            return ApiResults.Unauthorized();
        }

        if (request.AssetIds is null || request.AssetIds.Length is < 1 or > MaxAssetsPerAssignment)
        {
            return Invalid($"assetIds must contain between 1 and {MaxAssetsPerAssignment} items");
        }

        var ids = new List<Guid>(request.AssetIds.Length);
        foreach (var raw in request.AssetIds)
        {
            if (!Guid.TryParse(raw, out var assetId) || assetId == Guid.Empty)
            {
                return Invalid("assetIds contains a malformed id");
            }

            if (!ids.Contains(assetId)) ids.Add(assetId);
        }

        Guid? groupId = null;
        if (request.GroupId is not null)
        {
            if (!Guid.TryParse(request.GroupId, out var parsed) || parsed == Guid.Empty)
            {
                return Invalid("groupId must be a valid id or null");
            }

            if (await assets.FindGroupAsync(deviceId, parsed, ct) is null)
            {
                return ApiResults.NotFound();
            }

            groupId = parsed;
        }

        var updated = await assets.AssignGroupAsync(deviceId, ids, groupId, ct);
        return ApiResults.Ok(new AssignCanvasAssetsResponse(updated));
    }

    /// <summary>
    /// 把素材移入可恢复的回收站；不删除字节、不使历史预览失效。
    /// </summary>
    /// <remarks>
    /// 旧素材库入口与资产中心使用同一引用检查和审计，永久清理只能从明确确认的管理入口执行。
    /// </remarks>
    internal static async Task<IActionResult>
        DeleteAssetAsync(
            string id,
            HttpContext http,
            ICanvasAssetRepository assets,
            IAssetStore assetStore,
            CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId)
        {
            return ApiResults.Unauthorized();
        }

        if (!Guid.TryParse(id, out var assetId))
        {
            return ApiResults.NotFound();
        }

        var center = http.RequestServices.GetRequiredService<IAssetCenterRepository>();
        var preview = await center.PreviewAsync(assetId, deviceId.Value, ct);
        if (preview is null) return ApiResults.NotFound();
        var result = await center.ActAsync(assetId, "recycle", new(preview.Asset.Revision, "从素材库移入回收站"), deviceId.Value, deviceId.Value, ct);
        return result.Status is "recycled" or "unchanged" ? ApiResults.NoContent()
            : ApiResults.Conflict(new ErrorResponse("asset_recycle_blocked", [result.Message]));
    }

    /// <summary>将已存储素材流式返回给其所有者。</summary>
    internal static async Task<IActionResult>
        GetAssetAsync(
            string id,
            HttpContext http,
            ICanvasAssetRepository assets,
            IAssetStore assetStore,
            CancellationToken ct)
    {
        if (http.GetWorkflowDeviceId() is not { } deviceId)
        {
            return ApiResults.Unauthorized();
        }

        if (!Guid.TryParse(id, out var assetId))
        {
            return ApiResults.NotFound();
        }

        var asset = await assets.FindAsync(deviceId, assetId, ct);
        if (asset is null)
        {
            return ApiResults.NotFound();
        }

        var stream = await assetStore.OpenReadAsync(asset.StoragePath, ct);
        if (stream is null)
        {
            // 数据行比文件存续更久，404 是准确响应。
            return ApiResults.NotFound();
        }

        // 使用私有缓存而非公开缓存：素材只能由拥有它的设备读取，共享缓存绝不能保存它。
        // 同一标识下的内容永不变化，因此可标记为不可变。
        http.Response.Headers.CacheControl = "private, max-age=31536000, immutable";

        // 防止浏览器猜测已存储媒体类型。配合上传白名单和签名检查，
        // 确保已存储文件绝不会在此源下被解释为标记或脚本。
        http.Response.Headers.XContentTypeOptions = "nosniff";

        return ApiResults.Stream(stream, asset.MediaType, enableRangeProcessing: true);
    }

    /// <summary>将媒体类型映射为当前支持的文件扩展名，并保留未知类型的默认值。</summary>
    private static string ExtensionFor(string mediaType) => mediaType switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        "video/mp4" => ".mp4",
        _ => ".bin",
    };

    /// <summary>将分组记录映射为素材库的摘要响应。</summary>
    private static CanvasAssetGroupSummary ToGroupSummary(CanvasAssetGroup group) =>
        new(
            group.GroupId.ToString(),
            group.Name,
            group.AssetCount,
            group.CreatedAt.ToString("O"),
            group.UpdatedAt.ToString("O"));

    /// <summary>校验素材分组名称并返回规范化结果。</summary>
    private static bool TryNormalizeGroupName(
        string? value,
        out string name,
        out string error)
    {
        name = value?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            error = "name is required";
            return false;
        }

        if (name.Length > MaxAssetGroupNameLength)
        {
            error = $"name must be {MaxAssetGroupNameLength} characters or fewer";
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>将校验失败映射为当前功能既有的错误结果，保留详情和错误分类。</summary>
    private static IActionResult Invalid(string detail) =>
        ApiResults.BadRequest(new ErrorResponse("invalid_request", [detail]));
}
