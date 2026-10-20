using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Api.Serialization;

/// <summary>
/// 将领域实体映射为传输契约。
/// </summary>
/// <remarks>
/// 这是供应商转换为 JSON 的唯一位置，使“API 密钥绝不序列化”成为系统属性而非编码习惯。
/// <see cref="ProviderResponse"/> 没有可容纳密钥的字段，且此映射器只读取
/// <see cref="AiProvider.HasApiKey"/> 和显示提示。
/// </remarks>
internal static class AiContractMapper
{
    /// <summary>将业务记录映射为既有 HTTP 响应字段。</summary>
    public static ProviderResponse ToResponse(AiProvider provider) => new(
        provider.Id.ToString(),
        provider.BuiltinId,
        provider.Name,
        provider.Description,
        provider.Icon,
        provider.Type.ToWire(),
        provider.BaseUrl,
        provider.Enabled,
        provider.HasApiKey,
        provider.ApiKey?.Hint,
        provider.SortOrder,
        provider.PresetVersion,
        provider.CreatedAt.ToString("O"),
        provider.UpdatedAt.ToString("O"),
        [.. provider.Models.Select(ToResponse)]);

    /// <summary>将业务记录映射为既有 HTTP 响应字段。</summary>
    public static ModelResponse ToResponse(AiModel model) => new(
        model.Id.ToString(),
        model.ModelKey,
        model.Name,
        model.Enabled,
        model.Type?.ToWire(),
        model.Category.ToWire(),
        model.Icon,
        model.ContextLength,
        model.MaxOutputTokens,
        model.SupportsVision,
        model.SupportsFunctionCall,
        model.SupportsThinking,
        model.SortOrder);
}
