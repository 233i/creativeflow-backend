using System.Text.Json.Nodes;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Application.Abstractions.Persistence;

/// <summary>固定 HTTP 凭据版本与显式资源许可；工作流只引用不透明 ID，不覆盖连接定义。</summary>
public interface IHttpCredentialRepository
{
    /// <summary>读取有界凭据列表，不包含定义、请求头或密钥。</summary>
    Task<JsonArray> ListAsync(CancellationToken ct);
    /// <summary>已迁移的受保护查询路径不能通过移除凭据引用变成匿名 HTTP 调用。</summary>
    Task<bool> IsRestrictedEndpointAsync(string endpoint, CancellationToken ct);
    /// <summary>读取管理定义和版本列表，不包含密钥明文或密文。</summary>
    Task<JsonObject?> DetailsAsync(Guid id, CancellationToken ct);
    /// <summary>读取指定凭据下不可变版本，仅供服务器构造请求和版本核验。</summary>
    Task<HttpCredentialRevision?> FindRevisionAsync(Guid operation, Guid revision, CancellationToken ct);
    /// <summary>新建或更新凭据时原子追加版本；旧配置不会被当前草稿覆盖。</summary>
    Task<Guid> SaveAsync(Guid operation, string name, string description, string definition, string digest, ApiKeySecret? secret, Guid admin, CancellationToken ct);
    /// <summary>即时停用或恢复，不删除被运行或发布版本引用的记录。</summary>
    Task<bool> SetEnabledAsync(Guid operation, bool enabled, CancellationToken ct);
    /// <summary>复验管理员，或启用账号及设备，或未撤销未过期的 API Key 与明确凭据许可。</summary>
    Task<bool> CanUseAsync(Guid operation, KnowledgeSubject subject, CancellationToken ct);
    /// <summary>管理员查看现有许可和最近审计，不包含 API Key 值。</summary>
    Task<JsonObject> GrantsAsync(Guid operation, CancellationToken ct);
    /// <summary>显式资源授权与原因审计原子提交，重复同状态不重复写审计。</summary>
    Task SetGrantAsync(Guid operation, string kind, Guid subject, Guid admin, bool enabled, string reason, CancellationToken ct);
}

/// <summary>服务端使用的固定凭据版本，禁止直接序列化到普通用户或外部 API 响应。</summary>
/// <param name="CredentialId">所属凭据。</param>
/// <param name="Id">不可变版本。</param>
/// <param name="DefinitionJson">有界、已验证的连接和请求映射，不含专用鉴权密钥。</param>
/// <param name="Digest">定义摘要。</param>
/// <param name="Secret">此固定版本使用的受保护凭据，仅网络适配器短暂解密。</param>
public sealed record HttpCredentialRevision(Guid CredentialId, Guid Id, string DefinitionJson, string Digest, ApiKeySecret? Secret);
