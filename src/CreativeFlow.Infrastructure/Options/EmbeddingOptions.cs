namespace CreativeFlow.Infrastructure.Options;

/// <summary>由部署者维护的新向量接口出站许可，不接受模型表单或工作流覆盖。</summary>
public sealed class EmbeddingOptions
{
    /// <summary>允许访问的完整 HTTP 端点，包括固定路径；默认不允许任何端点。</summary>
    public string[] AllowedEndpoints { get; init; } = [];
}
