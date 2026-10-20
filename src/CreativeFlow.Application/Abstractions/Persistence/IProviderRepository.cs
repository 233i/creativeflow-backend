using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Application.Abstractions.Persistence;

/// <summary>
/// 应用级供应商和模型存储。仓储刻意只读取和修改 device_id 为空的记录；
/// 管理员授权在 API 边界执行。
/// </summary>
public interface IProviderRepository
{
    /// <summary>按显示顺序列出全局供应商及其模型。</summary>
    Task<IReadOnlyList<AiProvider>> ListAsync(CancellationToken ct);

    /// <summary>
    /// 加载一个全局供应商及其模型。
    /// </summary>
    Task<AiProvider?> FindAsync(ProviderId providerId, CancellationToken ct);

    /// <summary>按唯一名称查找全局供应商。</summary>
    Task<AiProvider?> FindByNameAsync(string name, CancellationToken ct);

    /// <summary>按稳定的预设标识查找全局内置供应商。</summary>
    Task<AiProvider?> FindByBuiltinIdAsync(string builtinId, CancellationToken ct);

    /// <summary>按原有存储规则写入AI 渠道。</summary>
    Task InsertAsync(AiProvider provider, CancellationToken ct);

    /// <summary>
    /// 更新供应商的可变字段。
    /// </summary>
    /// <remarks>
    /// <paramref name="apiKey"/> 有三种状态：秘密值替换已存储密钥，
    /// <see cref="ApiKeyUpdate.Unchanged"/> 保持不变，<see cref="ApiKeyUpdate.Cleared"/> 将其移除。
    /// 普通可空值无法表达“保持不变”，而这正是用户编辑基础 URL 时的常见情况；处理错误会悄然清除密钥。
    /// </remarks>
    Task UpdateAsync(AiProvider provider, ApiKeyUpdate apiKey, CancellationToken ct);

    /// <summary>删除AI 渠道，保留不存在或不可访问时的返回语义。</summary>
    Task<bool> DeleteAsync(ProviderId providerId, CancellationToken ct);

    /// <summary>获取新渠道应使用的下一个排序值。</summary>
    Task<int> NextSortOrderAsync(CancellationToken ct);

    // --- 模型 ---

    /// <summary>保存一个全局渠道模型。</summary>
    Task InsertModelAsync(AiModel model, CancellationToken ct);

    /// <summary>更新指定模型的能力与参数配置。</summary>
    Task UpdateModelAsync(AiModel model, CancellationToken ct);

    /// <summary>删除模型，并验证它属于该设备拥有的供应商。</summary>
    Task<bool> DeleteModelAsync(ModelId modelId, CancellationToken ct);

    /// <summary>按本地模型标识读取模型配置。</summary>
    Task<AiModel?> FindModelAsync(ModelId modelId, CancellationToken ct);

    /// <summary>
    /// 按 <c>model_key</c> 添加供应商中尚不存在的模型。用于“获取模型”和 URL 导入；
    /// 重复执行时不得创建重复记录，也不得覆盖用户对单个模型的编辑。
    /// </summary>
    Task<int> InsertModelsIfAbsentAsync(IReadOnlyList<AiModel> models, CancellationToken ct);

    /// <summary>批量修改指定渠道下模型的启用状态。</summary>
    Task SetModelsEnabledAsync(ProviderId providerId, bool enabled, CancellationToken ct);
}

/// <summary>更新操作应如何处理已存储的 API 密钥。</summary>
public readonly record struct ApiKeyUpdate
{
    /// <summary>初始化 ApiKeyUpdate 的依赖与实例状态。</summary>
    private ApiKeyUpdate(ApiKeySecret? secret, bool clear)
    {
        Secret = secret;
        Clear = clear;
    }

    /// <summary>用于替换的加密密钥内容；未提供时按更新标记处理。</summary>
    public ApiKeySecret? Secret { get; }

    /// <summary>本次更新是否明确清除已经保存的密钥。</summary>
    public bool Clear { get; }

    /// <summary>保持已存储内容不变。</summary>
    public static ApiKeyUpdate Unchanged => new(null, false);

    /// <summary>移除已存储密钥。</summary>
    public static ApiKeyUpdate Cleared => new(null, true);

    /// <summary>显式表示以新的加密秘密替换现有密钥。</summary>
    public static ApiKeyUpdate Replace(ApiKeySecret secret) => new(secret, false);
}
