using CreativeFlow.Api.Middleware;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Abstractions.Security;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Api.Http.Handlers;

/// <summary>
/// 将供应商配置包应用到应用级目录。
/// </summary>
/// <remarks>
/// 此端点仍是管理员授权、规范化、URL 验证、API 密钥保护和模型输入验证的权威边界。
/// 导入过程中绝不会获取供应商的上游目录。
/// </remarks>
internal static class AiConfigImportHandlers
{
    /// <summary>一次导入允许的渠道数量上限。</summary>
    private const int MaxProviders = 50;
    /// <summary>一次配置操作允许的渠道模型数量上限。</summary>
    private const int MaxModelsPerProvider = 200;
    /// <summary>渠道名称允许的最多字符数。</summary>
    private const int MaxProviderNameLength = 200;
    /// <summary>内置渠道标识允许的最多字符数。</summary>
    private const int MaxBuiltinIdLength = 64;
    /// <summary>渠道基础地址允许的最多字符数。</summary>
    private const int MaxBaseUrlLength = 2048;
    /// <summary>渠道密钥允许的最多字符数。</summary>
    private const int MaxApiKeyLength = 4096;
    /// <summary>说明文本允许的最多字符数。</summary>
    private const int MaxDescriptionLength = 1000;
    /// <summary>上游模型标识允许的最多字符数。</summary>
    private const int MaxModelKeyLength = 200;
    /// <summary>模型显示名称允许的最多字符数。</summary>
    private const int MaxModelNameLength = 200;
    /// <summary>图标标识或地址允许的最多字符数。</summary>
    private const int MaxIconLength = 2048;
    /// <summary>允许的内置预设版本号上限。</summary>
    private const int MaxPresetVersion = 100_000;

    /// <summary>校验并导入 URL 中的渠道配置，保持原有重复项处理策略。</summary>
    internal static async Task<IActionResult> ImportAsync(
        ImportConfigRequest request,
        HttpContext http,
        IProviderRepository providers,
        IApiKeyProtector protector,
        CancellationToken ct)
    {
        if (http.GetAuthenticatedAdmin() is null)
        {
            return ApiResults.Unauthorized();
        }

        if (request.Providers is null || request.Providers.Length == 0)
        {
            return Invalid("at least one provider is required");
        }

        if (request.Providers.Length > MaxProviders)
        {
            return Invalid($"at most {MaxProviders} providers may be imported");
        }

        // 首次写入前验证并解析所有条目。除提供更好的错误消息外，
        // 还可防止后续格式错误条目使先前供应商处于半导入状态。
        var plans = new List<ImportPlan>(request.Providers.Length);
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in request.Providers)
        {
            if (entry is null)
            {
                return Invalid("provider entry is required");
            }

            var builtinId = NormalizeOptional(entry.BuiltinId);
            var requestedName = NormalizeOptional(entry.Name);

            AiProvider? existing = null;
            if (builtinId is not null)
            {
                existing = await providers.FindByBuiltinIdAsync(builtinId, ct);
            }

            // 此回退使新版 URL 导入器可以接管旧版本在稳定内置标识查询出现前创建的供应商。
            if (existing is null && requestedName is not null)
            {
                existing = await providers.FindByNameAsync(requestedName, ct);
            }

            var name = requestedName ?? existing?.Name ?? builtinId;
            if (name is null)
            {
                return Invalid("provider name is required");
            }

            if (name.Length > MaxProviderNameLength)
            {
                return Invalid(
                    $"provider name must be at most {MaxProviderNameLength} characters");
            }

            if (builtinId is { Length: > MaxBuiltinIdLength })
            {
                return Invalid(
                    $"provider '{name}' has a built-in id longer than {MaxBuiltinIdLength} characters");
            }

            var identity = builtinId is not null
                ? $"builtin:{builtinId}"
                : $"name:{name}";
            if (!identities.Add(identity))
            {
                return Invalid($"provider '{name}' appears more than once in the request");
            }

            if (!names.Add(name))
            {
                return Invalid($"provider name '{name}' appears more than once in the request");
            }

            ProviderType type;
            if (entry.Type is not null)
            {
                if (!AiEnumNames.TryParseProviderType(entry.Type, out type))
                {
                    return Invalid($"unknown provider type '{entry.Type}'");
                }
            }
            else if (existing is not null)
            {
                type = existing.Type;
            }
            else
            {
                return Invalid($"provider '{name}' has no type");
            }

            string baseUrl;
            if (entry.BaseUrl is not null)
            {
                if (!TryNormalizeBaseUrl(entry.BaseUrl, type, out baseUrl, out var urlError))
                {
                    return Invalid($"provider '{name}' {urlError}");
                }
            }
            else if (existing is not null)
            {
                // 若同一请求显式更改了协议，则重新规范化现有 URL。浏览器通常发送完整解析后的 URL，
                // 但保留此回退也能保障直接 API 客户端的安全。
                if (!TryNormalizeBaseUrl(existing.BaseUrl, type, out baseUrl, out var urlError))
                {
                    return Invalid($"provider '{name}' {urlError}");
                }
            }
            else
            {
                return Invalid($"provider '{name}' has no baseUrl and does not exist yet");
            }

            if (entry.ApiKey is not null && string.IsNullOrWhiteSpace(entry.ApiKey))
            {
                return Invalid($"provider '{name}' apiKey cannot be empty; omit it to keep the stored key");
            }

            if (entry.ApiKey is { Length: > MaxApiKeyLength })
            {
                return Invalid(
                    $"provider '{name}' apiKey is longer than {MaxApiKeyLength} characters");
            }

            if (baseUrl.Length > MaxBaseUrlLength)
            {
                return Invalid(
                    $"provider '{name}' baseUrl is longer than {MaxBaseUrlLength} characters");
            }

            if (entry.Description is { Length: > MaxDescriptionLength })
            {
                return Invalid(
                    $"provider '{name}' description is longer than {MaxDescriptionLength} characters");
            }

            if (entry.Icon is { Length: > MaxIconLength })
            {
                return Invalid($"provider '{name}' icon is too long");
            }

            if (entry.PresetVersion is <= 0 or > MaxPresetVersion)
            {
                return Invalid($"provider '{name}' has an invalid presetVersion");
            }

            var definitions = new List<ModelDefinition>();
            var modelKeys = new HashSet<string>(StringComparer.Ordinal);
            var models = entry.Models ?? [];
            if (models.Length > MaxModelsPerProvider)
            {
                return Invalid(
                    $"provider '{name}' declares {models.Length} models; "
                    + $"the limit is {MaxModelsPerProvider}");
            }

            foreach (var model in models)
            {
                if (model is null)
                {
                    return Invalid($"provider '{name}' contains a null model");
                }

                if (!TryValidateModel(model, out var definition, out var modelError))
                {
                    return Invalid($"provider '{name}' {modelError}");
                }

                if (!modelKeys.Add(definition.ModelKey))
                {
                    return Invalid(
                        $"provider '{name}' declares model '{definition.ModelKey}' more than once");
                }

                definitions.Add(definition);
            }

            plans.Add(new ImportPlan(
                entry,
                existing,
                builtinId,
                name,
                type,
                baseUrl,
                definitions));
        }

        var now = DateTimeOffset.UtcNow;
        var created = 0;
        var updated = 0;

        foreach (var plan in plans)
        {
            var providerId = plan.Existing?.Id ?? ProviderId.New();
            var secret = plan.Entry.ApiKey is null
                ? null
                : protector.Protect(plan.Entry.ApiKey);

            if (plan.Existing is null)
            {
                await providers.InsertAsync(
                    new AiProvider(
                        providerId,
                        plan.BuiltinId,
                        plan.Name,
                        plan.Entry.Description,
                        plan.Entry.Icon,
                        plan.Type,
                        secret,
                        plan.BaseUrl,
                        true,
                        plan.Entry.PresetVersion,
                        await providers.NextSortOrderAsync(ct),
                        now,
                        now),
                    ct);

                created++;
            }
            else
            {
                var existing = plan.Existing;
                var keyUpdate = secret is null
                    ? ApiKeyUpdate.Unchanged
                    : ApiKeyUpdate.Replace(secret);

                await providers.UpdateAsync(
                    existing with
                    {
                        // 显式内置标识可以接管按名称找到的旧记录。自定义导入绝不会移除现有内置标记。
                        BuiltinId = plan.BuiltinId ?? existing.BuiltinId,
                        Name = plan.Name,
                        Description = plan.Entry.Description ?? existing.Description,
                        Icon = plan.Entry.Icon ?? existing.Icon,
                        Type = plan.Type,
                        BaseUrl = plan.BaseUrl,
                        PresetVersion = plan.Entry.PresetVersion ?? existing.PresetVersion,
                        UpdatedAt = now,
                    },
                    keyUpdate,
                    ct);

                updated++;
            }

            if (plan.Models.Count == 0)
            {
                continue;
            }

            var built = plan.Models
                .Select((model, index) => new AiModel(
                    ModelId.New(),
                    providerId,
                    model.ModelKey,
                    model.Name,
                    model.Enabled,
                    model.Type,
                    model.Category,
                    model.Icon,
                    model.ContextLength,
                    model.MaxOutputTokens,
                    model.SupportsVision,
                    model.SupportsFunctionCall,
                    model.SupportsThinking,
                    index,
                    now))
                .ToList();

            // 仅在缺失时添加，因此重新打开链接不会覆盖用户对单个模型的编辑。
            await providers.InsertModelsIfAbsentAsync(built, ct);
        }

        return ApiResults.Ok(new ImportConfigResponse(created, updated));
    }

    /// <summary>验证 HTTP(S) 基础地址并应用既有协议后缀规范化规则。</summary>
    private static bool TryNormalizeBaseUrl(
        string value,
        ProviderType type,
        out string normalized,
        out string error)
    {
        if (AiProvider.TryNormalizeBaseUrl(value, type, out normalized, out var detail))
        {
            error = string.Empty;
            return true;
        }

        error = detail;
        return false;
    }

    /// <summary>校验导入的模型定义与类别、能力及长度限制。</summary>
    private static bool TryValidateModel(
        CreateModelRequest model,
        out ModelDefinition definition,
        out string error)
    {
        // 导入数据不能依赖导出端已经校验；按当前模型分类与协议规则逐项拒绝非法配置。
        definition = default!;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(model.ModelKey))
        {
            error = "has a model with no modelKey";
            return false;
        }

        var key = model.ModelKey.Trim();
        if (key.Length > MaxModelKeyLength)
        {
            error = $"has a model key longer than {MaxModelKeyLength} characters";
            return false;
        }

        ProviderType? modelType = null;
        if (model.Type is not null)
        {
            if (string.IsNullOrWhiteSpace(model.Type)
                || !AiEnumNames.TryParseProviderType(model.Type, out var parsed))
            {
                error = $"has model '{key}' with unknown provider type '{model.Type}'";
                return false;
            }

            modelType = parsed;
        }

        var category = ModelCategory.Chat;
        if (model.Category is not null
            && !AiEnumNames.TryParseModelCategory(model.Category, out category))
        {
            error = $"has model '{key}' with unknown category '{model.Category}'";
            return false;
        }

        var name = string.IsNullOrWhiteSpace(model.Name) ? key : model.Name.Trim();
        if (name.Length > MaxModelNameLength)
        {
            error = $"has model '{key}' with a name longer than {MaxModelNameLength} characters";
            return false;
        }

        if (model.ContextLength is <= 0 or > 100_000_000)
        {
            error = $"has model '{key}' with an invalid contextLength";
            return false;
        }

        if (model.MaxOutputTokens is <= 0 or > 10_000_000)
        {
            error = $"has model '{key}' with an invalid maxOutputTokens";
            return false;
        }

        if (model.Icon is { Length: > MaxIconLength })
        {
            error = $"has model '{key}' with an icon that is too long";
            return false;
        }

        definition = new ModelDefinition(
            key,
            name,
            modelType,
            category,
            model.Icon,
            model.ContextLength,
            model.MaxOutputTokens,
            model.SupportsVision ?? false,
            model.SupportsFunctionCall ?? false,
            model.SupportsThinking ?? false,
            model.Enabled ?? true);
        return true;
    }

    /// <summary>清理可选文本，将空白输入归一为未提供。</summary>
    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>将校验失败映射为当前功能既有的错误结果，保留详情和错误分类。</summary>
    private static IActionResult Invalid(string detail) =>
        ApiResults.BadRequest(new ErrorResponse("invalid_request", [detail]));

    /// <param name="Entry">当前处理的项目。</param>
    /// <param name="Existing">已经存在且可复用的记录。</param>
    /// <param name="BuiltinId">内置渠道预设的稳定标识，非内置项可为空。</param>
    /// <param name="Name">实体或配置项的名称。</param>
    /// <param name="Type">该数据项的协议、值或事件类型，沿用所属契约的取值。</param>
    /// <param name="BaseUrl">调用方配置并规范化后的上游基础地址。</param>
    /// <param name="Models">当前渠道配置的模型集合。</param>
    private sealed record ImportPlan(
        ImportProviderEntry Entry,
        AiProvider? Existing,
        string? BuiltinId,
        string Name,
        ProviderType Type,
        string BaseUrl,
        IReadOnlyList<ModelDefinition> Models);

    /// <param name="ModelKey">上游识别的模型名称，不等同于本地模型配置标识。</param>
    /// <param name="Name">实体或配置项的名称。</param>
    /// <param name="Type">该数据项的协议、值或事件类型，沿用所属契约的取值。</param>
    /// <param name="Category">所属的模型、模板或内容分类。</param>
    /// <param name="Icon">显示用图标标识或地址。</param>
    /// <param name="ContextLength">模型声明的上下文 token 容量。</param>
    /// <param name="MaxOutputTokens">模型调用允许的最大输出 token 数量。</param>
    /// <param name="SupportsVision">模型是否声明支持图片输入。</param>
    /// <param name="SupportsFunctionCall">模型是否声明支持工具/函数调用。</param>
    /// <param name="SupportsThinking">模型是否声明支持思考内容。</param>
    /// <param name="Enabled">当前配置或资源是否启用。</param>
    private sealed record ModelDefinition(
        string ModelKey,
        string Name,
        ProviderType? Type,
        ModelCategory Category,
        string? Icon,
        int? ContextLength,
        int? MaxOutputTokens,
        bool SupportsVision,
        bool SupportsFunctionCall,
        bool SupportsThinking,
        bool Enabled);
}
