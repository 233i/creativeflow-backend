namespace CreativeFlow.Domain.Ai;

/// <summary>
/// 与模型通信所使用的传输协议。它描述请求如何组织，而不是由谁提供服务——多个供应商都可使用
/// <c>OpenAiChat</c>，一个聚合商也可同时通过三种协议提供模型。
/// </summary>
/// <remarks>
/// 以文本而非序号持久化（参见 <c>db/migrations/0003__ai_config.sql</c>），因此调整枚举顺序
/// 不会在无提示的情况下改变现有数据行的含义。
/// </remarks>
public enum ProviderType
{
    /// <summary>OpenAI Chat Completions 兼容协议。</summary>
    OpenAiChat,
    /// <summary>OpenAI Responses 协议。</summary>
    OpenAiResponses,
    /// <summary>Anthropic Messages 协议。</summary>
    Anthropic,
    /// <summary>Gemini 模型协议。</summary>
    Gemini,
    /// <summary>OpenAI 图片生成与编辑协议。</summary>
    OpenAiImages,
    /// <summary>火山 Seedream JSON 图片生成及参考图协议。</summary>
    SeedreamImages,
    /// <summary>OpenAI 兼容视频任务协议。</summary>
    OpenAiVideo,
    /// <summary>MiniMax 视频协议。</summary>
    MiniMaxVideo,
    /// <summary>Seedance 视频任务协议。</summary>
    SeedanceVideo,
    /// <summary>xAI 视频生成与续接协议。</summary>
    XaiVideo,
    /// <summary>MiniMax H3 官方 V2 视频协议。</summary>
    MiniMaxH3,
    /// <summary>OpenAI 兼容向量化接口，不复用聊天请求格式。</summary>
    OpenAiEmbeddings,
    /// <summary>Cohere/Jina 兼容的 query/documents 重排序协议，返回候选索引和相关性分数。</summary>
    Rerank,
}

/// <summary>模型的用途，用于决定哪些节点类型可以选择它。</summary>
public enum ModelCategory
{
    /// <summary>用于对话或文本生成的模型类别。</summary>
    Chat,
    /// <summary>图片数据、素材或模型类别。</summary>
    Image,
    /// <summary>视频数据、素材或模型类别。</summary>
    Video,
    /// <summary>用于向量表示的模型类别。</summary>
    Embedding,
    /// <summary>对已召回的文档候选进行相关性重排序。</summary>
    Rerank,
    /// <summary>用于语音处理的模型类别。</summary>
    Speech,
}

public enum McpTransport
{
    /// <summary>MCP 标准输入输出传输配置。</summary>
    Stdio,
    /// <summary>旧版 MCP SSE 传输。</summary>
    Sse,
    /// <summary>MCP Streamable HTTP 传输。</summary>
    StreamableHttp,
}

/// <summary>
/// 在上述枚举与 Postgres 中存储的精确字符串之间转换。
/// </summary>
/// <remarks>
/// 这里手动实现而不使用 <see cref="System.Enum.Parse(System.Type, string)"/>：基于反射的枚举解析
/// 在 Native AOT 下存在裁剪风险，而且传输字符串采用短横线命名法，内置命名策略均无法生成。
/// 双向转换集中在一起，可避免新增协议时只修改一个方向。
/// </remarks>
public static class AiEnumNames
{
    /// <summary>将业务枚举映射为既有存储或接口字符串。</summary>
    public static string ToWire(this ProviderType value) => value switch
    {
        ProviderType.OpenAiChat => "openai-chat",
        ProviderType.OpenAiEmbeddings => "openai-embeddings",
        ProviderType.Rerank => "rerank",
        ProviderType.OpenAiResponses => "openai-responses",
        ProviderType.Anthropic => "anthropic",
        ProviderType.Gemini => "gemini",
        ProviderType.OpenAiImages => "openai-images",
        ProviderType.SeedreamImages => "seedream-images",
        ProviderType.OpenAiVideo => "openai-video",
        ProviderType.MiniMaxVideo => "minimax-video",
        ProviderType.MiniMaxH3 => "minimax-h3",
        ProviderType.SeedanceVideo => "seedance-video",
        ProviderType.XaiVideo => "xai-video",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown provider type."),
    };

    /// <summary>读取当前接受的上游协议字符串。</summary>
    public static bool TryParseProviderType(string? text, out ProviderType value)
    {
        switch (text)
        {
            case "openai-chat": value = ProviderType.OpenAiChat; return true;
            case "openai-embeddings": value = ProviderType.OpenAiEmbeddings; return true;
            case "rerank": value = ProviderType.Rerank; return true;
            case "openai-responses": value = ProviderType.OpenAiResponses; return true;
            case "anthropic": value = ProviderType.Anthropic; return true;
            case "gemini": value = ProviderType.Gemini; return true;
            case "openai-images": value = ProviderType.OpenAiImages; return true;
            case "seedream-images": value = ProviderType.SeedreamImages; return true;
            case "openai-video": value = ProviderType.OpenAiVideo; return true;
            case "minimax-video": value = ProviderType.MiniMaxVideo; return true;
            case "minimax-h3": value = ProviderType.MiniMaxH3; return true;
            case "seedance-video": value = ProviderType.SeedanceVideo; return true;
            case "xai-video": value = ProviderType.XaiVideo; return true;
            default: value = default; return false;
        }
    }

    /// <summary>将业务枚举映射为既有存储或接口字符串。</summary>
    public static string ToWire(this ModelCategory value) => value switch
    {
        ModelCategory.Chat => "chat",
        ModelCategory.Image => "image",
        ModelCategory.Video => "video",
        ModelCategory.Embedding => "embedding",
        ModelCategory.Rerank => "rerank",
        ModelCategory.Speech => "speech",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown model category."),
    };

    /// <summary>读取当前接受的模型分类字符串。</summary>
    public static bool TryParseModelCategory(string? text, out ModelCategory value)
    {
        switch (text)
        {
            case "chat": value = ModelCategory.Chat; return true;
            case "image": value = ModelCategory.Image; return true;
            case "video": value = ModelCategory.Video; return true;
            case "embedding": value = ModelCategory.Embedding; return true;
            case "rerank": value = ModelCategory.Rerank; return true;
            case "speech": value = ModelCategory.Speech; return true;
            default: value = default; return false;
        }
    }

    /// <summary>将业务枚举映射为既有存储或接口字符串。</summary>
    public static string ToWire(this McpTransport value) => value switch
    {
        McpTransport.Stdio => "stdio",
        McpTransport.Sse => "sse",
        McpTransport.StreamableHttp => "streamable-http",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown MCP transport."),
    };

    /// <summary>读取当前接受的 MCP 传输名称。</summary>
    public static bool TryParseMcpTransport(string? text, out McpTransport value)
    {
        switch (text)
        {
            case "stdio": value = McpTransport.Stdio; return true;
            case "sse": value = McpTransport.Sse; return true;
            case "streamable-http": value = McpTransport.StreamableHttp; return true;
            default: value = default; return false;
        }
    }
}
