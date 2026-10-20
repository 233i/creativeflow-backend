using System.Text.Json;
using System.Text.Json.Nodes;
using CreativeFlow.Api.Middleware;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Api.Http.Handlers;

/// <summary>
/// 按调用设备限定作用域的画布文档。
/// </summary>
/// <remarks>
/// 客户端在 localStorage 中保留副本，用于即时加载和离线编辑；此处是持久化副本。
/// 若无此存储，画布只存在于浏览器中，清除站点数据会销毁用户创建的所有图。
/// </remarks>
internal static class CanvasHandlers
{
    /// <summary>
    /// 已存储图的大小上限。该上限较宽松——图是文本，图片位于素材存储而非内联——
    /// 但无上限 jsonb 列会成为拒绝服务攻击入口。
    /// </summary>
    private const int MaxGraphBytes = 8 * 1024 * 1024;

    /// <summary>显示名称允许的最多字符数。</summary>
    private const int MaxNameLength = 200;

    /// <summary>
    /// 没有图的画布所使用的存储形式。
    /// </summary>
    /// <remarks>
    /// 使用格式完整的空图而非 <c>{}</c>：客户端会直接解构 `nodes` 和 `edges`；
    /// 缺少这些字段的对象虽为真值，却会产生未定义数组。
    /// </remarks>
    private const string EmptyGraph = """{"nodes":[],"edges":[]}""";

    /// <summary>列出画布文档，保留现有筛选、排序与访问范围。</summary>
    internal static async Task<IActionResult> ListAsync(
        HttpContext http,
        ICanvasRepository canvases,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId)
        {
            return ApiResults.Unauthorized();
        }

        var list = await canvases.ListAsync(deviceId, ct);

        return ApiResults.Ok(new CanvasListResponse(
        [
            .. list.Select(summary => new CanvasSummaryResponse(
                summary.CanvasId.ToString(),
                summary.Name,
                summary.NodeCount,
                summary.CreatedAt.ToString("O"),
                summary.UpdatedAt.ToString("O"))),
        ]));
    }

    /// <summary>校验输入并创建画布文档。</summary>
    internal static async Task<IActionResult> CreateAsync(
        CreateCanvasRequest request,
        HttpContext http,
        ICanvasRepository canvases,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId)
        {
            return ApiResults.Unauthorized();
        }

        if (!TryReadGraph(request.Graph, out var graphJson, out var error))
        {
            return Invalid(error);
        }

        var now = DateTimeOffset.UtcNow;
        var canvas = new Canvas(
            CanvasId.New(),
            deviceId,
            NormalizeName(request.Name),
            graphJson,
            now,
            now);

        await canvases.InsertAsync(canvas, ct);

        return ApiResults.Created($"/api/v1/canvas/{canvas.Id}", ToResponse(canvas));
    }

    /// <summary>读取画布文档，保留缺失资源的既有处理。</summary>
    internal static async Task<IActionResult> GetAsync(
        string id,
        HttpContext http,
        ICanvasRepository canvases,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId)
        {
            return ApiResults.Unauthorized();
        }

        if (!CanvasId.TryParse(id, out var canvasId))
        {
            return Invalid("malformed canvas id");
        }

        var canvas = await canvases.FindAsync(deviceId, canvasId, ct);

        return canvas is null ? ApiResults.NotFound() : ApiResults.Ok(ToResponse(canvas));
    }

    /// <summary>
    /// 替换画布。不存在时按提供的标识创建，使客户端可接管本地已有画布，而无需额外往返。
    /// </summary>
    internal static async Task<IActionResult> UpdateAsync(
        string id,
        UpdateCanvasRequest request,
        HttpContext http,
        ICanvasRepository canvases,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId)
        {
            return ApiResults.Unauthorized();
        }

        if (!CanvasId.TryParse(id, out var canvasId))
        {
            return Invalid("malformed canvas id");
        }

        var existing = await canvases.FindAsync(deviceId, canvasId, ct);
        var now = DateTimeOffset.UtcNow;

        if (existing is null)
        {
            if (!TryReadGraph(request.Graph, out var newGraph, out var createError))
            {
                return Invalid(createError);
            }

            var created = new Canvas(
                canvasId, deviceId, NormalizeName(request.Name), newGraph, now, now);

            await canvases.InsertAsync(created, ct);
            return ApiResults.Ok(ToResponse(created));
        }

        // 图为空表示“仅重命名”，因此重命名不会意外清空文档。
        var graphJson = existing.GraphJson;
        if (request.Graph is not null)
        {
            if (!TryReadGraph(request.Graph, out graphJson, out var updateError))
            {
                return Invalid(updateError);
            }
        }

        var updated = existing with
        {
            Name = string.IsNullOrWhiteSpace(request.Name)
                ? existing.Name
                : Truncate(request.Name.Trim()),
            GraphJson = graphJson,
            UpdatedAt = now,
        };

        await canvases.UpdateAsync(updated, ct);

        return ApiResults.Ok(ToResponse(updated));
    }

    /// <summary>删除画布文档，保留不存在或不可访问时的返回语义。</summary>
    internal static async Task<IActionResult> DeleteAsync(
        string id,
        HttpContext http,
        ICanvasRepository canvases,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId)
        {
            return ApiResults.Unauthorized();
        }

        if (!CanvasId.TryParse(id, out var canvasId))
        {
            return Invalid("malformed canvas id");
        }

        return await canvases.DeleteAsync(deviceId, canvasId, ct)
            ? ApiResults.NoContent()
            : ApiResults.NotFound();
    }

    /// <summary>
    /// 序列化传入图并强制执行大小上限。
    /// </summary>
    /// <remarks>
    /// 在序列化后检查正文，而不信任 Content-Length，因为请求可能经过压缩，
    /// 该上限保护的是实际存储大小。
    /// </remarks>
    private static bool TryReadGraph(JsonNode? graph, out string json, out string error)
    {
        json = graph?.ToJsonString() ?? EmptyGraph;

        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxGraphBytes)
        {
            error = $"the graph exceeds the {MaxGraphBytes / (1024 * 1024)} MB limit";
            json = EmptyGraph;
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>按既有缺省值和长度规则规范化显示名称。</summary>
    private static string NormalizeName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "未命名画布" : Truncate(name.Trim());

    /// <summary>按当前组件的既有上限截断文本。</summary>
    private static string Truncate(string value) =>
        value.Length <= MaxNameLength ? value : value[..MaxNameLength];

    /// <summary>将业务记录映射为既有 HTTP 响应字段。</summary>
    private static CanvasResponse ToResponse(Canvas canvas)
    {
        JsonNode? graph;
        try
        {
            graph = JsonNode.Parse(canvas.GraphJson);
        }
        catch (JsonException)
        {
            // 已存储 jsonb 始终是有效 JSON，因此实际不会进入此处；但对于用户可从本地副本覆盖的文档，
            // 返回空值优于抛出 500。
            graph = null;
        }

        return new CanvasResponse(
            canvas.Id.ToString(),
            canvas.Name,
            graph,
            canvas.CreatedAt.ToString("O"),
            canvas.UpdatedAt.ToString("O"));
    }

    /// <summary>将校验失败映射为当前功能既有的错误结果，保留详情和错误分类。</summary>
    private static IActionResult Invalid(string detail) =>
        ApiResults.BadRequest(new ErrorResponse("invalid_request", [detail]));
}
