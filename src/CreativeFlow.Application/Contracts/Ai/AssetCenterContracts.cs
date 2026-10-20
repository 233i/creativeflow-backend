namespace CreativeFlow.Application.Contracts.Ai;

/// <summary>资产列表的受限筛选条件；排序值只能使用服务端枚举。</summary>
public sealed record AssetQuery(string? Search = null, string? Kind = null, string? Source = null,
    Guid? AccountId = null, Guid? GroupId = null, bool Ungrouped = false, string? Tag = null,
    string State = "active", string? Usage = null, DateTimeOffset? From = null, DateTimeOffset? To = null,
    long? MinBytes = null, string Sort = "created_desc", int Offset = 0, int Limit = 30, bool Public = false);

/// <summary>列表只返回管理必需的元数据，不暴露文件键、供应商 URL 或密钥。</summary>
public sealed record AssetItem(string Id, string Name, string Kind, string MediaType, long ByteSize,
    string Source, string State, string? AccountId, string Owner, string? GroupId, string? GroupName,
    string[] Tags, DateTimeOffset CreatedAt, DateTimeOffset? DeletedAt, DateTimeOffset? PurgedAt,
    bool IsPublic, int ReferenceCount, int? Width, int? Height, double? Duration, string Revision,
    string Url, string ThumbnailUrl, bool CanManage);

/// <summary>稳定排序的资产分页结果。</summary>
public sealed record AssetPage(AssetItem[] Items, long Total, int Offset, int Limit);
/// <summary>筛选项带稳定标识，不以用户重名或文件夹重名合并权限域。</summary>
public sealed record AssetFacet(string Id, string Name, long Count);
public sealed record AssetFacets(AssetFacet[] Groups, AssetFacet[] Owners, string[] Tags);

/// <summary>一处持久化引用；只包含定位元数据，不返回运行输入正文。</summary>
public sealed record AssetReference(string Kind, string Id, string Slot, string Name, string Protection,
    string? WorkflowId, string? RunId, string? NodeId);

/// <summary>历史可准确还原的生成来源；缺失的模型和参数保持空值。</summary>
public sealed record AssetOrigin(string Kind, string Id, string? WorkflowId, string? NodeId,
    string? Model, string? Provider, string? ParametersJson, string? SourceAssetId);

/// <summary>资产管理审计不存储文件正文或秘密信息。</summary>
public sealed record AssetAudit(string Action, string Actor, string Reason, DateTimeOffset CreatedAt);

/// <summary>资产详情包含原提示词、使用位置、来源及最近操作，正文仍单独鉴权读取。</summary>
public sealed record AssetDetail(AssetItem Asset, string? Prompt, string? Sha256, AssetReference[] References,
    AssetOrigin[] Origins, AssetAudit[] Audit, bool ReferencesComplete);

/// <summary>修改展示元数据时使用行版本，避免覆盖并发修改。</summary>
public sealed record UpdateAssetRequest(string Name, string[] Tags, string Revision, string Reason = "整理资产");

/// <summary>回收、恢复、公开和永久清理共用显式确认请求；历史清理必须额外选择。</summary>
public sealed record AssetActionRequest(string Revision, string Reason, string? ConfirmName = null, bool AllowHistory = false);

/// <summary>删除预览不能替代最终执行时的锁内检查。</summary>
public sealed record AssetDeletionPreview(AssetItem Asset, AssetReference[] References, bool CanRecycle,
    bool CanPurge, bool CanPurgeHistory, DateTimeOffset? EligibleAt, string[] Blockers, int RetentionDays);

/// <summary>逐项操作结果，可准确表达部分成功而不自动重复危险请求。</summary>
public sealed record AssetActionResult(string Id, string Status, string Message, long LogicalBytes = 0);

/// <summary>分组不跨越账户/设备归属；同名目录按各资产所属设备复用。</summary>
public sealed record AssetBatchRequest(string Action, string[] Ids, string Reason, string? GroupName = null,
    string[]? Tags = null, Dictionary<string, string>? Revisions = null);

/// <summary>公共素材复用只复制逻辑记录，字节保持共享，访问权仍属于接收者。</summary>
public sealed record ReusePublicAssetRequest(string Revision);
/// <summary>批量打包只接受明确选择的资产，不允许服务器路径或任意下载 URL。</summary>
public sealed record DownloadAssetsRequest(string[] Ids);

/// <summary>容量统计项区分逻辑资产与真实存储键，避免重复计算共享内容。</summary>
public sealed record AssetStorageBucket(string Key, long Count, long Bytes);

/// <summary>存储策略仅用于预警和人工清理资格；不隐式启动删除任务。</summary>
public sealed record AssetStoragePolicy(int RetentionDays = 7, long WarningBytes = 10737418240,
    long AccountWarningBytes = 2147483648);

/// <summary>存储总览返回可清理候选容量，而不是声称操作系统已经回收空间。</summary>
public sealed record AssetStorageReport(long ActiveBytes, long RecycleBytes, long StoredBytes, long PreviewBytes,
    long PurgeableBytes, long PurgedBytes, AssetStorageBucket[] ByKind, AssetStorageBucket[] ByOwner,
    AssetStorageBucket[] BySource, AssetStoragePolicy Policy, bool Warning);

/// <summary>资产字节访问前已解析出的受控存储信息，只在后端端口之间传递。</summary>
public sealed record AssetContent(Guid Id, Guid DeviceId, string Name, string MediaType, string StoragePath,
    long ByteSize, string State);
