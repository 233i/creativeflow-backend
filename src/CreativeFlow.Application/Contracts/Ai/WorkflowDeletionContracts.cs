namespace CreativeFlow.Application.Contracts.Ai;

/// <summary>删除影响的安全摘要；不包含图正文、素材内容、密钥或运行输入输出。</summary>
public sealed record WorkflowDeletionPreview(
    string Id, string Source, string Name, string Revision, DateTimeOffset? DeletedAt,
    long PublishedApis, long PublishedTemplates, long QueuedRuns, long RunningRuns,
    long HistoryRuns, long DeploymentVersions, long ApiKeys, long IndependentCopies, long PublicApps = 0)
{
    /// <summary>仅未删除、未发布且没有在途任务时允许确认删除。</summary>
    public bool CanDelete => DeletedAt is null && PublishedApis == 0 && PublishedTemplates == 0
        && PublicApps == 0 && QueuedRuns == 0 && RunningRuns == 0;
}

/// <summary>显式确认名称和预览时的行版本，防止误删以及预览后草稿被他人改写。</summary>
public sealed record DeleteWorkflowRequest(string? Name, string? Revision);

/// <summary>原子删除的结果；未找到、受阻和版本冲突均不修改资源。</summary>
public sealed record WorkflowDeletionResult(string Status, WorkflowDeletionPreview? Preview);
