using Dapper;
using Npgsql;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;

namespace CreativeFlow.Infrastructure.Persistence;

/// <summary>
/// 存储智能体对话和运行。JSON 内容以文本跨越 Npgsql，并显式转换为 jsonb；
/// 此方式与 <see cref="CanvasRepository"/> 一致，可避免 AOT 下的动态 JSON 映射。
/// </summary>
public sealed class AgentRepository(CreativeFlowConnectionFactory connections) : IAgentRepository
{
    /// <summary>Agent 会话查询使用的固定数据库列清单。</summary>
    private const string SessionColumns =
        "session_id, device_id, canvas_id, title, created_at, updated_at";

    /// <summary>工作流运行查询使用的固定数据库列清单。</summary>
    private const string RunColumns = """
        run_id, session_id, device_id, canvas_id, provider_id, model_key, status,
        executor_connection_id, iteration, last_seq, error, input_tokens, output_tokens,
        started_at, finished_at, created_at, updated_at
        """;

    /// <summary>在设备及账号共享设备的可访问范围内按标识查询 Agent 会话。</summary>
    public async Task<AgentSession?> FindSessionAsync(
        DeviceId deviceId,
        AgentSessionId sessionId,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var row = await connection.QueryFirstOrDefaultAsync<AgentSessionRow>(
            $"SELECT {SessionColumns} FROM agent_session WHERE session_id = @SessionId AND device_id IN (SELECT device_id FROM creativeflow_accessible_device_ids(@DeviceId))",
            new AgentSessionScopeParams { SessionId = sessionId.Value, DeviceId = deviceId.Value });
        return row?.ToDomain();
    }

    /// <summary>在当前可访问设备范围内读取指定画布已建立的 Agent 会话。</summary>
    public async Task<AgentSession?> FindSessionByCanvasAsync(
        DeviceId deviceId,
        CanvasId canvasId,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var row = await connection.QueryFirstOrDefaultAsync<AgentSessionRow>(
            $"SELECT {SessionColumns} FROM agent_session WHERE canvas_id = @CanvasId AND device_id IN (SELECT device_id FROM creativeflow_accessible_device_ids(@DeviceId))",
            new AgentCanvasScopeParams { CanvasId = canvasId.Value, DeviceId = deviceId.Value });
        return row?.ToDomain();
    }

    /// <summary>保存新 Agent 会话；单引擎执行不再写入迁移期的运行绑定表。</summary>
    public async Task InsertSessionAsync(AgentSession session, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await connection.ExecuteAsync(
            """
            INSERT INTO agent_session
                (session_id, device_id, canvas_id, title, created_at, updated_at)
            VALUES (@SessionId, @DeviceId, @CanvasId, @Title, @CreatedAt, @UpdatedAt)
            """,
            new InsertAgentSessionParams
            {
                SessionId = session.Id.Value,
                DeviceId = session.DeviceId.Value,
                CanvasId = session.CanvasId.Value,
                Title = session.Title,
                CreatedAt = session.CreatedAt,
                UpdatedAt = session.UpdatedAt,
            });
    }

    /// <summary>在当前可访问设备范围内查询 Agent 运行，越权或不存在时返回空。</summary>
    public async Task<AgentRun?> FindRunAsync(DeviceId deviceId, AgentRunId runId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var row = await connection.QueryFirstOrDefaultAsync<AgentRunRow>(
            $"SELECT {RunColumns} FROM agent_run WHERE run_id = @RunId AND device_id IN (SELECT device_id FROM creativeflow_accessible_device_ids(@DeviceId))",
            new AgentRunScopeParams { RunId = runId.Value, DeviceId = deviceId.Value });
        return row?.ToDomain();
    }

    /// <summary>供后台宿主按运行标识读取完整 Agent 运行，不依赖 HTTP 设备作用域。</summary>
    public async Task<AgentRun?> FindRunByIdAsync(AgentRunId runId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var row = await connection.QueryFirstOrDefaultAsync<AgentRunRow>(
            $"SELECT {RunColumns} FROM agent_run WHERE run_id = @RunId",
            new AgentRunIdParams { RunId = runId.Value });
        return row?.ToDomain();
    }

    /// <summary>查询可访问画布的排队、执行、等待画布或等待审批状态运行。</summary>
    public async Task<AgentRun?> FindActiveRunAsync(
        DeviceId deviceId,
        CanvasId canvasId,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var row = await connection.QueryFirstOrDefaultAsync<AgentRunRow>(
            $"""
            SELECT {RunColumns}
            FROM agent_run
            WHERE device_id IN (SELECT device_id FROM creativeflow_accessible_device_ids(@DeviceId)) AND canvas_id = @CanvasId
              AND status IN ('queued', 'running', 'awaiting_canvas', 'awaiting_approval')
            ORDER BY created_at DESC
            LIMIT 1
            """,
            new AgentCanvasScopeParams { DeviceId = deviceId.Value, CanvasId = canvasId.Value });
        return row?.ToDomain();
    }

    /// <summary>保存 Agent 运行，继续由原数据库约束保证会话归属和活动运行唯一性。</summary>
    public async Task InsertRunAsync(AgentRun run, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await connection.ExecuteAsync(
            """
            INSERT INTO agent_run
                (run_id, session_id, device_id, canvas_id, provider_id, model_key, status,
                 executor_connection_id, iteration, last_seq, error, input_tokens, output_tokens,
                 started_at, finished_at, created_at, updated_at)
            VALUES
                (@RunId, @SessionId, @DeviceId, @CanvasId, @ProviderId, @ModelKey, @Status,
                 @ExecutorConnectionId, @Iteration::smallint, @LastSequence, @Error,
                 @InputTokens, @OutputTokens, @StartedAt, @FinishedAt, @CreatedAt, @UpdatedAt)
            """,
            AgentRunParams.From(run));
    }

    /// <summary>在可访问设备范围内更新 Agent 运行状态、执行信息和用量统计，返回是否命中记录。</summary>
    public async Task<bool> UpdateRunAsync(AgentRun run, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var affected = await connection.ExecuteAsync(
            """
            UPDATE agent_run
            SET status = @Status,
                iteration = @Iteration::smallint,
                error = @Error,
                input_tokens = @InputTokens,
                output_tokens = @OutputTokens,
                started_at = @StartedAt,
                finished_at = @FinishedAt,
                updated_at = @UpdatedAt
            WHERE run_id = @RunId AND device_id IN (SELECT device_id FROM creativeflow_accessible_device_ids(@DeviceId))
            """,
            AgentRunParams.From(run));
        return affected > 0;
    }

    /// <summary>仅对可访问且仍活动的运行认领执行器连接，已有其他连接占用时返回 false。</summary>
    public async Task<bool> ClaimExecutorConnectionAsync(
        DeviceId deviceId,
        AgentRunId runId,
        CanvasId canvasId,
        string connectionId,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var affected = await connection.ExecuteAsync(
            """
            UPDATE agent_run
            SET executor_connection_id = @ConnectionId,
                status = CASE
                    WHEN status = 'awaiting_canvas' THEN 'running'
                    ELSE status
                END,
                updated_at = @Now
            WHERE run_id = @RunId AND device_id IN (SELECT device_id FROM creativeflow_accessible_device_ids(@DeviceId)) AND canvas_id = @CanvasId
              AND status IN ('queued', 'running', 'awaiting_canvas', 'awaiting_approval')
              AND (executor_connection_id IS NULL OR executor_connection_id = @ConnectionId)
            """,
            new ClaimAgentExecutorParams
            {
                RunId = runId.Value,
                DeviceId = deviceId.Value,
                CanvasId = canvasId.Value,
                ConnectionId = connectionId,
                Now = DateTimeOffset.UtcNow,
            });
        return affected > 0;
    }

    /// <summary>清除可访问活动运行中仍指向指定断开连接的执行器关联，返回受影响数量。</summary>
    public async Task<int> ClearExecutorConnectionAsync(
        DeviceId deviceId,
        string connectionId,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        return await connection.ExecuteAsync(
            """
            UPDATE agent_run
            SET executor_connection_id = NULL,
                status = CASE
                    WHEN status = 'queued' THEN status
                    ELSE 'awaiting_canvas'
                END,
                updated_at = @Now
            WHERE device_id IN (SELECT device_id FROM creativeflow_accessible_device_ids(@DeviceId)) AND executor_connection_id = @ConnectionId
              AND status IN ('queued', 'running', 'awaiting_canvas', 'awaiting_approval')
            """,
            new ClearAgentExecutorParams
            {
                DeviceId = deviceId.Value,
                ConnectionId = connectionId,
                Now = DateTimeOffset.UtcNow,
            });
    }

    /// <summary>按原排序与批次上限列出待执行 Agent 运行标识，供宿主扫描。</summary>
    public async Task<IReadOnlyList<AgentRunId>> ListQueuedAsync(int limit, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<QueuedAgentRunRow>(
            """
            SELECT run_id
            FROM agent_run
            WHERE status = 'queued'
            ORDER BY created_at
            LIMIT @Limit
            """,
            new AgentLimitParams { Limit = limit });
        return rows.Select(row => new AgentRunId(row.RunId)).ToList();
    }

    /// <summary>在可访问会话范围内按持久顺序读取 Agent 历史消息。</summary>
    public async Task<IReadOnlyList<AgentStoredMessage>> ListMessagesAsync(
        DeviceId deviceId,
        AgentSessionId sessionId,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<AgentMessageRow>(
            """
            SELECT message_id, session_id, run_id, device_id, ordinal, role,
                   content::text AS content_json, created_at
            FROM agent_message
            WHERE session_id = @SessionId AND device_id IN (SELECT device_id FROM creativeflow_accessible_device_ids(@DeviceId))
            ORDER BY ordinal
            """,
            new AgentSessionScopeParams { SessionId = sessionId.Value, DeviceId = deviceId.Value });
        return rows.Select(row => row.ToDomain()).ToList();
    }

    /// <summary>删除当前可访问会话的历史消息并返回删除数量。</summary>
    public async Task<int> ClearMessagesAsync(
        DeviceId deviceId,
        AgentSessionId sessionId,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        return await connection.ExecuteAsync(
            """
            DELETE FROM agent_message
            WHERE session_id = @SessionId AND device_id IN (SELECT device_id FROM creativeflow_accessible_device_ids(@DeviceId))
            """,
            new AgentSessionScopeParams { SessionId = sessionId.Value, DeviceId = deviceId.Value });
    }

    /// <summary>按给定会话顺序号保存 Agent 消息及其原内容 JSON。</summary>
    public async Task InsertMessageAsync(AgentStoredMessage message, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await connection.ExecuteAsync(
            """
            INSERT INTO agent_message
                (message_id, session_id, run_id, device_id, ordinal, role, content, created_at)
            VALUES
                (@MessageId, @SessionId, @RunId, @DeviceId, @Ordinal, @Role,
                 @ContentJson::jsonb, @CreatedAt)
            """,
            new InsertAgentMessageParams
            {
                MessageId = message.MessageId,
                SessionId = message.SessionId.Value,
                RunId = message.RunId?.Value,
                DeviceId = message.DeviceId.Value,
                Ordinal = message.Ordinal,
                Role = message.Role,
                ContentJson = message.ContentJson,
                CreatedAt = message.CreatedAt,
            });
    }

    /// <summary>根据当前可访问会话的已保存消息计算下一条顺序号，不在此处写入新消息。</summary>
    public async Task<int> NextMessageOrdinalAsync(
        DeviceId deviceId,
        AgentSessionId sessionId,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var row = await connection.QueryFirstAsync<AgentOrdinalRow>(
            """
            SELECT COALESCE(MAX(ordinal), -1) + 1 AS ordinal
            FROM agent_message
            WHERE session_id = @SessionId AND device_id IN (SELECT device_id FROM creativeflow_accessible_device_ids(@DeviceId))
            """,
            new AgentSessionScopeParams { SessionId = sessionId.Value, DeviceId = deviceId.Value });
        return row.Ordinal;
    }

    /// <summary>保存已开始的工具调用；同一运行与工具调用标识重复插入时不覆盖已有记录。</summary>
    public async Task InsertToolCallAsync(AgentStoredToolCall toolCall, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await connection.ExecuteAsync(
            """
            INSERT INTO agent_tool_call
                (tool_call_id, run_id, device_id, tool_use_id, tool_name, site, status,
                 input, result, error, started_at, finished_at)
            VALUES
                (@ToolCallId, @RunId, @DeviceId, @ToolUseId, @ToolName, @Site, @Status,
                 @InputJson::jsonb, @ResultJson::jsonb, @Error, @StartedAt, @FinishedAt)
            ON CONFLICT (run_id, tool_use_id) DO NOTHING
            """,
            AgentToolCallParams.From(toolCall));
    }

    /// <summary>在可访问运行范围内写入工具调用终态、输出、错误和完成时间。</summary>
    public async Task<bool> CompleteToolCallAsync(
        DeviceId deviceId,
        AgentRunId runId,
        string toolUseId,
        string status,
        string resultJson,
        string? error,
        DateTimeOffset finishedAt,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var affected = await connection.ExecuteAsync(
            """
            UPDATE agent_tool_call
            SET status = @Status,
                result = @ResultJson::jsonb,
                error = @Error,
                finished_at = @FinishedAt
            WHERE run_id = @RunId AND device_id IN (SELECT device_id FROM creativeflow_accessible_device_ids(@DeviceId)) AND tool_use_id = @ToolUseId
            """,
            new CompleteAgentToolCallParams
            {
                DeviceId = deviceId.Value,
                RunId = runId.Value,
                ToolUseId = toolUseId,
                Status = status,
                ResultJson = resultJson,
                Error = error,
                FinishedAt = finishedAt,
            });
        return affected > 0;
    }

    /// <summary>在同一事务递增运行事件序号并保存事件，防止重复序号或只更新序号而丢失载荷。</summary>
    public async Task<AgentStoredEvent?> AppendEventAsync(
        AgentRunId runId,
        DeviceId deviceId,
        string type,
        string payloadJson,
        CancellationToken ct)
    {
        // 序号递增与事件插入在同一事务内提交，防止并发追加产生相同序号或只有序号没有事件的状态。
        await using var connection = await connections.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        var sequence = await connection.QueryFirstOrDefaultAsync<AgentSequenceRow>(
            """
            UPDATE agent_run
            SET last_seq = last_seq + 1, updated_at = @CreatedAt
            WHERE run_id = @RunId AND device_id IN (SELECT device_id FROM creativeflow_accessible_device_ids(@DeviceId))
            RETURNING last_seq AS sequence
            """,
            new AppendAgentEventParams
            {
                RunId = runId.Value,
                DeviceId = deviceId.Value,
                Type = type,
                PayloadJson = payloadJson,
                CreatedAt = DateTimeOffset.UtcNow,
            },
            transaction);

        if (sequence is null)
        {
            await transaction.RollbackAsync(ct);
            return null;
        }

        var createdAt = DateTimeOffset.UtcNow;
        await connection.ExecuteAsync(
            """
            INSERT INTO agent_event (run_id, device_id, seq, type, payload, created_at)
            VALUES (@RunId, @DeviceId, @Sequence, @Type, @PayloadJson::jsonb, @CreatedAt)
            """,
            new InsertAgentEventParams
            {
                RunId = runId.Value,
                DeviceId = deviceId.Value,
                Sequence = sequence.Sequence,
                Type = type,
                PayloadJson = payloadJson,
                CreatedAt = createdAt,
            },
            transaction);

        await transaction.CommitAsync(ct);
        return new AgentStoredEvent(runId, deviceId, sequence.Sequence, type, payloadJson, createdAt);
    }

    /// <summary>在可访问运行范围内按持久序号读取指定游标之后的事件，供断线重放。</summary>
    public async Task<IReadOnlyList<AgentStoredEvent>> ListEventsAsync(
        DeviceId deviceId,
        AgentRunId runId,
        long afterSequence,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<AgentEventRow>(
            """
            SELECT event.run_id, event.device_id, event.seq, event.type,
                   event.payload::text AS payload_json, event.created_at
            FROM agent_event event
            JOIN agent_run run ON run.run_id = event.run_id
            WHERE event.run_id = @RunId AND event.device_id IN (SELECT device_id FROM creativeflow_accessible_device_ids(@DeviceId))
              AND run.device_id IN (SELECT device_id FROM creativeflow_accessible_device_ids(@DeviceId)) AND event.seq > @AfterSequence
            ORDER BY event.seq
            LIMIT 1000
            """,
            new AgentEventScopeParams
            {
                RunId = runId.Value,
                DeviceId = deviceId.Value,
                AfterSequence = Math.Max(0, afterSequence),
            });
        return rows.Select(row => row.ToDomain()).ToList();
    }

    /// <summary>将上一进程遗留的执行、等待画布和等待审批运行标记为 interrupted，保留排队任务。</summary>
    public async Task<int> InterruptOrphanedRunsAsync(DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        return await connection.ExecuteAsync(
            """
            UPDATE agent_run
            SET status = 'interrupted',
                error = 'The server restarted while this run was active.',
                finished_at = @Now,
                updated_at = @Now
            WHERE status IN ('running', 'awaiting_canvas', 'awaiting_approval')
            """,
            new AgentNowParams { Now = now });
    }

    /// <summary>将业务枚举映射为既有存储或接口字符串。</summary>
    internal static string ToWire(AgentRunStatus status) => status switch
    {
        AgentRunStatus.Queued => "queued",
        AgentRunStatus.Running => "running",
        AgentRunStatus.AwaitingCanvas => "awaiting_canvas",
        AgentRunStatus.AwaitingApproval => "awaiting_approval",
        AgentRunStatus.Succeeded => "succeeded",
        AgentRunStatus.Failed => "failed",
        AgentRunStatus.Cancelled => "cancelled",
        AgentRunStatus.Interrupted => "interrupted",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown agent run status."),
    };

    /// <summary>将数据库中的既有状态字符串还原为业务枚举。</summary>
    internal static AgentRunStatus ParseStatus(string status) => status switch
    {
        "queued" => AgentRunStatus.Queued,
        "running" => AgentRunStatus.Running,
        "awaiting_canvas" => AgentRunStatus.AwaitingCanvas,
        "awaiting_approval" => AgentRunStatus.AwaitingApproval,
        "succeeded" => AgentRunStatus.Succeeded,
        "failed" => AgentRunStatus.Failed,
        "cancelled" => AgentRunStatus.Cancelled,
        "interrupted" => AgentRunStatus.Interrupted,
        _ => throw new InvalidOperationException($"Unrecognised agent run status '{status}'."),
    };
}

internal sealed class AgentSessionScopeParams {
    /// <summary>账号会话或 Agent 会话的关联标识。</summary>
    public Guid SessionId { get; init; }
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; } }
internal sealed class AgentCanvasScopeParams {
    /// <summary>关联画布的唯一标识。</summary>
    public Guid CanvasId { get; init; }
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; } }
internal sealed class AgentRunScopeParams {
    /// <summary>工作流或 Agent 运行的唯一标识。</summary>
    public Guid RunId { get; init; }
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; } }
internal sealed class AgentRunIdParams {
    /// <summary>工作流或 Agent 运行的唯一标识。</summary>
    public Guid RunId { get; init; } }
internal sealed class AgentLimitParams {
    /// <summary>本次查询或处理允许返回的最大数量。</summary>
    public int Limit { get; init; } }
internal sealed class AgentNowParams {
    /// <summary>本次操作使用的时间基准。</summary>
    public DateTimeOffset Now { get; init; } }
internal sealed class ClaimAgentExecutorParams
{
    /// <summary>工作流或 Agent 运行的唯一标识。</summary>
    public Guid RunId { get; init; }
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; }
    /// <summary>关联画布的唯一标识。</summary>
    public Guid CanvasId { get; init; }
    /// <summary>SignalR 浏览器连接标识。</summary>
    public string ConnectionId { get; init; } = string.Empty;
    /// <summary>本次操作使用的时间基准。</summary>
    public DateTimeOffset Now { get; init; }
}
internal sealed class ClearAgentExecutorParams
{
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; }
    /// <summary>SignalR 浏览器连接标识。</summary>
    public string ConnectionId { get; init; } = string.Empty;
    /// <summary>本次操作使用的时间基准。</summary>
    public DateTimeOffset Now { get; init; }
}
internal sealed class AgentEventScopeParams {
    /// <summary>工作流或 Agent 运行的唯一标识。</summary>
    public Guid RunId { get; init; }
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; }
    /// <summary>增量读取事件时排除的最后已知序号。</summary>
    public long AfterSequence { get; init; } }
internal sealed class AgentOrdinalRow {
    /// <summary>消息在会话中的持久顺序号。</summary>
    public int Ordinal { get; init; } }
internal sealed class AgentSequenceRow {
    /// <summary>持久事件序号；Agent 临时增量事件仍按原约定使用零。</summary>
    public long Sequence { get; init; } }
internal sealed class QueuedAgentRunRow {
    /// <summary>工作流或 Agent 运行的唯一标识。</summary>
    public Guid RunId { get; init; } }

internal sealed class InsertAgentSessionParams
{
    /// <summary>账号会话或 Agent 会话的关联标识。</summary>
    public Guid SessionId { get; init; }
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; }
    /// <summary>关联画布的唯一标识。</summary>
    public Guid CanvasId { get; init; }
    /// <summary>内容、画布或模板的显示标题。</summary>
    public string? Title { get; init; }
    /// <summary>记录创建时间，沿用当前 UTC 与序列化约定。</summary>
    public DateTimeOffset CreatedAt { get; init; }
    /// <summary>记录最近一次修改的 UTC 时间。</summary>
    public DateTimeOffset UpdatedAt { get; init; }
}

internal sealed class AgentRunParams
{
    /// <summary>工作流或 Agent 运行的唯一标识。</summary>
    public Guid RunId { get; init; }
    /// <summary>账号会话或 Agent 会话的关联标识。</summary>
    public Guid SessionId { get; init; }
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; }
    /// <summary>关联画布的唯一标识。</summary>
    public Guid CanvasId { get; init; }
    /// <summary>关联的 AI 渠道标识。</summary>
    public Guid? ProviderId { get; init; }
    /// <summary>上游识别的模型名称，不等同于本地模型配置标识。</summary>
    public string ModelKey { get; init; } = string.Empty;
    /// <summary>当前对象的业务状态，存储和接口名称保持既有映射。</summary>
    public string Status { get; init; } = "queued";
    /// <summary>认领当前 Agent 执行的浏览器连接标识。</summary>
    public string? ExecutorConnectionId { get; init; }
    /// <summary>Agent 当前执行的轮次编号。</summary>
    public int Iteration { get; init; }
    /// <summary>最后一条已持久化或已消费事件的序号。</summary>
    public long LastSequence { get; init; }
    /// <summary>失败详情或错误分类，具体格式沿用所属契约。</summary>
    public string? Error { get; init; }
    /// <summary>模型报告或运行累计的输入 token 数量。</summary>
    public int? InputTokens { get; init; }
    /// <summary>模型报告或运行累计的输出 token 数量。</summary>
    public int? OutputTokens { get; init; }
    /// <summary>运行、节点或工具开始执行的时间。</summary>
    public DateTimeOffset? StartedAt { get; init; }
    /// <summary>运行、节点或工具调用到达终止状态的时间。</summary>
    public DateTimeOffset? FinishedAt { get; init; }
    /// <summary>记录创建时间，沿用当前 UTC 与序列化约定。</summary>
    public DateTimeOffset CreatedAt { get; init; }
    /// <summary>记录最近一次修改的 UTC 时间。</summary>
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>把业务记录投影为当前存储操作的静态参数。</summary>
    public static AgentRunParams From(AgentRun run) => new()
    {
        RunId = run.Id.Value,
        SessionId = run.SessionId.Value,
        DeviceId = run.DeviceId.Value,
        CanvasId = run.CanvasId.Value,
        ProviderId = run.ProviderId?.Value,
        ModelKey = run.ModelKey,
        Status = AgentRepository.ToWire(run.Status),
        ExecutorConnectionId = run.ExecutorConnectionId,
        Iteration = run.Iteration,
        LastSequence = run.LastSequence,
        Error = run.Error,
        InputTokens = run.InputTokens,
        OutputTokens = run.OutputTokens,
        StartedAt = run.StartedAt,
        FinishedAt = run.FinishedAt,
        CreatedAt = run.CreatedAt,
        UpdatedAt = run.UpdatedAt,
    };
}

internal sealed class InsertAgentMessageParams
{
    /// <summary>持久化消息的唯一标识。</summary>
    public Guid MessageId { get; init; }
    /// <summary>账号会话或 Agent 会话的关联标识。</summary>
    public Guid SessionId { get; init; }
    /// <summary>工作流或 Agent 运行的唯一标识。</summary>
    public Guid? RunId { get; init; }
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; }
    /// <summary>消息在会话中的持久顺序号。</summary>
    public int Ordinal { get; init; }
    /// <summary>消息角色或模型角色，含义由所属契约定义。</summary>
    public string Role { get; init; } = "user";
    /// <summary>保留消息块顺序的 JSON 内容。</summary>
    public string ContentJson { get; init; } = "[]";
    /// <summary>记录创建时间，沿用当前 UTC 与序列化约定。</summary>
    public DateTimeOffset CreatedAt { get; init; }
}

internal sealed class AppendAgentEventParams
{
    /// <summary>工作流或 Agent 运行的唯一标识。</summary>
    public Guid RunId { get; init; }
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; }
    /// <summary>该数据项的协议、值或事件类型，沿用所属契约的取值。</summary>
    public string Type { get; init; } = string.Empty;
    /// <summary>事件或缓存载荷的 JSON 表示。</summary>
    public string PayloadJson { get; init; } = "{}";
    /// <summary>记录创建时间，沿用当前 UTC 与序列化约定。</summary>
    public DateTimeOffset CreatedAt { get; init; }
}

internal sealed class AgentToolCallParams
{
    /// <summary>工具调用的持久标识。</summary>
    public Guid ToolCallId { get; init; }
    /// <summary>工作流或 Agent 运行的唯一标识。</summary>
    public Guid RunId { get; init; }
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; }
    /// <summary>一次模型工具调用的关联标识。</summary>
    public string ToolUseId { get; init; } = string.Empty;
    /// <summary>模型调用或界面展示的工具名称。</summary>
    public string ToolName { get; init; } = string.Empty;
    /// <summary>工具在服务器端还是浏览器画布上执行。</summary>
    public string Site { get; init; } = "canvas";
    /// <summary>当前对象的业务状态，存储和接口名称保持既有映射。</summary>
    public string Status { get; init; } = "running";
    /// <summary>执行输入的 JSON 快照。</summary>
    public string InputJson { get; init; } = "{}";
    /// <summary>工具或上游调用结果的 JSON 表示。</summary>
    public string? ResultJson { get; init; }
    /// <summary>失败详情或错误分类，具体格式沿用所属契约。</summary>
    public string? Error { get; init; }
    /// <summary>运行、节点或工具开始执行的时间。</summary>
    public DateTimeOffset? StartedAt { get; init; }
    /// <summary>运行、节点或工具调用到达终止状态的时间。</summary>
    public DateTimeOffset? FinishedAt { get; init; }

    /// <summary>把业务记录投影为当前存储操作的静态参数。</summary>
    public static AgentToolCallParams From(AgentStoredToolCall toolCall) => new()
    {
        ToolCallId = toolCall.ToolCallId,
        RunId = toolCall.RunId.Value,
        DeviceId = toolCall.DeviceId.Value,
        ToolUseId = toolCall.ToolUseId,
        ToolName = toolCall.ToolName,
        Site = toolCall.Site,
        Status = toolCall.Status,
        InputJson = toolCall.InputJson,
        ResultJson = toolCall.ResultJson,
        Error = toolCall.Error,
        StartedAt = toolCall.StartedAt,
        FinishedAt = toolCall.FinishedAt,
    };
}

internal sealed class CompleteAgentToolCallParams
{
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; }
    /// <summary>工作流或 Agent 运行的唯一标识。</summary>
    public Guid RunId { get; init; }
    /// <summary>一次模型工具调用的关联标识。</summary>
    public string ToolUseId { get; init; } = string.Empty;
    /// <summary>当前对象的业务状态，存储和接口名称保持既有映射。</summary>
    public string Status { get; init; } = "succeeded";
    /// <summary>工具或上游调用结果的 JSON 表示。</summary>
    public string ResultJson { get; init; } = "{}";
    /// <summary>失败详情或错误分类，具体格式沿用所属契约。</summary>
    public string? Error { get; init; }
    /// <summary>运行、节点或工具调用到达终止状态的时间。</summary>
    public DateTimeOffset FinishedAt { get; init; }
}

internal sealed class InsertAgentEventParams
{
    /// <summary>工作流或 Agent 运行的唯一标识。</summary>
    public Guid RunId { get; init; }
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; }
    /// <summary>持久事件序号；Agent 临时增量事件仍按原约定使用零。</summary>
    public long Sequence { get; init; }
    /// <summary>该数据项的协议、值或事件类型，沿用所属契约的取值。</summary>
    public string Type { get; init; } = string.Empty;
    /// <summary>事件或缓存载荷的 JSON 表示。</summary>
    public string PayloadJson { get; init; } = "{}";
    /// <summary>记录创建时间，沿用当前 UTC 与序列化约定。</summary>
    public DateTimeOffset CreatedAt { get; init; }
}

internal sealed class AgentSessionRow
{
    /// <summary>账号会话或 Agent 会话的关联标识。</summary>
    public Guid SessionId { get; init; }
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; }
    /// <summary>关联画布的唯一标识。</summary>
    public Guid CanvasId { get; init; }
    /// <summary>内容、画布或模板的显示标题。</summary>
    public string? Title { get; init; }
    /// <summary>记录创建时间，沿用当前 UTC 与序列化约定。</summary>
    public DateTime CreatedAt { get; init; }
    /// <summary>记录最近一次修改的 UTC 时间。</summary>
    public DateTime UpdatedAt { get; init; }

    /// <summary>把数据库静态行映射为业务记录，保留既有状态和时间语义。</summary>
    public AgentSession ToDomain() => new(
        new AgentSessionId(SessionId), new DeviceId(DeviceId), new CanvasId(CanvasId), Title,
        Utc(CreatedAt), Utc(UpdatedAt));

    /// <summary>将数据库时间值按原有约定转换为 UTC DateTimeOffset。</summary>
    private static DateTimeOffset Utc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}

internal sealed class AgentRunRow
{
    /// <summary>工作流或 Agent 运行的唯一标识。</summary>
    public Guid RunId { get; init; }
    /// <summary>账号会话或 Agent 会话的关联标识。</summary>
    public Guid SessionId { get; init; }
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; }
    /// <summary>关联画布的唯一标识。</summary>
    public Guid CanvasId { get; init; }
    /// <summary>关联的 AI 渠道标识。</summary>
    public Guid? ProviderId { get; init; }
    /// <summary>上游识别的模型名称，不等同于本地模型配置标识。</summary>
    public string ModelKey { get; init; } = string.Empty;
    /// <summary>当前对象的业务状态，存储和接口名称保持既有映射。</summary>
    public string Status { get; init; } = "queued";
    /// <summary>认领当前 Agent 执行的浏览器连接标识。</summary>
    public string? ExecutorConnectionId { get; init; }
    /// <summary>Agent 当前执行的轮次编号。</summary>
    public int Iteration { get; init; }
    /// <summary>运行最后一条持久化事件的序号。</summary>
    public long LastSeq { get; init; }
    /// <summary>失败详情或错误分类，具体格式沿用所属契约。</summary>
    public string? Error { get; init; }
    /// <summary>模型报告或运行累计的输入 token 数量。</summary>
    public int? InputTokens { get; init; }
    /// <summary>模型报告或运行累计的输出 token 数量。</summary>
    public int? OutputTokens { get; init; }
    /// <summary>运行、节点或工具开始执行的时间。</summary>
    public DateTime? StartedAt { get; init; }
    /// <summary>运行、节点或工具调用到达终止状态的时间。</summary>
    public DateTime? FinishedAt { get; init; }
    /// <summary>记录创建时间，沿用当前 UTC 与序列化约定。</summary>
    public DateTime CreatedAt { get; init; }
    /// <summary>记录最近一次修改的 UTC 时间。</summary>
    public DateTime UpdatedAt { get; init; }

    /// <summary>把数据库静态行映射为业务记录，保留既有状态和时间语义。</summary>
    public AgentRun ToDomain() => new(
        new AgentRunId(RunId), new AgentSessionId(SessionId), new DeviceId(DeviceId),
        new CanvasId(CanvasId), ProviderId is { } provider ? new ProviderId(provider) : null,
        ModelKey, AgentRepository.ParseStatus(Status), ExecutorConnectionId, Iteration, LastSeq,
        Error, InputTokens, OutputTokens, UtcOrNull(StartedAt), UtcOrNull(FinishedAt),
        Utc(CreatedAt), Utc(UpdatedAt));

    /// <summary>将数据库时间值按原有约定转换为 UTC DateTimeOffset。</summary>
    private static DateTimeOffset Utc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    /// <summary>转换可空的数据库 UTC 时间，保留空值。</summary>
    private static DateTimeOffset? UtcOrNull(DateTime? value) => value is { } actual ? Utc(actual) : null;
}

internal sealed class AgentMessageRow
{
    /// <summary>持久化消息的唯一标识。</summary>
    public Guid MessageId { get; init; }
    /// <summary>账号会话或 Agent 会话的关联标识。</summary>
    public Guid SessionId { get; init; }
    /// <summary>工作流或 Agent 运行的唯一标识。</summary>
    public Guid? RunId { get; init; }
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; }
    /// <summary>消息在会话中的持久顺序号。</summary>
    public int Ordinal { get; init; }
    /// <summary>消息角色或模型角色，含义由所属契约定义。</summary>
    public string Role { get; init; } = "user";
    /// <summary>保留消息块顺序的 JSON 内容。</summary>
    public string ContentJson { get; init; } = "[]";
    /// <summary>记录创建时间，沿用当前 UTC 与序列化约定。</summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>把数据库静态行映射为业务记录，保留既有状态和时间语义。</summary>
    public AgentStoredMessage ToDomain() => new(
        MessageId, new AgentSessionId(SessionId), RunId is { } run ? new AgentRunId(run) : null,
        new DeviceId(DeviceId), Ordinal, Role, ContentJson,
        new DateTimeOffset(DateTime.SpecifyKind(CreatedAt, DateTimeKind.Utc)));
}

internal sealed class AgentEventRow
{
    /// <summary>工作流或 Agent 运行的唯一标识。</summary>
    public Guid RunId { get; init; }
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; }
    /// <summary>事件或消息的持久序号。</summary>
    public long Seq { get; init; }
    /// <summary>该数据项的协议、值或事件类型，沿用所属契约的取值。</summary>
    public string Type { get; init; } = string.Empty;
    /// <summary>事件或缓存载荷的 JSON 表示。</summary>
    public string PayloadJson { get; init; } = "{}";
    /// <summary>记录创建时间，沿用当前 UTC 与序列化约定。</summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>把数据库静态行映射为业务记录，保留既有状态和时间语义。</summary>
    public AgentStoredEvent ToDomain() => new(
        new AgentRunId(RunId), new DeviceId(DeviceId), Seq, Type, PayloadJson,
        new DateTimeOffset(DateTime.SpecifyKind(CreatedAt, DateTimeKind.Utc)));
}
