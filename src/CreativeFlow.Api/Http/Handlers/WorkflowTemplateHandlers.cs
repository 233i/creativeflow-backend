using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CreativeFlow.Api.Middleware;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Application.Features.Workflow;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Api.Http.Handlers;

internal static class WorkflowTemplateHandlers
{
    /// <summary>完整节点图 JSON 允许的最大字节数。</summary>
    private const int MaxGraphBytes = 2 * 1024 * 1024;
    /// <summary>显示名称允许的最多字符数。</summary>
    private const int MaxNameLength = 200;
    /// <summary>说明文本允许的最多字符数。</summary>
    private const int MaxDescriptionLength = 2000;
    /// <summary>模板分类允许的最多字符数。</summary>
    private const int MaxCategoryLength = 80;
    /// <summary>模板允许的标签数量上限。</summary>
    private const int MaxTags = 10;
    /// <summary>模板标签允许的最多字符数。</summary>
    private const int MaxTagLength = 40;
    /// <summary>新图或模板使用的既有空节点/连线结构。</summary>
    private const string EmptyGraph = """{"nodes":[],"edges":[]}""";

    /// <summary>读取当前公开可用的发布条目。</summary>
    internal static async Task<IActionResult> ListPublishedAsync(
        HttpContext http,
        IWorkflowTemplateRepository templates,
        string? search,
        string? category,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is null) return ApiResults.Unauthorized();
        var list = await templates.ListAsync(
            search,
            category,
            WorkflowTemplateStatus.Published,
            ct);
        return ApiResults.Ok(ToListResponse(list));
    }

    /// <summary>读取已发布且可访问的工作流模板详情。</summary>
    internal static async Task<IActionResult> GetPublishedAsync(
        string id,
        HttpContext http,
        IWorkflowTemplateRepository templates,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is null) return ApiResults.Unauthorized();
        if (!WorkflowTemplateId.TryParse(id, out var templateId)) return ApiResults.NotFound();
        var template = await templates.FindAsync(templateId, ct);
        return template is { Status: WorkflowTemplateStatus.Published }
            ? ApiResults.Ok(ToResponse(template))
            : ApiResults.NotFound();
    }

    /// <summary>从已发布模板创建独立工作流并记录使用次数。</summary>
    internal static async Task<IActionResult> InstantiateAsync(
        string id,
        InstantiateWorkflowTemplateRequest request,
        HttpContext http,
        IWorkflowTemplateRepository templates,
        IWorkflowRepository workflows,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId) return ApiResults.Unauthorized();
        if (!WorkflowTemplateId.TryParse(id, out var templateId)) return ApiResults.NotFound();
        var template = await templates.FindAsync(templateId, ct);
        if (template is not { Status: WorkflowTemplateStatus.Published })
        {
            return ApiResults.NotFound();
        }

        var now = DateTimeOffset.UtcNow;
        var workflow = new Workflow(
            WorkflowId.New(),
            deviceId,
            NormalizeName(request.Name, template.Name),
            template.SchemaVersion,
            template.GraphJson,
            now,
            now);
        await workflows.InsertAsync(workflow, ct);
        await templates.IncrementUseCountAsync(template.Id, ct);
        return ApiResults.Created(
            $"/api/v1/workflows/{workflow.Id}",
            ToWorkflowResponse(workflow));
    }

    /// <summary>列出管理员可见的工作流模板。</summary>
    internal static async Task<IActionResult> ListAdminAsync(
        HttpContext http,
        IWorkflowTemplateRepository templates,
        string? search,
        string? category,
        string? status,
        CancellationToken ct)
    {
        if (http.GetAuthenticatedAdmin() is null) return ApiResults.Unauthorized();
        if (!TryParseStatus(status, out var parsedStatus))
        {
            return Invalid("unknown workflow template status");
        }

        var list = await templates.ListAsync(search, category, parsedStatus, ct);
        return ApiResults.Ok(ToListResponse(list));
    }

    /// <summary>读取管理员需要的工作流模板详情。</summary>
    internal static async Task<IActionResult> GetAdminAsync(
        string id,
        HttpContext http,
        IWorkflowTemplateRepository templates,
        CancellationToken ct)
    {
        if (http.GetAuthenticatedAdmin() is null) return ApiResults.Unauthorized();
        if (!WorkflowTemplateId.TryParse(id, out var templateId)) return ApiResults.NotFound();
        var template = await templates.FindAsync(templateId, ct);
        return template is null ? ApiResults.NotFound() : ApiResults.Ok(ToResponse(template));
    }

    /// <summary>校验输入并创建工作流模板。</summary>
    internal static async Task<IActionResult> CreateAsync(
        CreateWorkflowTemplateRequest request,
        HttpContext http,
        IWorkflowTemplateRepository templates,
        CancellationToken ct)
    {
        // 管理员输入须同时满足元数据与图结构限制；写入时保留原有草稿和排序规则。
        if (http.GetAuthenticatedAdmin() is null) return ApiResults.Unauthorized();
        var schemaVersion = request.SchemaVersion ?? WorkflowGraphValidator.SchemaVersion;
        if (schemaVersion != WorkflowGraphValidator.SchemaVersion)
        {
            return Invalid("unsupported workflow schema version");
        }

        if (!TryReadGraph(request.Graph, out var graphJson, out var graphError))
        {
            return Invalid(graphError);
        }

        var now = DateTimeOffset.UtcNow;
        var template = new WorkflowTemplate(
            WorkflowTemplateId.New(),
            NormalizeName(request.Name, "未命名模板"),
            NormalizeDescription(request.Description),
            NormalizeCategory(request.Category),
            NormalizeTags(request.Tags),
            schemaVersion,
            graphJson,
            WorkflowTemplateStatus.Draft,
            1,
            request.IsFeatured ?? false,
            NormalizeSortOrder(request.SortOrder),
            0,
            now,
            now,
            null);
        await templates.InsertAsync(template, ct);
        return ApiResults.Created(
            $"/api/v1/admin/workflow-templates/{template.Id}",
            ToResponse(template));
    }

    /// <summary>更新工作流模板，保留原有访问范围和并发结果。</summary>
    internal static async Task<IActionResult> UpdateAsync(
        string id,
        UpdateWorkflowTemplateRequest request,
        HttpContext http,
        IWorkflowTemplateRepository templates,
        CancellationToken ct)
    {
        // 更新模板前先确认管理身份和现有记录，再按原规则校验内容，避免部分字段提前写入。
        if (http.GetAuthenticatedAdmin() is null) return ApiResults.Unauthorized();
        if (!WorkflowTemplateId.TryParse(id, out var templateId)) return ApiResults.NotFound();
        var existing = await templates.FindAsync(templateId, ct);
        if (existing is null) return ApiResults.NotFound();
        if (existing.Status == WorkflowTemplateStatus.Published)
        {
            return ApiResults.Conflict(new ErrorResponse(
                "template_is_published",
                ["archive or duplicate the template before editing it"]));
        }

        var schemaVersion = request.SchemaVersion ?? existing.SchemaVersion;
        if (schemaVersion != WorkflowGraphValidator.SchemaVersion)
        {
            return Invalid("unsupported workflow schema version");
        }

        var graphJson = existing.GraphJson;
        if (request.Graph is not null
            && !TryReadGraph(request.Graph, out graphJson, out var graphError))
        {
            return Invalid(graphError);
        }

        var updated = existing with
        {
            Name = request.Name is null
                ? existing.Name
                : NormalizeName(request.Name, existing.Name),
            Description = request.Description is null
                ? existing.Description
                : NormalizeDescription(request.Description),
            Category = request.Category is null
                ? existing.Category
                : NormalizeCategory(request.Category),
            Tags = request.Tags is null ? existing.Tags : NormalizeTags(request.Tags),
            SchemaVersion = schemaVersion,
            GraphJson = graphJson,
            IsFeatured = request.IsFeatured ?? existing.IsFeatured,
            SortOrder = request.SortOrder is null
                ? existing.SortOrder
                : NormalizeSortOrder(request.SortOrder),
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        return await templates.UpdateAsync(updated, ct) ? ApiResults.Ok(ToResponse(updated)) : ApiResults.NotFound();
    }

    /// <summary>校验模板内容并将其发布为可实例化的模板。</summary>
    internal static async Task<IActionResult> PublishAsync(
        string id,
        HttpContext http,
        IWorkflowTemplateRepository templates,
        CancellationToken ct)
    {
        // 发布模板必须满足可展示和可实例化条件，不能只切换状态而跳过图校验。
        if (http.GetAuthenticatedAdmin() is null) return ApiResults.Unauthorized();
        if (!WorkflowTemplateId.TryParse(id, out var templateId)) return ApiResults.NotFound();
        var existing = await templates.FindAsync(templateId, ct);
        if (existing is null) return ApiResults.NotFound();

        JsonNode? graph;
        try
        {
            graph = JsonNode.Parse(existing.GraphJson);
        }
        catch (JsonException)
        {
            return Invalid("stored template graph is invalid JSON");
        }

        var validation = WorkflowGraphValidator.ValidateTemplate(graph);
        if (!validation.IsValid)
        {
            return ApiResults.BadRequest(new ErrorResponse(
                "invalid_workflow_template",
                [.. validation.Errors]));
        }

        if (existing.Status == WorkflowTemplateStatus.Published)
        {
            return ApiResults.Ok(ToResponse(existing));
        }

        if (!TryReadGraph(graph, out var publishedGraphJson, out var graphError, true))
        {
            return Invalid(graphError);
        }

        var now = DateTimeOffset.UtcNow;
        var published = existing with
        {
            GraphJson = publishedGraphJson,
            Status = WorkflowTemplateStatus.Published,
            Version = existing.PublishedAt is null ? existing.Version : existing.Version + 1,
            UpdatedAt = now,
            PublishedAt = now,
        };
        return await templates.UpdateAsync(published, ct) ? ApiResults.Ok(ToResponse(published)) : ApiResults.NotFound();
    }

    /// <summary>将工作流模板下架，保留原有历史记录。</summary>
    internal static async Task<IActionResult> ArchiveAsync(
        string id,
        HttpContext http,
        IWorkflowTemplateRepository templates,
        CancellationToken ct)
    {
        if (http.GetAuthenticatedAdmin() is null) return ApiResults.Unauthorized();
        if (!WorkflowTemplateId.TryParse(id, out var templateId)) return ApiResults.NotFound();
        var existing = await templates.FindAsync(templateId, ct);
        if (existing is null) return ApiResults.NotFound();
        if (existing.Status == WorkflowTemplateStatus.Archived)
        {
            return ApiResults.Ok(ToResponse(existing));
        }

        var archived = existing with
        {
            Status = WorkflowTemplateStatus.Archived,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        return await templates.UpdateAsync(archived, ct) ? ApiResults.Ok(ToResponse(archived)) : ApiResults.NotFound();
    }

    /// <summary>复制工作流模板，按既有规则建立新的草稿。</summary>
    internal static async Task<IActionResult> DuplicateAsync(
        string id,
        HttpContext http,
        IWorkflowTemplateRepository templates,
        CancellationToken ct)
    {
        if (http.GetAuthenticatedAdmin() is null) return ApiResults.Unauthorized();
        if (!WorkflowTemplateId.TryParse(id, out var templateId)) return ApiResults.NotFound();
        var existing = await templates.FindAsync(templateId, ct);
        if (existing is null) return ApiResults.NotFound();

        var now = DateTimeOffset.UtcNow;
        var duplicate = existing with
        {
            Id = WorkflowTemplateId.New(),
            Name = NormalizeName($"{existing.Name} 副本", "模板副本"),
            Status = WorkflowTemplateStatus.Draft,
            Version = 1,
            IsFeatured = false,
            UseCount = 0,
            CreatedAt = now,
            UpdatedAt = now,
            PublishedAt = null,
        };
        await templates.InsertAsync(duplicate, ct);
        return ApiResults.Created(
            $"/api/v1/admin/workflow-templates/{duplicate.Id}",
            ToResponse(duplicate));
    }

    /// <summary>为管理员准备当前模板的调试工作流。</summary>
    internal static async Task<IActionResult> PrepareDebugWorkflowAsync(
        string id,
        HttpContext http,
        IWorkflowTemplateRepository templates,
        IWorkflowRepository workflows,
        CancellationToken ct)
    {
        if (http.GetAuthenticatedAdmin() is null) return ApiResults.Unauthorized();
        if (http.GetDeviceId() is not { } deviceId) return ApiResults.Unauthorized();
        if (!WorkflowTemplateId.TryParse(id, out var templateId)) return ApiResults.NotFound();

        var template = await templates.FindAsync(templateId, ct);
        if (template is null) return ApiResults.NotFound();

        var now = DateTimeOffset.UtcNow;
        try
        {
            var debugWorkflow = await workflows.UpsertTemplateDebugAsync(
                template.Id,
                deviceId,
                NormalizeName($"[模板调试] {template.Name}", "模板调试"),
                template.SchemaVersion,
                template.GraphJson,
                now,
                ct);

            return ApiResults.Ok(ToWorkflowResponse(debugWorkflow));
        }
        catch (CreativeFlow.Application.Abstractions.Ai.WorkflowV2Exception exception)
        {
            return ApiResults.Conflict(new ErrorResponse("workflow_deleted", [exception.Message]));
        }
    }

    /// <summary>删除工作流模板，保留不存在或不可访问时的返回语义。</summary>
    internal static async Task<IActionResult> DeleteAsync(
        string id,
        HttpContext http,
        IWorkflowTemplateRepository templates,
        CancellationToken ct)
    {
        if (http.GetAuthenticatedAdmin() is null) return ApiResults.Unauthorized();
        if (!WorkflowTemplateId.TryParse(id, out var templateId)) return ApiResults.NotFound();
        var existing = await templates.FindAsync(templateId, ct);
        if (existing is null) return ApiResults.NotFound();
        if (existing.Status == WorkflowTemplateStatus.Published)
        {
            return ApiResults.Conflict(new ErrorResponse(
                "template_is_published",
                ["archive the template before deleting it"]));
        }

        // 旧模板管理入口也使用统一原子删除，不能绕过运行检查或级联清理历史。
        var center = http.RequestServices.GetRequiredService<IWorkflowCenterRepository>();
        var preview = await center.PreviewDeletionAsync("template", templateId.Value, ct);
        if (preview is null) return ApiResults.NotFound();
        var result = await center.DeleteAsync("template", templateId.Value, http.GetAuthenticatedAdmin()!.Id,
            new DeleteWorkflowRequest(preview.Name, preview.Revision), ct);
        return result.Status == "deleted" ? ApiResults.NoContent()
            : ApiResults.Conflict(new ErrorResponse("workflow_delete_blocked", ["模板状态已改变或仍有排队、运行任务，请重新查询后再删除。"]));
    }

    /// <summary>将查询结果映射为当前列表响应。</summary>
    private static WorkflowTemplateListResponse ToListResponse(
        IReadOnlyList<WorkflowTemplate> templates) => new(
        [.. templates.Select(ToSummaryResponse)]);

    /// <summary>将完整记录投影为列表需要的摘要字段。</summary>
    private static WorkflowTemplateSummaryResponse ToSummaryResponse(
        WorkflowTemplate template)
    {
        var metrics = ReadMetrics(template.GraphJson);
        return new WorkflowTemplateSummaryResponse(
            template.Id.ToString(),
            template.Name,
            template.Description,
            template.Category,
            template.Tags,
            ToStatusName(template.Status),
            template.Version,
            template.IsFeatured,
            template.SortOrder,
            template.UseCount,
            metrics.NodeCount,
            metrics.TextInputCount,
            metrics.ImageInputCount,
            metrics.OutputKinds,
            template.UpdatedAt.ToString("O"),
            template.PublishedAt?.ToString("O"));
    }

    /// <summary>将业务记录映射为既有 HTTP 响应字段。</summary>
    private static WorkflowTemplateResponse ToResponse(WorkflowTemplate template) => new(
        template.Id.ToString(),
        template.Name,
        template.Description,
        template.Category,
        template.Tags,
        template.SchemaVersion,
        ParseGraph(template.GraphJson),
        ToStatusName(template.Status),
        template.Version,
        template.IsFeatured,
        template.SortOrder,
        template.UseCount,
        template.CreatedAt.ToString("O"),
        template.UpdatedAt.ToString("O"),
        template.PublishedAt?.ToString("O"));

    /// <summary>将工作流记录映射为现有画布端响应。</summary>
    private static WorkflowResponse ToWorkflowResponse(Workflow workflow) => new(
        workflow.Id.ToString(),
        workflow.Name,
        workflow.SchemaVersion,
        ParseGraph(workflow.GraphJson),
        workflow.CreatedAt.ToString("O"),
        workflow.UpdatedAt.ToString("O"));

    /// <summary>读取图 JSON 并校验既有 schema 与大小边界。</summary>
    private static bool TryReadGraph(
        JsonNode? graph,
        out string graphJson,
        out string error,
        bool stripInputAssets = false)
    {
        graph ??= JsonNode.Parse(EmptyGraph);
        if (graph is not JsonObject root
            || root["nodes"] is not JsonArray
            || root["edges"] is not JsonArray)
        {
            graphJson = string.Empty;
            error = "workflow template graph must contain nodes and edges arrays";
            return false;
        }

        var clean = root.DeepClone().AsObject();
        SanitizeGraph(clean, stripInputAssets);
        graphJson = clean.ToJsonString();
        if (Encoding.UTF8.GetByteCount(graphJson) > MaxGraphBytes)
        {
            graphJson = string.Empty;
            error = "workflow template graph exceeds 2 MB";
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>按模板规则清除不应被发布或复用的图运行数据。</summary>
    private static void SanitizeGraph(JsonObject graph, bool stripInputAssets)
    {
        if (graph["nodes"] is not JsonArray nodes) return;
        foreach (var item in nodes)
        {
            if (item is not JsonObject node) continue;
            node.Remove("selected");
            node.Remove("dragging");
            if (!stripInputAssets
                || node["type"]?.GetValue<string>() != "image"
                || node["data"] is not JsonObject data)
            {
                continue;
            }

            data.Remove("assetUrl");
            data.Remove("mediaType");
        }
    }

    /// <summary>从图快照读取节点数量及输出等模板展示指标。</summary>
    private static WorkflowTemplateMetrics ReadMetrics(string graphJson)
    {
        // 统计仅观察已识别的节点与模型字段，保留对缺失或类型不符数据的既有容错。
        try
        {
            if (JsonNode.Parse(graphJson)?["nodes"] is not JsonArray nodes)
            {
                return new WorkflowTemplateMetrics(0, 0, 0, []);
            }

            var types = nodes
                .OfType<JsonObject>()
                .Select(node => node["type"]?.GetValue<string>() ?? string.Empty)
                .ToList();
            var outputs = new HashSet<string>(StringComparer.Ordinal);
            if (types.Contains("imageOutput")) outputs.Add("image");
            if (types.Contains("videoOutput")) outputs.Add("video");
            if (types.Contains("output"))
            {
                outputs.Add("image");
                outputs.Add("video");
            }

            return new WorkflowTemplateMetrics(
                nodes.Count,
                types.Count(type => type == "text"),
                types.Count(type => type == "image"),
                [.. outputs]);
        }
        catch (JsonException)
        {
            return new WorkflowTemplateMetrics(0, 0, 0, []);
        }
    }

    /// <summary>解析已保存的图快照，保持无效 JSON 的现有处理方式。</summary>
    private static JsonNode? ParseGraph(string json)
    {
        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>按既有缺省值和长度规则规范化显示名称。</summary>
    private static string NormalizeName(string? value, string fallback)
    {
        var name = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return name.Length <= MaxNameLength ? name : name[..MaxNameLength];
    }

    /// <summary>规范化模板描述并保留原有长度限制。</summary>
    private static string NormalizeDescription(string? value)
    {
        var description = value?.Trim() ?? string.Empty;
        return description.Length <= MaxDescriptionLength
            ? description
            : description[..MaxDescriptionLength];
    }

    /// <summary>规范化模板分类名称。</summary>
    private static string NormalizeCategory(string? value)
    {
        var category = string.IsNullOrWhiteSpace(value) ? "其他" : value.Trim();
        return category.Length <= MaxCategoryLength
            ? category
            : category[..MaxCategoryLength];
    }

    /// <summary>按原有去重、数量和长度规则规范化模板标签。</summary>
    private static string[] NormalizeTags(string[]? tags) =>
    [
        .. (tags ?? [])
            .Select(tag => tag.Trim())
            .Where(tag => tag.Length > 0)
            .Select(tag => tag.Length <= MaxTagLength ? tag : tag[..MaxTagLength])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxTags),
    ];

    /// <summary>按原有允许范围规范化显示排序。</summary>
    private static int NormalizeSortOrder(int? value) =>
        Math.Clamp(value ?? 0, -100000, 100000);

    /// <summary>将模板状态映射为既有对外字符串。</summary>
    private static string ToStatusName(WorkflowTemplateStatus status) => status switch
    {
        WorkflowTemplateStatus.Draft => "draft",
        WorkflowTemplateStatus.Published => "published",
        WorkflowTemplateStatus.Archived => "archived",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    /// <summary>解析当前业务接受的状态名称，未知值返回失败。</summary>
    private static bool TryParseStatus(
        string? value,
        out WorkflowTemplateStatus? status)
    {
        status = value?.Trim().ToLowerInvariant() switch
        {
            null or "" => null,
            "draft" => WorkflowTemplateStatus.Draft,
            "published" => WorkflowTemplateStatus.Published,
            "archived" => WorkflowTemplateStatus.Archived,
            _ => (WorkflowTemplateStatus?)null,
        };
        return string.IsNullOrWhiteSpace(value) || status is not null;
    }

    /// <summary>将校验失败映射为当前功能既有的错误结果，保留详情和错误分类。</summary>
    private static IActionResult Invalid(string detail) => ApiResults.BadRequest(
        new ErrorResponse("invalid_workflow_template", [detail]));

    /// <param name="NodeCount">节点图包含的节点数量。</param>
    /// <param name="TextInputCount">外部契约接受的文本输入数量。</param>
    /// <param name="ImageInputCount">外部契约接受的图片输入数量。</param>
    /// <param name="OutputKinds">当前端口或工具可以产生的输出种类。</param>
    private sealed record WorkflowTemplateMetrics(
        int NodeCount,
        int TextInputCount,
        int ImageInputCount,
        string[] OutputKinds);
}
