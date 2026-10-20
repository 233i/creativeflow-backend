using Npgsql;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Infrastructure.Persistence;

/// <summary>文本日志与运行同库保存；短事务锁定运行，确保取消/完成后没有晚到增量。</summary>
public sealed class WorkflowRunEventRepository(CreativeFlowConnectionFactory connections) : IWorkflowRunEventRepository
{
    public async Task AppendAsync(WorkflowRunId runId, string data, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var check = new NpgsqlCommand("SELECT status FROM workflow_run WHERE run_id=$1 AND trigger_type='api' FOR UPDATE", connection, transaction);
        check.Parameters.AddWithValue(runId.Value);
        if (await check.ExecuteScalarAsync(ct) is not "running") return;
        await using var insert = new NpgsqlCommand("""
            INSERT INTO workflow_run_text_event (run_id, sequence, data)
            SELECT $1, COALESCE(MAX(sequence), 0) + 1, $2::jsonb
            FROM workflow_run_text_event WHERE run_id=$1
            """, connection, transaction);
        insert.Parameters.AddWithValue(runId.Value); insert.Parameters.AddWithValue(data);
        await insert.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<WorkflowRunTextEvent>> ReadAsync(WorkflowRunId runId, long after, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT sequence, data::text FROM workflow_run_text_event WHERE run_id=$1 AND sequence>$2 ORDER BY sequence LIMIT 16", connection);
        command.Parameters.AddWithValue(runId.Value); command.Parameters.AddWithValue(after);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<WorkflowRunTextEvent>();
        while (await reader.ReadAsync(ct)) result.Add(new(reader.GetInt64(0), reader.GetString(1)));
        return result;
    }

    public async Task<long> LastSequenceAsync(WorkflowRunId runId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT COALESCE(MAX(sequence),0) FROM workflow_run_text_event WHERE run_id=$1", connection);
        command.Parameters.AddWithValue(runId.Value);
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }
}
