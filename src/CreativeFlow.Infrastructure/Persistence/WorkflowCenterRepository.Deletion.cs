using System.Data;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;
using CreativeFlow.Application.Contracts.Ai;

namespace CreativeFlow.Infrastructure.Persistence;

public sealed partial class WorkflowCenterRepository
{
    /// <summary>使用同一数据库快照计算预览；最终提交仍会持锁重新检查全部条件。</summary>
    public async Task<WorkflowDeletionPreview?> PreviewDeletionAsync(string source, Guid id, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var result = await ReadDeletionAsync(connection, transaction, source, id, false, ct);
        await transaction.CommitAsync(ct);
        return result;
    }

    /// <summary>与运行受理、发布共用资源行锁；保留图、所有历史记录和素材，仅写删除标记及审计。</summary>
    public async Task<WorkflowDeletionResult> DeleteAsync(string source, Guid id, Guid adminId, DeleteWorkflowRequest request, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var preview = await ReadDeletionAsync(connection, transaction, source, id, true, ct);
        if (preview is null) return new("not_found", null);
        // 重复提交（包括响应丢失后的重试）不再次写审计，也不恢复资源。
        if (preview.DeletedAt is not null) return new("deleted", preview);
        if (request.Name != preview.Name || request.Revision != preview.Revision) return new("changed", preview);
        if (!preview.CanDelete) return new("blocked", preview);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = source == "workflow"
            ? "UPDATE workflow SET deleted_at = now(), updated_at = now() WHERE workflow_id = @Id;"
            : "UPDATE workflow_template SET deleted_at = now(), updated_at = now() WHERE template_id = @Id;";
        command.CommandText += """
            INSERT INTO workflow_deletion_audit(source, resource_id, resource_name, actor_id, deleted_at, impact)
            VALUES (@Source, @Id, @Name, @AdminId, now(), @Impact);
            """;
        Parameter(command, "Id", NpgsqlDbType.Uuid, id);
        Parameter(command, "Source", NpgsqlDbType.Text, source);
        Parameter(command, "Name", NpgsqlDbType.Text, preview.Name);
        Parameter(command, "AdminId", NpgsqlDbType.Uuid, adminId);
        Parameter(command, "Impact", NpgsqlDbType.Jsonb, new JsonObject
        {
            ["historyRuns"] = preview.HistoryRuns, ["deploymentVersions"] = preview.DeploymentVersions,
            ["apiKeys"] = preview.ApiKeys, ["independentCopies"] = preview.IndependentCopies,
            ["queuedRuns"] = preview.QueuedRuns, ["runningRuns"] = preview.RunningRuns,
            ["publishedApis"] = preview.PublishedApis, ["publishedTemplates"] = preview.PublishedTemplates,
        }.ToJsonString());
        await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return new("deleted", preview);
    }

    /// <summary>先锁定真实来源对象，再单独查询影响，确保等待并发受理结束后能看到已提交的任务。</summary>
    private static async Task<WorkflowDeletionPreview?> ReadDeletionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string source, Guid id, bool locked, CancellationToken ct)
    {
        if (source is not ("workflow" or "template")) return null;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = (source == "workflow"
            ? "SELECT name, xmin::text, deleted_at FROM workflow WHERE workflow_id = @Id AND is_api AND debug_template_id IS NULL"
            : "SELECT name, xmin::text, deleted_at FROM workflow_template WHERE template_id = @Id") + (locked ? " FOR UPDATE" : "");
        Parameter(command, "Id", NpgsqlDbType.Uuid, id);
        string name, revision;
        DateTimeOffset? deletedAt;
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) return null;
            name = reader.GetString(0); revision = reader.GetString(1);
            deletedAt = reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2);
        }
        command.CommandText = """
            WITH scope AS (
                SELECT workflow_id FROM workflow
                WHERE (@Source = 'workflow' AND workflow_id = @Id) OR (@Source = 'template' AND debug_template_id = @Id)
            )
            SELECT
                (SELECT count(*) FROM workflow_deployment WHERE workflow_id IN (SELECT workflow_id FROM scope) AND status = 'published'),
                (SELECT count(*) FROM workflow_template WHERE @Source = 'template' AND template_id = @Id AND status = 'published'),
                (SELECT count(*) FROM workflow_run WHERE workflow_id IN (SELECT workflow_id FROM scope) AND status = 'queued'),
                (SELECT count(*) FROM workflow_run WHERE workflow_id IN (SELECT workflow_id FROM scope) AND status = 'running'),
                (SELECT count(*) FROM workflow_run WHERE workflow_id IN (SELECT workflow_id FROM scope)),
                (SELECT count(*) FROM workflow_deployment WHERE workflow_id IN (SELECT workflow_id FROM scope)),
                (SELECT count(*) FROM workflow_api_key WHERE workflow_id IN (SELECT workflow_id FROM scope)),
                (SELECT count(*) FROM workflow WHERE copied_from_source = @Source AND copied_from_id = @Id AND deleted_at IS NULL)
            """;
        Parameter(command, "Source", NpgsqlDbType.Text, source);
        await using var counts = await command.ExecuteReaderAsync(ct);
        await counts.ReadAsync(ct);
        return new(id.ToString(), source, name, revision, deletedAt,
            counts.GetInt64(0), counts.GetInt64(1), counts.GetInt64(2), counts.GetInt64(3),
            counts.GetInt64(4), counts.GetInt64(5), counts.GetInt64(6), counts.GetInt64(7));
    }
}
