using System.Text.Json.Nodes;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;

namespace CreativeFlow.Application.Abstractions.Persistence;

/// <summary>公共媒体应用的不可变发布快照；仅服务端可以读取图和内部绑定。</summary>
/// <param name="Id">发布版本标识。</param>
/// <param name="WorkflowId">来源媒体工作流。</param>
/// <param name="Version">同一应用内递增的版本号。</param>
/// <param name="Metadata">展示信息和固定素材共享授权。</param>
/// <param name="GraphJson">仅供服务端执行的冻结图。</param>
/// <param name="InputSchemaJson">含内部绑定的参数契约。</param>
/// <param name="OutputSchemaJson">声明的图片或视频输出。</param>
/// <param name="MaxConcurrency">该应用同时执行上限。</param>
/// <param name="UserConcurrency">每个客户在该应用的执行上限。</param>
/// <param name="DailyLimit">每个客户每日 UTC 提交额度。</param>
/// <param name="CreatedAt">发布时间。</param>
public sealed record CreativeAppRelease(Guid Id, Guid WorkflowId, int Version, JsonObject Metadata,
    string GraphJson, string InputSchemaJson, string OutputSchemaJson, int MaxConcurrency,
    int UserConcurrency, int DailyLimit, DateTimeOffset CreatedAt);

/// <summary>公共应用运行与客户身份的持久关联。</summary>
/// <param name="Run">持久运行及调用客户设备。</param>
/// <param name="Release">该运行固定使用的发布版本。</param>
public sealed record CreativeAppRun(WorkflowRun Run, CreativeAppRelease Release);

/// <summary>在同一事务内检查版本、额度和幂等键后的受理结果。</summary>
/// <param name="Status">accepted、replayed 或明确的拒绝原因。</param>
/// <param name="RunId">新建或找到的任务标识，拒绝时可为空。</param>
public sealed record CreativeAppAdmission(string Status, WorkflowRunId? RunId);

public interface ICreativeAppRepository
{
    /// <summary>只读取上架版本的可用封面，调用者不因此获得发布者其他素材的权限。</summary>
    Task<CanvasAsset?> FindCoverAsync(Guid workflowId, CancellationToken ct);
    /// <summary>分页读取当前上架应用，返回已脱敏的表单与展示信息。</summary>
    Task<JsonArray> ListAsync(string? search, string? kind, int offset, CancellationToken ct);
    /// <summary>读取指定发布版本；未提供版本时只返回当前上架版本。</summary>
    Task<CreativeAppRelease?> FindAsync(Guid workflowId, Guid? releaseId, CancellationToken ct);
    /// <summary>读取管理员可管理的发布历史及当前上架版本。</summary>
    Task<JsonObject> ManagementAsync(Guid workflowId, CancellationToken ct);
    /// <summary>保存不可变版本并切换上架指针，同时记录管理员发布审计。</summary>
    Task<CreativeAppRelease> PublishAsync(CreativeAppRelease release, Guid actor, CancellationToken ct);
    /// <summary>切换有效发布版本或下架，不修改已受理任务的执行快照。</summary>
    Task<bool> ActivateAsync(Guid workflowId, Guid? releaseId, Guid actor, CancellationToken ct);
    /// <summary>按客户和请求键原子受理任务，执行版本、素材、额度与幂等校验。</summary>
    Task<CreativeAppAdmission> EnqueueAsync(CreativeAppRelease release, AccountId account, DeviceId device,
        string key, string hash, string graphJson, JsonObject inputs, CancellationToken ct);
    /// <summary>只在指定客户账户内查找运行及其固定发布版本。</summary>
    Task<CreativeAppRun?> FindRunAsync(AccountId account, Guid runId, CancellationToken ct);
    /// <summary>按客户、应用和请求键找回已受理任务，不产生新的模型调用。</summary>
    Task<Guid?> FindRequestAsync(AccountId account, Guid workflowId, string key, CancellationToken ct);
    /// <summary>分页读取指定账户的生成历史，可选限定应用。</summary>
    Task<JsonArray> ListRunsAsync(AccountId account, Guid? workflowId, int offset, CancellationToken ct);
    /// <summary>分页检索客户自己的已保存成品，仅返回发布契约声明的输出，包含下架应用的历史结果。</summary>
    Task<JsonObject> ListOutputsAsync(AccountId account, Guid? workflowId, string? search, string? kind,
        DateTimeOffset? since, int offset, CancellationToken ct);
    /// <summary>仅取消客户自己的活动任务，终态任务保持原结果。</summary>
    Task<bool> CancelAsync(AccountId account, Guid runId, CancellationToken ct);
}
