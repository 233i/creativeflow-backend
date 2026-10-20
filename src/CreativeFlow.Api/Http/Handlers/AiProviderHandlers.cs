using CreativeFlow.Api.Middleware;
using CreativeFlow.Api.Serialization;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Abstractions.Security;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;

namespace CreativeFlow.Api.Http.Handlers;

/// <summary>
/// 已登录用户可读取全局供应商和模型，管理员可执行修改。
/// </summary>
/// <remarks>
/// API 密钥始终保持加密且绝不返回。公开组提供只读目录和模型角色端点；
/// 每次修改都会检查独立的管理员会话。
/// </remarks>
internal static class AiProviderHandlers
{
    /// <summary>列出AI 渠道和模型配置，保留现有筛选、排序与访问范围。</summary>
    internal static async Task<IActionResult> ListAsync(
        HttpContext http,
        IProviderRepository providers,
        CancellationToken ct)
    {
        if (http.GetAuthenticatedAccountId() is null && http.GetAuthenticatedAdmin() is null)
        {
            return ApiResults.Unauthorized();
        }

        var list = await providers.ListAsync(ct);

        return ApiResults.Ok(new ProviderListResponse(
            [.. list.Select(provider => AiContractMapper.ToResponse(provider) with
            {
                ApiKeyHint = null,
                Models =
                [
                    .. provider.Models
                        .Where(model => model.Enabled)
                        .Select(AiContractMapper.ToResponse),
                ],
            })]));
    }

    /// <summary>列出管理员可见的AI 渠道和模型配置。</summary>
    internal static async Task<IActionResult> ListAdminAsync(
        HttpContext http,
        IProviderRepository providers,
        CancellationToken ct)
    {
        if (!IsAdmin(http))
        {
            return ApiResults.Unauthorized();
        }

        var list = await providers.ListAsync(ct);
        return ApiResults.Ok(new ProviderListResponse(
            [.. list.Select(AiContractMapper.ToResponse)]));
    }

    /// <summary>校验输入并创建AI 渠道和模型配置。</summary>
    internal static async Task<IActionResult> CreateAsync(
        CreateProviderRequest request,
        HttpContext http,
        IProviderRepository providers,
        IApiKeyProtector protector,
        CancellationToken ct)
    {
        // 在保护密钥和持久化之前完成协议、地址与模型校验，避免保存部分有效的配置。
        if (!IsAdmin(http))
        {
            return ApiResults.Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return Invalid("name is required");
        }

        if (!AiEnumNames.TryParseProviderType(request.Type, out var type))
        {
            return Invalid($"unknown provider type '{request.Type}'");
        }

        if (string.IsNullOrWhiteSpace(request.BaseUrl))
        {
            return Invalid("baseUrl is required");
        }

        if (type == ProviderType.OpenAiEmbeddings
            && !AiProvider.TryNormalizeBaseUrl(request.BaseUrl, type, out _, out var endpointError))
        {
            return Invalid(endpointError);
        }

        if (await providers.FindByNameAsync(request.Name, ct) is not null)
        {
            return ApiResults.Conflict(new ErrorResponse(
                "duplicate_name", [$"A provider named '{request.Name}' already exists."]));
        }

        var now = DateTimeOffset.UtcNow;
        var providerId = ProviderId.New();

        var provider = new AiProvider(
            providerId,
            request.BuiltinId,
            request.Name.Trim(),
            request.Description,
            request.Icon,
            type,
            string.IsNullOrWhiteSpace(request.ApiKey) ? null : protector.Protect(request.ApiKey),
            AiProvider.NormalizeBaseUrl(request.BaseUrl, type),
            request.Enabled ?? true,
            request.PresetVersion,
            await providers.NextSortOrderAsync(ct),
            now,
            now);

        await providers.InsertAsync(provider, ct);

        if (request.Models is { Length: > 0 } models)
        {
            var built = new List<AiModel>(models.Length);
            for (var i = 0; i < models.Length; i++)
            {
                if (!TryBuildModel(models[i], providerId, i, now, out var model, out var error))
                {
                    return Invalid(error);
                }

                built.Add(model);
            }

            await providers.InsertModelsIfAbsentAsync(built, ct);
        }

        var created = await providers.FindAsync(providerId, ct);

        return ApiResults.Created(
            $"/api/v1/admin/ai/providers/{providerId}",
            AiContractMapper.ToResponse(created!));
    }

    /// <summary>更新AI 渠道和模型配置，保留原有访问范围和并发结果。</summary>
    internal static async Task<IActionResult> UpdateAsync(
        string id,
        UpdateProviderRequest request,
        HttpContext http,
        IProviderRepository providers,
        IApiKeyProtector protector,
        CancellationToken ct)
    {
        if (!IsAdmin(http))
        {
            return ApiResults.Unauthorized();
        }

        if (!ProviderId.TryParse(id, out var providerId))
        {
            return Invalid("malformed provider id");
        }

        var existing = await providers.FindAsync(providerId, ct);
        if (existing is null)
        {
            return ApiResults.NotFound();
        }

        var type = existing.Type;
        if (request.Type is not null && !AiEnumNames.TryParseProviderType(request.Type, out type))
        {
            return Invalid($"unknown provider type '{request.Type}'");
        }

        if (type == ProviderType.OpenAiEmbeddings
            && !AiProvider.TryNormalizeBaseUrl(string.IsNullOrWhiteSpace(request.BaseUrl) ? existing.BaseUrl : request.BaseUrl,
                type, out _, out var endpointError))
        {
            return Invalid(endpointError);
        }

        // 三种状态：空值保留已存储密钥（编辑基础 URL 时最常见），
        // 空字符串清除密钥，其他值替换密钥。普通可空值无法表达“保留”。
        var keyUpdate = request.ApiKey switch
        {
            null => ApiKeyUpdate.Unchanged,
            "" => ApiKeyUpdate.Cleared,
            var key => ApiKeyUpdate.Replace(protector.Protect(key)),
        };

        var updated = existing with
        {
            Name = string.IsNullOrWhiteSpace(request.Name) ? existing.Name : request.Name.Trim(),
            Description = request.Description ?? existing.Description,
            Icon = request.Icon ?? existing.Icon,
            Type = type,
            // 每次更新都重新规范化：同一请求中协议可能已变化，需要移除的后缀也会随之改变。
            BaseUrl = AiProvider.NormalizeBaseUrl(
                string.IsNullOrWhiteSpace(request.BaseUrl) ? existing.BaseUrl : request.BaseUrl,
                type),
            Enabled = request.Enabled ?? existing.Enabled,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        await providers.UpdateAsync(updated, keyUpdate, ct);

        var result = await providers.FindAsync(providerId, ct);
        return ApiResults.Ok(AiContractMapper.ToResponse(result!));
    }

    /// <summary>删除AI 渠道和模型配置，保留不存在或不可访问时的返回语义。</summary>
    internal static async Task<IActionResult> DeleteAsync(
        string id,
        HttpContext http,
        IProviderRepository providers,
        CancellationToken ct)
    {
        if (!IsAdmin(http))
        {
            return ApiResults.Unauthorized();
        }

        if (!ProviderId.TryParse(id, out var providerId))
        {
            return Invalid("malformed provider id");
        }

        return await providers.DeleteAsync(providerId, ct)
            ? ApiResults.NoContent()
            : ApiResults.NotFound();
    }

    /// <summary>
    /// 通过列出供应商模型，测试已存储凭据能否访问供应商。
    /// </summary>
    internal static async Task<IActionResult> CheckAsync(
        string id,
        HttpContext http,
        IProviderRepository providers,
        IApiKeyProtector protector,
        IUpstreamModelCatalog catalog,
        CancellationToken ct)
    {
        if (!IsAdmin(http))
        {
            return ApiResults.Unauthorized();
        }

        if (!ProviderId.TryParse(id, out var providerId))
        {
            return Invalid("malformed provider id");
        }

        var provider = await providers.FindAsync(providerId, ct);
        if (provider is null)
        {
            return ApiResults.NotFound();
        }

        if (!TryUnprotect(provider, protector, out var apiKey, out var keyError))
        {
            return ApiResults.Ok(new ProviderCheckResponse(false, null, keyError, null));
        }

        var result = await catalog.ListModelsAsync(provider, apiKey, ct);

        return ApiResults.Ok(new ProviderCheckResponse(
            result.Ok, result.StatusCode, result.Error, result.Ok ? result.Models.Count : null));
    }

    /// <summary>读取指定渠道提供的上游模型目录。</summary>
    internal static async Task<IActionResult> ListUpstreamModelsAsync(
        string id,
        HttpContext http,
        IProviderRepository providers,
        IApiKeyProtector protector,
        IUpstreamModelCatalog catalog,
        CancellationToken ct)
    {
        // 模型发现沿用所选渠道的协议与凭据；读取失败通过现有错误体返回，不修改本地模型。
        if (!IsAdmin(http))
        {
            return ApiResults.Unauthorized();
        }

        if (!ProviderId.TryParse(id, out var providerId))
        {
            return Invalid("malformed provider id");
        }

        var provider = await providers.FindAsync(providerId, ct);
        if (provider is null)
        {
            return ApiResults.NotFound();
        }

        if (!TryUnprotect(provider, protector, out var apiKey, out var keyError))
        {
            return Invalid(keyError);
        }

        var result = await catalog.ListModelsAsync(provider, apiKey, ct);

        if (!result.Ok)
        {
            return Invalid(result.Error ?? "upstream request failed");
        }

        return ApiResults.Ok(new DiscoveredModelsResponse(
        [
            .. result.Models.Select(model => new DiscoveredModel(
                model.ModelKey, model.Name, model.Category.ToWire(), null)),
        ]));
    }

    /// <summary>向指定渠道添加模型配置。</summary>
    internal static async Task<IActionResult> AddModelAsync(
        string id,
        CreateModelRequest request,
        HttpContext http,
        IProviderRepository providers,
        CancellationToken ct)
    {
        if (!IsAdmin(http))
        {
            return ApiResults.Unauthorized();
        }

        if (!ProviderId.TryParse(id, out var providerId))
        {
            return Invalid("malformed provider id");
        }

        var provider = await providers.FindAsync(providerId, ct);
        if (provider is null)
        {
            return ApiResults.NotFound();
        }

        var sortOrder = provider.Models.Count == 0 ? 0 : provider.Models.Max(m => m.SortOrder) + 1;

        if (!TryBuildModel(request, providerId, sortOrder, DateTimeOffset.UtcNow,
                out var model, out var error))
        {
            return Invalid(error);
        }

        await providers.InsertModelAsync(model, ct);

        return ApiResults.Created(
            $"/api/v1/ai/models/{model.Id}", AiContractMapper.ToResponse(model));
    }

    /// <summary>添加已发现模型并跳过现有模型，可安全重复执行。</summary>
    internal static async Task<IActionResult> ImportModelsAsync(
        string id,
        ImportModelsRequest request,
        HttpContext http,
        IProviderRepository providers,
        CancellationToken ct)
    {
        // 先解析整批模型并执行现有重复项策略，再持久化允许导入的条目。
        if (!IsAdmin(http))
        {
            return ApiResults.Unauthorized();
        }

        if (!ProviderId.TryParse(id, out var providerId))
        {
            return Invalid("malformed provider id");
        }

        var provider = await providers.FindAsync(providerId, ct);
        if (provider is null)
        {
            return ApiResults.NotFound();
        }

        var now = DateTimeOffset.UtcNow;
        var nextSort = provider.Models.Count == 0 ? 0 : provider.Models.Max(m => m.SortOrder) + 1;

        var models = new List<AiModel>(request.Models.Length);
        foreach (var entry in request.Models)
        {
            if (!TryBuildModel(entry, providerId, nextSort++, now, out var model, out var error))
            {
                return Invalid(error);
            }

            models.Add(model);
        }

        var added = await providers.InsertModelsIfAbsentAsync(models, ct);

        return ApiResults.Ok(new ImportModelsResponse(added));
    }

    /// <summary>更新指定模型的能力与参数配置。</summary>
    internal static async Task<IActionResult> UpdateModelAsync(
        string modelId,
        UpdateModelRequest request,
        HttpContext http,
        IProviderRepository providers,
        CancellationToken ct)
    {
        if (!IsAdmin(http))
        {
            return ApiResults.Unauthorized();
        }

        if (!ModelId.TryParse(modelId, out var parsedId))
        {
            return Invalid("malformed model id");
        }

        var existing = await providers.FindModelAsync(parsedId, ct);
        if (existing is null)
        {
            return ApiResults.NotFound();
        }

        var type = existing.Type;
        if (request.Type is not null)
        {
            // 空字符串清除覆盖，使模型重新跟随其供应商协议。
            if (request.Type.Length == 0)
            {
                type = null;
            }
            else if (AiEnumNames.TryParseProviderType(request.Type, out var parsedType))
            {
                type = parsedType;
            }
            else
            {
                return Invalid($"unknown provider type '{request.Type}'");
            }
        }

        var category = existing.Category;
        if (request.Category is not null
            && !AiEnumNames.TryParseModelCategory(request.Category, out category))
        {
            return Invalid($"unknown model category '{request.Category}'");
        }

        var updated = existing with
        {
            Name = string.IsNullOrWhiteSpace(request.Name) ? existing.Name : request.Name.Trim(),
            Type = type,
            Category = category,
            Icon = request.Icon ?? existing.Icon,
            ContextLength = request.ContextLength ?? existing.ContextLength,
            MaxOutputTokens = request.MaxOutputTokens ?? existing.MaxOutputTokens,
            SupportsVision = request.SupportsVision ?? existing.SupportsVision,
            SupportsFunctionCall = request.SupportsFunctionCall ?? existing.SupportsFunctionCall,
            SupportsThinking = request.SupportsThinking ?? existing.SupportsThinking,
            Enabled = request.Enabled ?? existing.Enabled,
        };

        await providers.UpdateModelAsync(updated, ct);

        return ApiResults.Ok(AiContractMapper.ToResponse(updated));
    }

    /// <summary>删除指定模型配置。</summary>
    internal static async Task<IActionResult> DeleteModelAsync(
        string modelId,
        HttpContext http,
        IProviderRepository providers,
        CancellationToken ct)
    {
        if (!IsAdmin(http))
        {
            return ApiResults.Unauthorized();
        }

        if (!ModelId.TryParse(modelId, out var parsedId))
        {
            return Invalid("malformed model id");
        }

        return await providers.DeleteModelAsync(parsedId, ct)
            ? ApiResults.NoContent()
            : ApiResults.NotFound();
    }

    /// <summary>读取管理员配置的默认模型角色。</summary>
    internal static async Task<IActionResult> ListModelSlotsAsync(
        HttpContext http,
        IModelSlotRepository slots,
        CancellationToken ct)
    {
        if (http.GetAuthenticatedAccountId() is null && http.GetAuthenticatedAdmin() is null)
        {
            return ApiResults.Unauthorized();
        }

        var configured = await slots.ListAsync(ct);
        return ApiResults.Ok(new ModelSlotListResponse([.. configured.Select(ToResponse)]));
    }

    /// <summary>修改指定默认模型角色的选择。</summary>
    internal static async Task<IActionResult> UpdateModelSlotAsync(
        string kind,
        UpdateModelSlotRequest request,
        HttpContext http,
        IProviderRepository providers,
        IModelSlotRepository slots,
        CancellationToken ct)
    {
        // 两个标识同时为空表示清空角色；指定模型时必须先通过归属、启用状态和类别校验。
        if (!IsAdmin(http))
        {
            return ApiResults.Unauthorized();
        }

        var normalizedKind = kind.Trim().ToLowerInvariant();
        if (normalizedKind is not ("chat" or "fast" or "image" or "video"))
        {
            return Invalid("unknown model slot");
        }

        var clearing = request.ProviderId is null && request.ModelId is null;
        var providerId = default(ProviderId);
        var modelId = default(ModelId);
        if (!clearing &&
            (!ProviderId.TryParse(request.ProviderId, out providerId) ||
             !ModelId.TryParse(request.ModelId, out modelId)))
        {
            return Invalid("providerId and modelId must both be valid ids, or both be null");
        }

        ProviderId? selectedProviderId = null;
        ModelId? selectedModelId = null;
        if (!clearing)
        {
            var provider = await providers.FindAsync(providerId, ct);
            var model = provider?.Models.FirstOrDefault(candidate => candidate.Id == modelId);
            if (provider is null || model is null)
            {
                return ApiResults.NotFound();
            }

            if (!provider.Enabled || !model.Enabled)
            {
                return Invalid("the selected provider and model must both be enabled");
            }

            var expectedCategory = normalizedKind is "chat" or "fast"
                ? ModelCategory.Chat
                : normalizedKind == "image" ? ModelCategory.Image : ModelCategory.Video;
            if (model.Category != expectedCategory)
            {
                return Invalid($"the {normalizedKind} slot requires a {expectedCategory.ToWire()} model");
            }

            selectedProviderId = provider.Id;
            selectedModelId = model.Id;
        }

        var now = DateTimeOffset.UtcNow;
        await slots.SetAsync(normalizedKind, selectedProviderId, selectedModelId, now, ct);
        return ApiResults.Ok(new ModelSlotResponse(
            normalizedKind,
            selectedProviderId?.ToString(),
            selectedModelId?.ToString(),
            now.ToString("O")));
    }

    /// <summary>将业务记录映射为既有 HTTP 响应字段。</summary>
    private static ModelSlotResponse ToResponse(ConfiguredModelSlot slot) => new(
        slot.Kind,
        slot.ProviderId?.ToString(),
        slot.ModelId?.ToString(),
        slot.UpdatedAt.ToString("O"));

    /// <summary>读取管理员设置的图片生成策略。</summary>
    internal static async Task<IActionResult> GetImageGenerationSettingsAsync(
        HttpContext http,
        IImageGenerationSettingsRepository settings,
        CancellationToken ct)
    {
        if (!IsAdmin(http))
        {
            return ApiResults.Unauthorized();
        }

        var configured = await settings.GetAsync(ct);
        return ApiResults.Ok(ToResponse(configured));
    }

    /// <summary>更新应用级图片生成策略。</summary>
    internal static async Task<IActionResult> UpdateImageGenerationSettingsAsync(
        UpdateImageGenerationSettingsRequest request,
        HttpContext http,
        IImageGenerationSettingsRepository settings,
        CancellationToken ct)
    {
        if (!IsAdmin(http))
        {
            return ApiResults.Unauthorized();
        }

        if (!ImageResponseFormats.TryNormalize(request.ResponseFormat, out var responseFormat))
        {
            return Invalid("responseFormat must be 'url' or 'base64'");
        }

        var now = DateTimeOffset.UtcNow;
        await settings.SetResponseFormatAsync(responseFormat, now, ct);
        return ApiResults.Ok(new ImageGenerationSettingsResponse(responseFormat, now.ToString("O")));
    }

    /// <summary>将业务记录映射为既有 HTTP 响应字段。</summary>
    private static ImageGenerationSettingsResponse ToResponse(
        ConfiguredImageGenerationSettings settings) => new(
            settings.ResponseFormat,
            settings.UpdatedAt.ToString("O"));

    /// <summary>判断本次请求是否具有有效的独立管理员会话。</summary>
    private static bool IsAdmin(HttpContext http) =>
        http.GetAuthenticatedAdmin() is not null;

    /// <summary>校验模型配置并构造与原契约一致的模型记录。</summary>
    private static bool TryBuildModel(
        CreateModelRequest request,
        ProviderId providerId,
        int sortOrder,
        DateTimeOffset now,
        out AiModel model,
        out string error)
    {
        // 显式协议覆盖与模型类别分别校验，避免把渠道默认协议误当作模型自身配置。
        model = null!;

        if (string.IsNullOrWhiteSpace(request.ModelKey))
        {
            error = "modelKey is required";
            return false;
        }

        ProviderType? type = null;
        if (!string.IsNullOrEmpty(request.Type))
        {
            if (!AiEnumNames.TryParseProviderType(request.Type, out var parsedType))
            {
                error = $"unknown provider type '{request.Type}'";
                return false;
            }

            type = parsedType;
        }

        var category = ModelCategory.Chat;
        if (request.Category is not null
            && !AiEnumNames.TryParseModelCategory(request.Category, out category))
        {
            error = $"unknown model category '{request.Category}'";
            return false;
        }

        var key = request.ModelKey.Trim();

        model = new AiModel(
            ModelId.New(),
            providerId,
            key,
            string.IsNullOrWhiteSpace(request.Name) ? key : request.Name.Trim(),
            request.Enabled ?? true,
            type,
            category,
            request.Icon,
            request.ContextLength,
            request.MaxOutputTokens,
            request.SupportsVision ?? false,
            request.SupportsFunctionCall ?? false,
            request.SupportsThinking ?? false,
            sortOrder,
            now);

        error = string.Empty;
        return true;
    }

    /// <summary>
    /// 为上游调用恢复供应商 API 密钥。
    /// </summary>
    /// <remarks>
    /// 配置的加密密钥变化或数据行被篡改时，解密会失败。两种情况都无法通过重试解决，
    /// 因此消息应提示用户重新输入密钥，而不是表现为临时错误。
    /// </remarks>
    private static bool TryUnprotect(
        AiProvider provider,
        IApiKeyProtector protector,
        out string apiKey,
        out string error)
    {
        if (provider.ApiKey is null)
        {
            apiKey = string.Empty;
            error = string.Empty;
            return true;
        }

        try
        {
            apiKey = protector.Unprotect(provider.ApiKey);
            error = string.Empty;
            return true;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            apiKey = string.Empty;
            error = "The stored API key could not be decrypted. Re-enter it in settings.";
            return false;
        }
    }

    /// <summary>将校验失败映射为当前功能既有的错误结果，保留详情和错误分类。</summary>
    private static IActionResult Invalid(string detail) =>
        ApiResults.BadRequest(new ErrorResponse("invalid_request", [detail]));
}
