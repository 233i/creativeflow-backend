using Dapper;
using Npgsql;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;

namespace CreativeFlow.Infrastructure.Persistence;

/// <remarks>
/// Dapper.AOT 约束适用于此处每个调用点：只能使用单泛型 <c>QueryAsync&lt;T&gt;</c>、
/// <c>ExecuteAsync</c>、<c>ExecuteScalarAsync</c> 和静态类型参数类。不支持多重映射，
/// 因此供应商及其模型通过两次扁平查询获取并在内存中合并，而不使用 <c>splitOn</c>，
/// 参见 <see cref="ListAsync"/>。
/// <para>
/// 枚举值通过 <see cref="AiEnumNames"/> 以传输字符串读写。Dapper 可以按序号映射枚举，
/// 但那会让数据库与声明顺序耦合。
/// </para>
/// </remarks>
/// <summary>初始化 ProviderRepository 的依赖与实例状态。</summary>
public sealed class ProviderRepository(CreativeFlowConnectionFactory connections) : IProviderRepository
{
    /// <summary>渠道查询使用的固定数据库列清单。</summary>
    private const string ProviderColumns = """
        provider_id, builtin_id, name, description, icon, type,
        api_key_cipher, api_key_nonce, api_key_tag, api_key_hint,
        base_url, enabled, preset_version, sort_order, created_at, updated_at
        """;

    /// <summary>模型查询使用的固定数据库列清单。</summary>
    private const string ModelColumns = """
        model_id, provider_id, model_key, name, enabled, type, category, icon,
        context_length, max_output_tokens, supports_vision, supports_function_call,
        supports_thinking, sort_order, created_at
        """;

    /// <summary>读取全局渠道及其模型目录，不返回旧设备级渠道记录。</summary>
    public async Task<IReadOnlyList<AiProvider>> ListAsync(CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        var providerRows = await connection.QueryAsync<ProviderRow>(
            $"""
            SELECT {ProviderColumns}
            FROM ai_provider
            WHERE device_id IS NULL
            ORDER BY sort_order, created_at
            """);

        // 无论存在多少全局供应商都只需一次往返。Dapper.AOT 无法为 IN 子句绑定数组参数，
        // 因此所有权谓词保留在联接中。
        var modelRows = await connection.QueryAsync<ModelRow>(
            $"""
            SELECT m.model_id, m.provider_id, m.model_key, m.name, m.enabled, m.type, m.category,
                   m.icon, m.context_length, m.max_output_tokens, m.supports_vision,
                   m.supports_function_call, m.supports_thinking, m.sort_order, m.created_at
            FROM ai_model m
            JOIN ai_provider p ON p.provider_id = m.provider_id
            WHERE p.device_id IS NULL
            ORDER BY m.sort_order, m.created_at
            """);

        var modelsByProvider = modelRows
            .Select(row => row.ToDomain())
            .GroupBy(model => model.ProviderId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<AiModel>)[.. group]);

        return providerRows
            .Select(row =>
            {
                var provider = row.ToDomain();
                return modelsByProvider.TryGetValue(provider.Id, out var models)
                    ? provider with { Models = models }
                    : provider;
            })
            .ToList();
    }

    /// <summary>按标识读取全局渠道及关联模型；旧设备级记录或不存在的渠道返回空。</summary>
    public async Task<AiProvider?> FindAsync(
        ProviderId providerId,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        // 全局所有权是谓词的一部分，因此即使已知旧版设备级记录的标识，
        // 它们也不会泄露回商业目录。
        var providerRow = await connection.QueryFirstOrDefaultAsync<ProviderRow>(
            $"""
            SELECT {ProviderColumns}
            FROM ai_provider
            WHERE provider_id = @ProviderId AND device_id IS NULL
            """,
            new ProviderIdParam { ProviderId = providerId.Value });

        if (providerRow is null)
        {
            return null;
        }

        var modelRows = await connection.QueryAsync<ModelRow>(
            $"""
            SELECT {ModelColumns}
            FROM ai_model
            WHERE provider_id = @ProviderId
            ORDER BY sort_order, created_at
            """,
            new ProviderIdParam { ProviderId = providerId.Value });

        return providerRow.ToDomain() with
        {
            Models = modelRows.Select(row => row.ToDomain()).ToList(),
        };
    }

    /// <summary>按不区分大小写的名称查找全局渠道，并加载其模型列表。</summary>
    public async Task<AiProvider?> FindByNameAsync(
        string name,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        var row = await connection.QueryFirstOrDefaultAsync<ProviderRow>(
            $"""
            SELECT {ProviderColumns}
            FROM ai_provider
            WHERE device_id IS NULL AND lower(name) = lower(@Name)
            """,
            new ProviderNameParam { Name = name });

        if (row is null)
        {
            return null;
        }

        var provider = row.ToDomain();

        var modelRows = await connection.QueryAsync<ModelRow>(
            $"""
            SELECT {ModelColumns}
            FROM ai_model
            WHERE provider_id = @ProviderId
            ORDER BY sort_order, created_at
            """,
            new ProviderIdParam { ProviderId = provider.Id.Value });

        return provider with { Models = modelRows.Select(r => r.ToDomain()).ToList() };
    }

    /// <summary>按内置渠道标识查找全局渠道及模型列表。</summary>
    public async Task<AiProvider?> FindByBuiltinIdAsync(
        string builtinId,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        var row = await connection.QueryFirstOrDefaultAsync<ProviderRow>(
            $"""
            SELECT {ProviderColumns}
            FROM ai_provider
            WHERE device_id IS NULL AND builtin_id = @BuiltinId
            """,
            new ProviderBuiltinParam { BuiltinId = builtinId });

        if (row is null)
        {
            return null;
        }

        var provider = row.ToDomain();

        var modelRows = await connection.QueryAsync<ModelRow>(
            $"""
            SELECT {ModelColumns}
            FROM ai_model
            WHERE provider_id = @ProviderId
            ORDER BY sort_order, created_at
            """,
            new ProviderIdParam { ProviderId = provider.Id.Value });

        return provider with { Models = modelRows.Select(r => r.ToDomain()).ToList() };
    }

    /// <summary>保存新的全局渠道配置与加密密钥材料，不在此处拉取上游模型。</summary>
    public async Task InsertAsync(AiProvider provider, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        await connection.ExecuteAsync(
            """
            INSERT INTO ai_provider (provider_id, device_id, builtin_id, name, description, icon,
                                     type, api_key_cipher, api_key_nonce, api_key_tag,
                                     api_key_hint, base_url, enabled, preset_version, sort_order,
                                     created_at, updated_at)
            VALUES (@ProviderId, NULL, @BuiltinId, @Name, @Description, @Icon,
                    @Type, @ApiKeyCipher, @ApiKeyNonce, @ApiKeyTag,
                    @ApiKeyHint, @BaseUrl, @Enabled, @PresetVersion, @SortOrder,
                    @CreatedAt, @UpdatedAt)
            """,
            new InsertProviderParams
            {
                ProviderId = provider.Id.Value,
                BuiltinId = provider.BuiltinId,
                Name = provider.Name,
                Description = provider.Description,
                Icon = provider.Icon,
                Type = provider.Type.ToWire(),
                ApiKeyCipher = provider.ApiKey?.Cipher,
                ApiKeyNonce = provider.ApiKey?.Nonce,
                ApiKeyTag = provider.ApiKey?.Tag,
                ApiKeyHint = provider.ApiKey?.Hint,
                BaseUrl = provider.BaseUrl,
                Enabled = provider.Enabled,
                PresetVersion = provider.PresetVersion,
                SortOrder = provider.SortOrder,
                CreatedAt = provider.CreatedAt,
                UpdatedAt = provider.UpdatedAt,
            });
    }

    /// <summary>更新全局渠道配置，明确区分保持、替换和清空 API Key，避免误覆盖密钥。</summary>
    public async Task UpdateAsync(AiProvider provider, ApiKeyUpdate apiKey, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        // 每种密钥处理方式使用一条语句，而不使用 COALESCE 技巧：既能表达“保持密钥不变”，
        // 也能表达“清除密钥”，且不会意外进入任一分支。
        var keyClause = apiKey switch
        {
            { Secret: not null } => """
                api_key_cipher = @ApiKeyCipher, api_key_nonce = @ApiKeyNonce,
                api_key_tag = @ApiKeyTag, api_key_hint = @ApiKeyHint,
                """,
            { Clear: true } => """
                api_key_cipher = NULL, api_key_nonce = NULL,
                api_key_tag = NULL, api_key_hint = NULL,
                """,
            _ => string.Empty,
        };

        await connection.ExecuteAsync(
            $"""
            UPDATE ai_provider
            SET builtin_id = @BuiltinId, name = @Name, description = @Description, icon = @Icon,
                type = @Type, base_url = @BaseUrl, enabled = @Enabled,
                preset_version = @PresetVersion, sort_order = @SortOrder,
                {keyClause}
                updated_at = @UpdatedAt
            WHERE provider_id = @ProviderId AND device_id IS NULL
            """,
            new UpdateProviderParams
            {
                ProviderId = provider.Id.Value,
                BuiltinId = provider.BuiltinId,
                Name = provider.Name,
                Description = provider.Description,
                Icon = provider.Icon,
                Type = provider.Type.ToWire(),
                BaseUrl = provider.BaseUrl,
                Enabled = provider.Enabled,
                PresetVersion = provider.PresetVersion,
                SortOrder = provider.SortOrder,
                UpdatedAt = provider.UpdatedAt,
                ApiKeyCipher = apiKey.Secret?.Cipher,
                ApiKeyNonce = apiKey.Secret?.Nonce,
                ApiKeyTag = apiKey.Secret?.Tag,
                ApiKeyHint = apiKey.Secret?.Hint,
            });
    }

    /// <summary>仅删除全局渠道，关联模型交由数据库外键级联清理。</summary>
    public async Task<bool> DeleteAsync(
        ProviderId providerId,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        // 模型通过外键级联处理。
        var affected = await connection.ExecuteAsync(
            "DELETE FROM ai_provider WHERE provider_id = @ProviderId AND device_id IS NULL",
            new ProviderIdParam { ProviderId = providerId.Value });

        return affected > 0;
    }

    /// <summary>根据全局渠道当前最大排序值计算下一排序位置，不写入新渠道。</summary>
    public async Task<int> NextSortOrderAsync(CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        return await connection.ExecuteScalarAsync<int>(
            "SELECT COALESCE(MAX(sort_order), -1) + 1 FROM ai_provider WHERE device_id IS NULL");
    }

    /// <summary>保存单个模型配置，重复记录的约束冲突交给调用方处理。</summary>
    public async Task InsertModelAsync(AiModel model, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await InsertModelCoreAsync(connection, model, onConflictDoNothing: false);
    }

    /// <summary>逐项插入尚不存在的模型配置，保留已存在记录并返回新增数量。</summary>
    public async Task<int> InsertModelsIfAbsentAsync(
        IReadOnlyList<AiModel> models,
        CancellationToken ct)
    {
        if (models.Count == 0)
        {
            return 0;
        }

        await using var connection = await connections.OpenAsync(ct);

        var added = 0;
        foreach (var model in models)
        {
            added += await InsertModelCoreAsync(connection, model, onConflictDoNothing: true);
        }

        return added;
    }

    /// <summary>执行单条模型写入，并按调用模式处理唯一性竞争。</summary>
    private static async Task<int> InsertModelCoreAsync(
        NpgsqlConnection connection,
        AiModel model,
        bool onConflictDoNothing)
    {
        var conflict = onConflictDoNothing ? "ON CONFLICT (provider_id, model_key) DO NOTHING" : "";

        return await connection.ExecuteAsync(
            $"""
            INSERT INTO ai_model (model_id, provider_id, model_key, name, enabled, type, category,
                                  icon, context_length, max_output_tokens, supports_vision,
                                  supports_function_call, supports_thinking, sort_order, created_at)
            VALUES (@ModelId, @ProviderId, @ModelKey, @Name, @Enabled, @Type, @Category,
                    @Icon, @ContextLength, @MaxOutputTokens, @SupportsVision,
                    @SupportsFunctionCall, @SupportsThinking, @SortOrder, @CreatedAt)
            {conflict}
            """,
            new InsertModelParams
            {
                ModelId = model.Id.Value,
                ProviderId = model.ProviderId.Value,
                ModelKey = model.ModelKey,
                Name = model.Name,
                Enabled = model.Enabled,
                Type = model.Type?.ToWire(),
                Category = model.Category.ToWire(),
                Icon = model.Icon,
                ContextLength = model.ContextLength,
                MaxOutputTokens = model.MaxOutputTokens,
                SupportsVision = model.SupportsVision,
                SupportsFunctionCall = model.SupportsFunctionCall,
                SupportsThinking = model.SupportsThinking,
                SortOrder = model.SortOrder,
                CreatedAt = model.CreatedAt,
            });
    }

    /// <summary>按模型标识更新名称、能力、启用状态和协议覆盖等配置。</summary>
    public async Task UpdateModelAsync(AiModel model, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        await connection.ExecuteAsync(
            """
            UPDATE ai_model
            SET model_key = @ModelKey, name = @Name, enabled = @Enabled, type = @Type,
                category = @Category, icon = @Icon, context_length = @ContextLength,
                max_output_tokens = @MaxOutputTokens, supports_vision = @SupportsVision,
                supports_function_call = @SupportsFunctionCall,
                supports_thinking = @SupportsThinking, sort_order = @SortOrder
            WHERE model_id = @ModelId
            """,
            new UpdateModelParams
            {
                ModelId = model.Id.Value,
                ModelKey = model.ModelKey,
                Name = model.Name,
                Enabled = model.Enabled,
                Type = model.Type?.ToWire(),
                Category = model.Category.ToWire(),
                Icon = model.Icon,
                ContextLength = model.ContextLength,
                MaxOutputTokens = model.MaxOutputTokens,
                SupportsVision = model.SupportsVision,
                SupportsFunctionCall = model.SupportsFunctionCall,
                SupportsThinking = model.SupportsThinking,
                SortOrder = model.SortOrder,
            });
    }

    /// <summary>仅查询归属于全局渠道的模型，旧设备级模型不进入当前目录。</summary>
    public async Task<AiModel?> FindModelAsync(
        ModelId modelId,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        var row = await connection.QueryFirstOrDefaultAsync<ModelRow>(
            """
            SELECT m.model_id, m.provider_id, m.model_key, m.name, m.enabled, m.type, m.category,
                   m.icon, m.context_length, m.max_output_tokens, m.supports_vision,
                   m.supports_function_call, m.supports_thinking, m.sort_order, m.created_at
            FROM ai_model m
            JOIN ai_provider p ON p.provider_id = m.provider_id
            WHERE m.model_id = @ModelId AND p.device_id IS NULL
            """,
            new ModelIdParam { ModelId = modelId.Value });

        return row?.ToDomain();
    }

    /// <summary>仅删除归属于全局渠道的指定模型，返回是否实际删除。</summary>
    public async Task<bool> DeleteModelAsync(
        ModelId modelId,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        // 全局作用域检查使用子查询而非联接：Postgres 的 DELETE 不使用 USING 时不接受 JOIN。
        var affected = await connection.ExecuteAsync(
            """
            DELETE FROM ai_model
            WHERE model_id = @ModelId
              AND provider_id IN (SELECT provider_id FROM ai_provider WHERE device_id IS NULL)
            """,
            new ModelIdParam { ModelId = modelId.Value });

        return affected > 0;
    }

    /// <summary>批量设置指定渠道下全部模型的启用状态，不修改其他模型配置。</summary>
    public async Task SetModelsEnabledAsync(
        ProviderId providerId,
        bool enabled,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        await connection.ExecuteAsync(
            "UPDATE ai_model SET enabled = @Enabled WHERE provider_id = @ProviderId",
            new SetModelsEnabledParams { ProviderId = providerId.Value, Enabled = enabled });
    }
}

// --- 参数类型（静态结构；Dapper.AOT 拒绝动态或匿名参数）---

internal sealed class DeviceIdParam
{
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; }
}

internal sealed class ProviderIdParam
{
    /// <summary>关联的 AI 渠道标识。</summary>
    public Guid ProviderId { get; init; }
}

internal sealed class ProviderScopeParam
{
    /// <summary>关联的 AI 渠道标识。</summary>
    public Guid ProviderId { get; init; }
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; }
}

internal sealed class ProviderNameParam
{
    /// <summary>实体或配置项的名称。</summary>
    public string Name { get; init; } = string.Empty;
}

internal sealed class ProviderBuiltinParam
{
    /// <summary>内置渠道预设的稳定标识，非内置项可为空。</summary>
    public string BuiltinId { get; init; } = string.Empty;
}

internal sealed class ModelIdParam
{
    /// <summary>本地模型配置标识。</summary>
    public Guid ModelId { get; init; }
}

internal sealed class ModelScopeParam
{
    /// <summary>本地模型配置标识。</summary>
    public Guid ModelId { get; init; }
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; }
}

internal sealed class SetModelsEnabledParams
{
    /// <summary>关联的 AI 渠道标识。</summary>
    public Guid ProviderId { get; init; }
    /// <summary>当前配置或资源是否启用。</summary>
    public bool Enabled { get; init; }
}

internal sealed class InsertProviderParams
{
    /// <summary>关联的 AI 渠道标识。</summary>
    public Guid ProviderId { get; init; }
    /// <summary>内置渠道预设的稳定标识，非内置项可为空。</summary>
    public string? BuiltinId { get; init; }
    /// <summary>实体或配置项的名称。</summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>面向用户或模型展示的说明文本。</summary>
    public string? Description { get; init; }
    /// <summary>显示用图标标识或地址。</summary>
    public string? Icon { get; init; }
    /// <summary>该数据项的协议、值或事件类型，沿用所属契约的取值。</summary>
    public string Type { get; init; } = string.Empty;
    /// <summary>渠道 API Key 的加密密文。</summary>
    public byte[]? ApiKeyCipher { get; init; }
    /// <summary>渠道 API Key 密文配套的随机数。</summary>
    public byte[]? ApiKeyNonce { get; init; }
    /// <summary>渠道 API Key 密文配套的认证标签。</summary>
    public byte[]? ApiKeyTag { get; init; }
    /// <summary>可公开展示的密钥掩码。</summary>
    public string? ApiKeyHint { get; init; }
    /// <summary>调用方配置并规范化后的上游基础地址。</summary>
    public string BaseUrl { get; init; } = string.Empty;
    /// <summary>当前配置或资源是否启用。</summary>
    public bool Enabled { get; init; }
    /// <summary>当前内置渠道预设的版本号。</summary>
    public int? PresetVersion { get; init; }
    /// <summary>界面显示及列表查询使用的排序值。</summary>
    public int SortOrder { get; init; }
    /// <summary>记录创建时间，沿用当前 UTC 与序列化约定。</summary>
    public DateTimeOffset CreatedAt { get; init; }
    /// <summary>记录最近一次修改的 UTC 时间。</summary>
    public DateTimeOffset UpdatedAt { get; init; }
}

internal sealed class UpdateProviderParams
{
    /// <summary>关联的 AI 渠道标识。</summary>
    public Guid ProviderId { get; init; }
    /// <summary>内置渠道预设的稳定标识，非内置项可为空。</summary>
    public string? BuiltinId { get; init; }
    /// <summary>实体或配置项的名称。</summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>面向用户或模型展示的说明文本。</summary>
    public string? Description { get; init; }
    /// <summary>显示用图标标识或地址。</summary>
    public string? Icon { get; init; }
    /// <summary>该数据项的协议、值或事件类型，沿用所属契约的取值。</summary>
    public string Type { get; init; } = string.Empty;
    /// <summary>调用方配置并规范化后的上游基础地址。</summary>
    public string BaseUrl { get; init; } = string.Empty;
    /// <summary>当前配置或资源是否启用。</summary>
    public bool Enabled { get; init; }
    /// <summary>当前内置渠道预设的版本号。</summary>
    public int? PresetVersion { get; init; }
    /// <summary>界面显示及列表查询使用的排序值。</summary>
    public int SortOrder { get; init; }
    /// <summary>记录最近一次修改的 UTC 时间。</summary>
    public DateTimeOffset UpdatedAt { get; init; }
    /// <summary>渠道 API Key 的加密密文。</summary>
    public byte[]? ApiKeyCipher { get; init; }
    /// <summary>渠道 API Key 密文配套的随机数。</summary>
    public byte[]? ApiKeyNonce { get; init; }
    /// <summary>渠道 API Key 密文配套的认证标签。</summary>
    public byte[]? ApiKeyTag { get; init; }
    /// <summary>可公开展示的密钥掩码。</summary>
    public string? ApiKeyHint { get; init; }
}

internal sealed class InsertModelParams
{
    /// <summary>本地模型配置标识。</summary>
    public Guid ModelId { get; init; }
    /// <summary>关联的 AI 渠道标识。</summary>
    public Guid ProviderId { get; init; }
    /// <summary>上游识别的模型名称，不等同于本地模型配置标识。</summary>
    public string ModelKey { get; init; } = string.Empty;
    /// <summary>实体或配置项的名称。</summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>当前配置或资源是否启用。</summary>
    public bool Enabled { get; init; }
    /// <summary>该数据项的协议、值或事件类型，沿用所属契约的取值。</summary>
    public string? Type { get; init; }
    /// <summary>所属的模型、模板或内容分类。</summary>
    public string Category { get; init; } = string.Empty;
    /// <summary>显示用图标标识或地址。</summary>
    public string? Icon { get; init; }
    /// <summary>模型声明的上下文 token 容量。</summary>
    public int? ContextLength { get; init; }
    /// <summary>模型调用允许的最大输出 token 数量。</summary>
    public int? MaxOutputTokens { get; init; }
    /// <summary>模型是否声明支持图片输入。</summary>
    public bool SupportsVision { get; init; }
    /// <summary>模型是否声明支持工具/函数调用。</summary>
    public bool SupportsFunctionCall { get; init; }
    /// <summary>模型是否声明支持思考内容。</summary>
    public bool SupportsThinking { get; init; }
    /// <summary>界面显示及列表查询使用的排序值。</summary>
    public int SortOrder { get; init; }
    /// <summary>记录创建时间，沿用当前 UTC 与序列化约定。</summary>
    public DateTimeOffset CreatedAt { get; init; }
}

internal sealed class UpdateModelParams
{
    /// <summary>本地模型配置标识。</summary>
    public Guid ModelId { get; init; }
    /// <summary>上游识别的模型名称，不等同于本地模型配置标识。</summary>
    public string ModelKey { get; init; } = string.Empty;
    /// <summary>实体或配置项的名称。</summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>当前配置或资源是否启用。</summary>
    public bool Enabled { get; init; }
    /// <summary>该数据项的协议、值或事件类型，沿用所属契约的取值。</summary>
    public string? Type { get; init; }
    /// <summary>所属的模型、模板或内容分类。</summary>
    public string Category { get; init; } = string.Empty;
    /// <summary>显示用图标标识或地址。</summary>
    public string? Icon { get; init; }
    /// <summary>模型声明的上下文 token 容量。</summary>
    public int? ContextLength { get; init; }
    /// <summary>模型调用允许的最大输出 token 数量。</summary>
    public int? MaxOutputTokens { get; init; }
    /// <summary>模型是否声明支持图片输入。</summary>
    public bool SupportsVision { get; init; }
    /// <summary>模型是否声明支持工具/函数调用。</summary>
    public bool SupportsFunctionCall { get; init; }
    /// <summary>模型是否声明支持思考内容。</summary>
    public bool SupportsThinking { get; init; }
    /// <summary>界面显示及列表查询使用的排序值。</summary>
    public int SortOrder { get; init; }
}

// --- 数据行类型 ---

internal sealed class ProviderRow
{
    /// <summary>关联的 AI 渠道标识。</summary>
    public Guid ProviderId { get; init; }
    /// <summary>内置渠道预设的稳定标识，非内置项可为空。</summary>
    public string? BuiltinId { get; init; }
    /// <summary>实体或配置项的名称。</summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>面向用户或模型展示的说明文本。</summary>
    public string? Description { get; init; }
    /// <summary>显示用图标标识或地址。</summary>
    public string? Icon { get; init; }
    /// <summary>该数据项的协议、值或事件类型，沿用所属契约的取值。</summary>
    public string Type { get; init; } = string.Empty;
    /// <summary>渠道 API Key 的加密密文。</summary>
    public byte[]? ApiKeyCipher { get; init; }
    /// <summary>渠道 API Key 密文配套的随机数。</summary>
    public byte[]? ApiKeyNonce { get; init; }
    /// <summary>渠道 API Key 密文配套的认证标签。</summary>
    public byte[]? ApiKeyTag { get; init; }
    /// <summary>可公开展示的密钥掩码。</summary>
    public string? ApiKeyHint { get; init; }
    /// <summary>调用方配置并规范化后的上游基础地址。</summary>
    public string BaseUrl { get; init; } = string.Empty;
    /// <summary>当前配置或资源是否启用。</summary>
    public bool Enabled { get; init; }
    /// <summary>当前内置渠道预设的版本号。</summary>
    public int? PresetVersion { get; init; }
    /// <summary>界面显示及列表查询使用的排序值。</summary>
    public int SortOrder { get; init; }

    // Npgsql 将 timestamptz 实例化为 UTC DateTime；若在*读取*行上声明 DateTimeOffset，
    // Dapper 会尝试不存在的 Convert.ChangeType 转换，并在运行时而非构建时抛错。
    // 这与 DeviceRow 的陷阱相同，参见 DeviceRepository。参数类型不受影响，写入方向可正常转换。
    /// <summary>记录创建时间，沿用当前 UTC 与序列化约定。</summary>
    public DateTime CreatedAt { get; init; }
    /// <summary>记录最近一次修改的 UTC 时间。</summary>
    public DateTime UpdatedAt { get; init; }

    /// <summary>把数据库静态行映射为业务记录，保留既有状态和时间语义。</summary>
    public AiProvider ToDomain()
    {
        // CHECK 约束保证已存储值可以解析；若某行仍解析失败，说明架构与此枚举已产生偏差，
        // 应明确失败而不是回退到默认值。
        if (!AiEnumNames.TryParseProviderType(Type, out var type))
        {
            throw new InvalidOperationException(
                $"Provider {ProviderId} has unrecognised type '{Type}'.");
        }

        var secret = ApiKeyCipher is not null && ApiKeyNonce is not null && ApiKeyTag is not null
            ? new ApiKeySecret(ApiKeyCipher, ApiKeyNonce, ApiKeyTag, ApiKeyHint ?? "••••")
            : null;

        return new AiProvider(
            new ProviderId(ProviderId),
            BuiltinId,
            Name,
            Description,
            Icon,
            type,
            secret,
            BaseUrl,
            Enabled,
            PresetVersion,
            SortOrder,
            new DateTimeOffset(DateTime.SpecifyKind(CreatedAt, DateTimeKind.Utc)),
            new DateTimeOffset(DateTime.SpecifyKind(UpdatedAt, DateTimeKind.Utc)));
    }
}

internal sealed class ModelRow
{
    /// <summary>本地模型配置标识。</summary>
    public Guid ModelId { get; init; }
    /// <summary>关联的 AI 渠道标识。</summary>
    public Guid ProviderId { get; init; }
    /// <summary>上游识别的模型名称，不等同于本地模型配置标识。</summary>
    public string ModelKey { get; init; } = string.Empty;
    /// <summary>实体或配置项的名称。</summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>当前配置或资源是否启用。</summary>
    public bool Enabled { get; init; }
    /// <summary>该数据项的协议、值或事件类型，沿用所属契约的取值。</summary>
    public string? Type { get; init; }
    /// <summary>所属的模型、模板或内容分类。</summary>
    public string Category { get; init; } = string.Empty;
    /// <summary>显示用图标标识或地址。</summary>
    public string? Icon { get; init; }
    /// <summary>模型声明的上下文 token 容量。</summary>
    public int? ContextLength { get; init; }
    /// <summary>模型调用允许的最大输出 token 数量。</summary>
    public int? MaxOutputTokens { get; init; }
    /// <summary>模型是否声明支持图片输入。</summary>
    public bool SupportsVision { get; init; }
    /// <summary>模型是否声明支持工具/函数调用。</summary>
    public bool SupportsFunctionCall { get; init; }
    /// <summary>模型是否声明支持思考内容。</summary>
    public bool SupportsThinking { get; init; }
    /// <summary>界面显示及列表查询使用的排序值。</summary>
    public int SortOrder { get; init; }

    // timestamptz 读取为 DateTime，参见 ProviderRow 上的说明。
    /// <summary>记录创建时间，沿用当前 UTC 与序列化约定。</summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>把数据库静态行映射为业务记录，保留既有状态和时间语义。</summary>
    public AiModel ToDomain()
    {
        // 数据库中的未知协议或类别应明确失败；读取时间按原有 UTC 约定还原，不能依赖进程本地时区。
        ProviderType? type = null;
        if (Type is not null)
        {
            if (!AiEnumNames.TryParseProviderType(Type, out var parsed))
            {
                throw new InvalidOperationException(
                    $"Model {ModelId} has unrecognised type override '{Type}'.");
            }

            type = parsed;
        }

        if (!AiEnumNames.TryParseModelCategory(Category, out var category))
        {
            throw new InvalidOperationException(
                $"Model {ModelId} has unrecognised category '{Category}'.");
        }

        return new AiModel(
            new ModelId(ModelId),
            new ProviderId(ProviderId),
            ModelKey,
            Name,
            Enabled,
            type,
            category,
            Icon,
            ContextLength,
            MaxOutputTokens,
            SupportsVision,
            SupportsFunctionCall,
            SupportsThinking,
            SortOrder,
            new DateTimeOffset(DateTime.SpecifyKind(CreatedAt, DateTimeKind.Utc)));
    }
}
