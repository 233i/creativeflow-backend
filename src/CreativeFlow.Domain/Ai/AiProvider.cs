namespace CreativeFlow.Domain.Ai;

/// <param name="Value">底层 UUID 值，保持现有标识编码与持久化格式。</param>
public readonly record struct ProviderId(Guid Value)
{
    /// <summary>生成新的 UUIDv7 标识，保留当前持久化排序特性。</summary>
    public static ProviderId New() => new(Guid.CreateVersion7());

    /// <summary>解析非空 UUID 标识；无效或全零输入返回失败并清空输出。</summary>
    public static bool TryParse(string? text, out ProviderId id)
    {
        if (Guid.TryParse(text, out var guid) && guid != Guid.Empty)
        {
            id = new ProviderId(guid);
            return true;
        }

        id = default;
        return false;
    }

    /// <summary>以既有标准 UUID 字符串格式输出该标识。</summary>
    public override string ToString() => Value.ToString();
}

/// <param name="Value">底层 UUID 值，保持现有标识编码与持久化格式。</param>
public readonly record struct ModelId(Guid Value)
{
    /// <summary>生成新的 UUIDv7 标识，保留当前持久化排序特性。</summary>
    public static ModelId New() => new(Guid.CreateVersion7());

    /// <summary>解析非空 UUID 标识；无效或全零输入返回失败并清空输出。</summary>
    public static bool TryParse(string? text, out ModelId id)
    {
        if (Guid.TryParse(text, out var guid) && guid != Guid.Empty)
        {
            id = new ModelId(guid);
            return true;
        }

        id = default;
        return false;
    }

    /// <summary>以既有标准 UUID 字符串格式输出该标识。</summary>
    public override string ToString() => Value.ToString();
}

/// <summary>
/// 加密后的 API 密钥。采用 AES-GCM，因此密文本身携带完整性标签。
/// </summary>
/// <remarks>
/// 三个部分必须一同传递，因为拆开后都无法使用：解密需要随机数来派生密钥流，
/// 还需要标签来证明密文未被篡改。将它们放在同一个类型中，可以避免只持久化密文却遗漏随机数。
/// <para>
/// <see cref="Hint"/> 是唯一可以安全展示给用户的部分，也是唯一会离开服务器的部分。
/// </para>
/// </remarks>
/// <param name="Cipher">AES-GCM 加密后的密文。</param>
/// <param name="Nonce">与 AES-GCM 密文配套保存的随机数。</param>
/// <param name="Tag">用于验证 AES-GCM 密文完整性的认证标签。</param>
/// <param name="Hint">仅用于显示的安全提示或掩码。</param>
public sealed record ApiKeySecret(byte[] Cipher, byte[] Nonce, byte[] Tag, string Hint)
{
    /// <summary>
    /// 为密钥构建显示提示。最多展示末尾四个字符；如果密钥短到四个字符已占较大比例，
    /// 则将其完全遮盖。
    /// </summary>
    public static string BuildHint(string apiKey)
    {
        var trimmed = apiKey.Trim();
        return trimmed.Length <= 8 ? "••••" : $"••••{trimmed[^4..]}";
    }
}

/// <param name="Id">当前实体或契约项目的唯一标识。</param>
/// <param name="ProviderId">关联的 AI 渠道标识。</param>
/// <param name="ModelKey">上游识别的模型名称，不等同于本地模型配置标识。</param>
/// <param name="Name">实体或配置项的名称。</param>
/// <param name="Enabled">当前配置或资源是否启用。</param>
/// <param name="Type">模型协议覆盖，未指定时按渠道与模型分类推导。</param>
/// <param name="Category">所属的模型、模板或内容分类。</param>
/// <param name="Icon">显示用图标标识或地址。</param>
/// <param name="ContextLength">模型声明的上下文 token 容量。</param>
/// <param name="MaxOutputTokens">模型调用允许的最大输出 token 数量。</param>
/// <param name="SupportsVision">模型是否声明支持图片输入。</param>
/// <param name="SupportsFunctionCall">模型是否声明支持工具/函数调用。</param>
/// <param name="SupportsThinking">模型是否声明支持思考内容。</param>
/// <param name="SortOrder">界面显示及列表查询使用的排序值。</param>
/// <param name="CreatedAt">记录创建时间，沿用当前 UTC 与序列化约定。</param>
public sealed record AiModel(
    ModelId Id,
    ProviderId ProviderId,
    string ModelKey,
    string Name,
    bool Enabled,
    ProviderType? Type,
    ModelCategory Category,
    string? Icon,
    int? ContextLength,
    int? MaxOutputTokens,
    bool SupportsVision,
    bool SupportsFunctionCall,
    bool SupportsThinking,
    int SortOrder,
    DateTimeOffset CreatedAt);

/// <param name="Id">当前实体或契约项目的唯一标识。</param>
/// <param name="BuiltinId">内置渠道预设的稳定标识，非内置项可为空。</param>
/// <param name="Name">实体或配置项的名称。</param>
/// <param name="Description">面向用户或模型展示的说明文本。</param>
/// <param name="Icon">显示用图标标识或地址。</param>
/// <param name="Type">渠道默认协议；模型可显式覆盖，媒体类别按既有规则推导。</param>
/// <param name="ApiKey">渠道调用密钥；ApiKeySecret 类型为受保护的持久化表示，字符串类型仅用于请求或执行。</param>
/// <param name="BaseUrl">调用方配置并规范化后的上游基础地址。</param>
/// <param name="Enabled">当前配置或资源是否启用。</param>
/// <param name="PresetVersion">当前内置渠道预设的版本号。</param>
/// <param name="SortOrder">界面显示及列表查询使用的排序值。</param>
/// <param name="CreatedAt">记录创建时间，沿用当前 UTC 与序列化约定。</param>
/// <param name="UpdatedAt">记录最近一次修改的 UTC 时间。</param>
public sealed record AiProvider(
    ProviderId Id,
    string? BuiltinId,
    string Name,
    string? Description,
    string? Icon,
    ProviderType Type,
    ApiKeySecret? ApiKey,
    string BaseUrl,
    bool Enabled,
    int? PresetVersion,
    int SortOrder,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>当前渠道配置的模型集合。</summary>
    public IReadOnlyList<AiModel> Models { get; init; } = [];

    /// <summary>是否已配置可用的密钥记录。</summary>
    public bool HasApiKey => ApiKey is not null;

    /// <summary>
    /// 解析该供应商某个模型应使用的协议。
    /// </summary>
    /// <remarks>
    /// 模型上的显式覆盖始终优先。否则，如果供应商本身声明了与模型分类兼容的
    /// 媒体协议，模型会真正继承该协议。仅当供应商按聊天协议声明时，才使用分类默认值：
    /// 例如 <c>openai-chat</c> 供应商的图片路由通常使用 <c>openai-images</c>。
    /// <para>
    /// 前端在 <c>web/features/ai/types.ts</c> 中实现了相同规则。两端必须保持一致：
    /// 客户端用它决定渲染哪些参数控件，服务器用它决定分派给哪个客户端。
    /// </para>
    /// </remarks>
    public ProviderType ResolveProtocol(AiModel? model)
    {
        if (model is null)
        {
            return Type;
        }

        if (model.Type is { } explicitType)
        {
            return explicitType;
        }

        return model.Category switch
        {
            ModelCategory.Rerank => ProviderType.Rerank,
            ModelCategory.Image when Type is ProviderType.OpenAiImages or ProviderType.SeedreamImages or ProviderType.Gemini => Type,
            ModelCategory.Image => ProviderType.OpenAiImages,
            ModelCategory.Video when Type is ProviderType.OpenAiVideo
                or ProviderType.MiniMaxVideo
                or ProviderType.MiniMaxH3
                or ProviderType.SeedanceVideo
                or ProviderType.XaiVideo => Type,
            ModelCategory.Video => ProviderType.SeedanceVideo,
            _ => Type,
        };
    }

    /// <summary>
    /// 移除协议客户端会自行重新追加的后缀。
    /// </summary>
    /// <remarks>
    /// 用户通常会粘贴供应商文档展示的地址，而该地址一般是兼容 OpenAI 的 URL。
    /// 若不处理，Anthropic 基础 URL <c>https://api.anthropic.com/v1</c> 会变成
    /// <c>…/v1/v1/messages</c>。
    /// <para>
    /// 此约束在这里强制执行，而不只在浏览器中执行，因为该不变量应与追加路径的代码放在一起：
    /// API 可以被直接调用，从分享链接导入的配置也不应依赖发送方是否已完成规范化。
    /// </para>
    /// </remarks>
    public static string NormalizeBaseUrl(string baseUrl, ProviderType protocol)
    {
        var trimmed = baseUrl.Trim().TrimEnd('/');

        return protocol switch
        {
            // 客户端会追加 /v1/messages。
            ProviderType.Anthropic => StripSuffix(StripSuffix(trimmed, "/messages"), "/v1"),

            // 客户端会追加 /v1beta/models/...，兼容 OpenAI 的后缀会导致地址错误。
            ProviderType.Gemini => StripSuffix(trimmed, "/openai"),

            // 保留普通及套餐版本前缀，只移除用户粘贴的完整图片操作路径。
            ProviderType.SeedreamImages => StripSuffix(trimmed, "/images/generations"),

            // H3 原生客户端自行追加 /v2，兼容粘贴根地址、版本地址或完整提交地址。
            ProviderType.MiniMaxH3 => StripSuffix(
                StripSuffix(StripSuffix(trimmed, "/video_generation"), "/v2"), "/v1"),

            _ => trimmed,
        };
    }

    /// <summary>
    /// 验证并规范化外部客户端提供的供应商基础 URL。
    /// </summary>
    /// <remarks>
    /// 供应商 URL 由用户选择，因此对 Ollama 等运行时仍允许使用本地主机。
    /// 协议客户端会向该值追加路径，所以拒绝凭据、查询字符串和片段；绝不能接收可借助持久化
    /// 偷渡另一授权信息或秘密的 URL。
    /// </remarks>
    public static bool TryNormalizeBaseUrl(
        string? baseUrl,
        ProviderType protocol,
        out string normalized,
        out string error)
    {
        // 允许用户配置本地模型服务，但禁止 URL 携带凭据、查询与片段，避免协议追加路径后产生歧义。
        normalized = string.Empty;
        error = "baseUrl must be an absolute HTTP(S) URL";

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return false;
        }

        var trimmed = baseUrl.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            error = "baseUrl must not include credentials";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            error = "baseUrl must not include a query or fragment";
            return false;
        }

        normalized = NormalizeBaseUrl(trimmed, protocol);
        return true;
    }

    /// <summary>仅在末尾匹配时去掉已知协议后缀，保留其余基础地址。</summary>
    private static string StripSuffix(string value, string suffix) =>
        value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? value[..^suffix.Length].TrimEnd('/')
            : value;
}
