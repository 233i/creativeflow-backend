using System.Text.Json.Nodes;

namespace CreativeFlow.Application.Abstractions.Persistence;

/// <summary>知识库独立授权，不继承设备匿名访问或旧外部 API Key 的工作流许可。</summary>
public interface IKnowledgeAccessRepository
{
    /// <summary>复验管理员存在，或已启用账号、设备当前归属及显式知识库授权。</summary>
    Task<bool> CanUseAsync(Guid library, KnowledgeSubject subject, CancellationToken ct);
    /// <summary>读取管理员可见的当前授权及最近一百次调整记录。</summary>
    Task<JsonObject> GrantsAsync(Guid library, CancellationToken ct);
    /// <summary>显式授权或撤权与原因审计在同一事务提交；重复相同设置不会重复记账。</summary>
    Task SetGrantAsync(Guid library, Guid account, Guid admin, bool enabled, string reason, CancellationToken ct);

    /// <summary>按 Key 记录标识单独授权知识访问并记录原因，绝不接收或存储 Key 明文。</summary>
    Task SetApiKeyGrantAsync(Guid library, Guid apiKey, Guid admin, bool enabled, string reason, CancellationToken ct);
}

/// <summary>仅服务端从已验证身份构造的知识使用主体，持久化后仍需每次复验。</summary>
/// <param name="Kind">account、admin 或 apiKey；外部 Key 的知识权限必须独立授权。</param>
/// <param name="Id">账户、独立管理员或 API Key 记录标识。</param>
/// <param name="Device">运行所属设备；账号必须仍拥有此设备。</param>
public sealed record KnowledgeSubject(string Kind, Guid Id, Guid Device);
