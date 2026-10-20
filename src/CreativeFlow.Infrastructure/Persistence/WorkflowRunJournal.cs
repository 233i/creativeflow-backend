using Npgsql;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Infrastructure.Persistence;

/// <summary>读取数据库事务自动记录的运行／节点状态及公开文本，跨作用域和重启保留。</summary>
public sealed class WorkflowRunJournal(CreativeFlowConnectionFactory connections) : IWorkflowRunJournal
{
    /// <summary>每次最多读取 32 条，限制慢订阅者的缓存和权限复核窗口。</summary>
    public async Task<IReadOnlyList<WorkflowRunJournalEntry>> ReadAsync(WorkflowRunId runId, long after, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT sequence,kind,data::text,created_at FROM workflow_run_event WHERE run_id=$1 AND sequence>$2 ORDER BY sequence LIMIT 32", connection);
        command.Parameters.AddWithValue(runId.Value); command.Parameters.AddWithValue(after);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<WorkflowRunJournalEntry>();
        while (await reader.ReadAsync(ct)) result.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetFieldValue<DateTimeOffset>(3)));
        return result;
    }
}
