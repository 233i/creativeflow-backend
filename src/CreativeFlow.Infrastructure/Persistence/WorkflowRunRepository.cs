using Dapper;
using Npgsql;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;

namespace CreativeFlow.Infrastructure.Persistence;

/// <summary>初始化 WorkflowRunRepository 的依赖与实例状态。</summary>
public sealed class WorkflowRunRepository(CreativeFlowConnectionFactory connections) : IWorkflowRunRepository
{
    /// <summary>工作流运行查询使用的固定数据库列清单。</summary>
    private const string RunColumns = """
        run_id, workflow_id, device_id, graph_snapshot::text AS graph_json,
        resumed_from_run_id, status, error, created_at, updated_at, started_at, finished_at,
        trigger_type, deployment_id, api_key_id, request_id, idempotency_key, request_hash,
        input_json::text AS input_json
        """;

    /// <summary>运行更新/认领语句返回的固定列清单。</summary>
    private const string ReturnedRunColumns = """
        run.run_id, run.workflow_id, run.device_id,
        run.graph_snapshot::text AS graph_json, run.resumed_from_run_id, run.status, run.error,
        run.created_at, run.updated_at, run.started_at, run.finished_at,
        run.trigger_type, run.deployment_id, run.api_key_id, run.request_id,
        run.idempotency_key, run.request_hash, run.input_json::text AS input_json
        """;

    /// <summary>通过统一受理事务保存交互式运行及节点；未成功插入时返回 false。</summary>
    public async Task<bool> InsertAsync(
        WorkflowRun run,
        IReadOnlyList<WorkflowNodeRun> nodes,
        CancellationToken ct)
        => await InsertCoreAsync(run, nodes, null, ct) == WorkflowRunInsertResult.Inserted;

    /// <summary>通过统一受理事务校验外部排队上限、幂等约束并保存运行及节点。</summary>
    public Task<WorkflowRunInsertResult> InsertExternalAsync(
        WorkflowRun run,
        IReadOnlyList<WorkflowNodeRun> nodes,
        int maxQueuedRuns,
        CancellationToken ct)
    {
        if (run.TriggerType != WorkflowRunTriggerType.Api)
            throw new ArgumentException("Only API-triggered runs can use the external queue.", nameof(run));
        if (maxQueuedRuns <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxQueuedRuns));

        return InsertCoreAsync(run, nodes, maxQueuedRuns, ct);
    }

    /// <summary>在同一连接和事务边界内完成当前原子写入操作。</summary>
    private async Task<WorkflowRunInsertResult> InsertCoreAsync(
        WorkflowRun run,
        IReadOnlyList<WorkflowNodeRun> nodes,
        int? maxQueuedRuns,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        if (maxQueuedRuns is { } queueLimit)
        {
            // Serialize admission for one workflow. A plain COUNT followed by INSERT would let
            // concurrent requests all observe the same free slot and exceed the configured cap.
            await connection.ExecuteAsync(
                """
                SELECT pg_advisory_xact_lock(
                    hashtextextended('workflow-external-queue:' || CAST(@WorkflowId AS text), 0))
                """,
                new WorkflowOnlyRunParams { WorkflowId = run.WorkflowId.Value },
                transaction);

            var queued = await connection.ExecuteScalarAsync<int>(
                """
                SELECT COUNT(*)::integer FROM workflow_run
                WHERE workflow_id = @WorkflowId AND trigger_type = 'api' AND status = 'queued'
                """,
                new WorkflowOnlyRunParams { WorkflowId = run.WorkflowId.Value },
                transaction);
            if (queued >= queueLimit)
            {
                await transaction.RollbackAsync(ct);
                return WorkflowRunInsertResult.QueueFull;
            }
        }

        try
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO workflow_run
                    (run_id, workflow_id, device_id, graph_snapshot, resumed_from_run_id,
                     status, error, created_at, updated_at, started_at, finished_at,
                     trigger_type, deployment_id, api_key_id, request_id, idempotency_key,
                     request_hash, input_json)
                VALUES
                    (@RunId, @WorkflowId, @DeviceId, @GraphJson::jsonb, @ResumedFromRunId,
                     @Status, @Error, @CreatedAt, @UpdatedAt, @StartedAt, @FinishedAt,
                     @TriggerType, @DeploymentId, @ApiKeyId, @RequestId, @IdempotencyKey,
                     @RequestHash, @InputJson::jsonb)
                """,
                WorkflowRunWriteParams.From(run),
                transaction);
        }
        catch (PostgresException exception)
            when (exception.ConstraintName == "workflow_deleted")
        {
            await transaction.RollbackAsync(ct);
            return WorkflowRunInsertResult.Duplicate;
        }
        catch (PostgresException exception)
            when (exception.ConstraintName == "knowledge_version_unavailable")
        {
            await transaction.RollbackAsync(ct);
            throw new CreativeFlow.Application.Abstractions.Ai.WorkflowV2Exception("知识版本在受理期间已不可用，请重新确认资源配置。");
        }
        catch (PostgresException exception)
            when (exception.SqlState == PostgresErrorCodes.UniqueViolation
                  && exception.ConstraintName is "uq_workflow_run_active_interactive"
                      or "uq_workflow_run_idempotency" or "uq_workflow_run_agui_identity"
                  || exception.SqlState == PostgresErrorCodes.UniqueViolation && exception.ConstraintName == "workflow_run_pkey"
                     && CreativeFlow.Application.Features.Workflow.WorkflowNodeDebug.Target(run.InputJson) is not null)
        {
            await transaction.RollbackAsync(ct);
            return WorkflowRunInsertResult.Duplicate;
        }

        foreach (var node in nodes)
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO workflow_node_run
                    (run_id, node_id, node_type, status, is_reused, progress, output, error,
                     started_at, finished_at, attempt, max_attempts, retry_at, skip_reason)
                VALUES
                    (@RunId, @NodeId, @NodeType, @Status, @IsReused, @Progress,
                     @OutputJson::jsonb, @Error, @StartedAt, @FinishedAt,
                     @Attempt, @MaxAttempts, @RetryAt, @SkipReason)
                """,
                WorkflowNodeRunWriteParams.From(node),
                transaction);
        }

        await transaction.CommitAsync(ct);
        return WorkflowRunInsertResult.Inserted;
    }

    /// <summary>在可访问设备与工作流范围内查询交互式运行，不混入 API 触发的运行。</summary>
    public async Task<WorkflowRun?> FindAsync(
        DeviceId deviceId,
        WorkflowId workflowId,
        WorkflowRunId runId,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var row = await connection.QueryFirstOrDefaultAsync<WorkflowRunRow>(
            $"""
            SELECT {RunColumns}
            FROM workflow_run
            WHERE run_id = @RunId
              AND workflow_id = @WorkflowId
              AND trigger_type = 'interactive'
              AND device_id IN (SELECT device_id FROM creativeflow_accessible_device_ids(@DeviceId))
            """,
            new WorkflowRunScopeParams
            {
                RunId = runId.Value,
                WorkflowId = workflowId.Value,
                DeviceId = deviceId.Value,
            });

        return row?.ToDomain();
    }

    /// <summary>读取可访问工作流最近一次交互式运行，包括已经结束的运行。</summary>
    public Task<WorkflowRun?> FindLatestAsync(
        DeviceId deviceId,
        WorkflowId workflowId,
        CancellationToken ct) => FindOneAsync(deviceId, workflowId, false, ct);

    /// <summary>读取可访问工作流当前仍活动的交互式运行。</summary>
    public Task<WorkflowRun?> FindActiveAsync(
        DeviceId deviceId,
        WorkflowId workflowId,
        CancellationToken ct) => FindOneAsync(deviceId, workflowId, true, ct);

    /// <summary>仅在指定工作流及 API 触发范围内查找外部运行。</summary>
    public async Task<WorkflowRun?> FindExternalAsync(
        WorkflowId workflowId,
        WorkflowRunId runId,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var row = await connection.QueryFirstOrDefaultAsync<WorkflowRunRow>(
            $"""
            SELECT {RunColumns}
            FROM workflow_run
            WHERE run_id = @RunId AND workflow_id = @WorkflowId AND trigger_type = 'api'
            """,
            new ExternalRunScopeParams
            {
                RunId = runId.Value,
                WorkflowId = workflowId.Value,
            });
        return row?.ToDomain();
    }

    /// <summary>按 API Key 与幂等键读取首次受理的运行，配置切换不创建新任务。</summary>
    public async Task<WorkflowRun?> FindByIdempotencyAsync(
        WorkflowApiKeyId apiKeyId,
        string idempotencyKey,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var row = await connection.QueryFirstOrDefaultAsync<WorkflowRunRow>(
            $"""
            SELECT {RunColumns}
            FROM workflow_run
            WHERE api_key_id = @ApiKeyId AND idempotency_key = @IdempotencyKey
            """,
            new IdempotencyLookupParams
            {
                ApiKeyId = apiKeyId.Value,
                IdempotencyKey = idempotencyKey,
            });
        return row?.ToDomain();
    }

    /// <summary>历史列表只投影有界安全元数据；按实际账号设备范围筛选，节点结果需单独授权读取。</summary>
    public async Task<System.Text.Json.Nodes.JsonArray> ListInteractiveAsync(DeviceId deviceId, WorkflowId workflowId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<WorkflowHistoryRow>("""
            SELECT run_id, status, resumed_from_run_id, created_at, updated_at, started_at, finished_at,
                   input_json->'nodeDebug'->>'nodeId' AS debug_node_id
            FROM workflow_run WHERE workflow_id = @WorkflowId AND trigger_type = 'interactive'
              AND device_id IN (SELECT device_id FROM creativeflow_accessible_device_ids(@DeviceId))
            ORDER BY created_at DESC, run_id DESC LIMIT 50
            """, new ExternalRunListParams { DeviceId = deviceId.Value, WorkflowId = workflowId.Value, Limit = 50 });
        return new System.Text.Json.Nodes.JsonArray(rows.Select(row => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject
        {
            ["id"] = row.RunId.ToString(), ["status"] = row.Status, ["resumedFromRunId"] = row.ResumedFromRunId?.ToString(),
            ["debugNodeId"] = row.DebugNodeId,
            ["createdAt"] = WorkflowPersistenceTime.ToOffset(row.CreatedAt).ToString("O"), ["updatedAt"] = WorkflowPersistenceTime.ToOffset(row.UpdatedAt).ToString("O"),
            ["startedAt"] = WorkflowPersistenceTime.ToOffset(row.StartedAt)?.ToString("O"), ["finishedAt"] = WorkflowPersistenceTime.ToOffset(row.FinishedAt)?.ToString("O"),
        }).ToArray());
    }

    /// <summary>在可访问工作流范围内限量列出外部运行摘要及关联 Key、发布版本。</summary>
    public async Task<IReadOnlyList<WorkflowExternalRunSummary>> ListExternalAsync(
        DeviceId deviceId,
        WorkflowId workflowId,
        int limit,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<WorkflowExternalRunRow>(
            """
            SELECT run.run_id, run.workflow_id, run.device_id,
                   run.graph_snapshot::text AS graph_json, run.resumed_from_run_id,
                   run.status, run.error, run.created_at, run.updated_at,
                   run.started_at, run.finished_at, run.trigger_type, run.deployment_id,
                   run.api_key_id, run.request_id, run.idempotency_key, run.request_hash,
                   run.input_json::text AS input_json,
                   api_key.name AS api_key_name, deployment.version AS deployment_version
            FROM workflow_run run
            JOIN workflow_api_key api_key ON api_key.api_key_id = run.api_key_id
            JOIN workflow_deployment deployment ON deployment.deployment_id = run.deployment_id
            WHERE run.workflow_id = @WorkflowId AND run.trigger_type = 'api'
              AND run.device_id IN (SELECT device_id FROM creativeflow_accessible_device_ids(@DeviceId))
            ORDER BY run.created_at DESC
            LIMIT @Limit
            """,
            new ExternalRunListParams
            {
                WorkflowId = workflowId.Value,
                DeviceId = deviceId.Value,
                Limit = limit,
            });
        return rows.Select(row => row.ToSummary()).ToList();
    }

    /// <summary>读取设备可访问的最新交互式运行，可选择仅查询排队或运行中的记录。</summary>
    private async Task<WorkflowRun?> FindOneAsync(
        DeviceId deviceId,
        WorkflowId workflowId,
        bool activeOnly,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var row = await connection.QueryFirstOrDefaultAsync<WorkflowRunRow>(
            $"""
            SELECT {RunColumns}
            FROM workflow_run
            WHERE workflow_id = @WorkflowId
              AND trigger_type = 'interactive'
              AND device_id IN (SELECT device_id FROM creativeflow_accessible_device_ids(@DeviceId))
              AND (@ActiveOnly = false OR status IN ('queued', 'running'))
              AND (@ActiveOnly = true OR input_json->'nodeDebug' IS NULL)
            ORDER BY created_at DESC
            LIMIT 1
            """,
            new WorkflowRunLookupParams
            {
                WorkflowId = workflowId.Value,
                DeviceId = deviceId.Value,
                ActiveOnly = activeOnly,
            });

        return row?.ToDomain();
    }

    /// <summary>按既定稳定顺序读取运行的全部节点状态与输出快照。</summary>
    public async Task<IReadOnlyList<WorkflowNodeRun>> ListNodesAsync(
        WorkflowRunId runId,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<WorkflowNodeRunRow>(
            """
            SELECT run_id, node_id, node_type, status, is_reused, progress,
                   output::text AS output_json, error, started_at, finished_at,
                   attempt, max_attempts, retry_at, skip_reason
            FROM workflow_node_run
            WHERE run_id = @RunId
            ORDER BY started_at NULLS LAST, node_id
            """,
            new WorkflowRunIdParams { RunId = runId.Value });

        return rows.Select(row => row.ToDomain()).ToList();
    }

    /// <summary>在行锁和工作流事务锁内复核并发额度，原子认领最早可执行的排队运行。</summary>
    public async Task<WorkflowRun?> ClaimNextAsync(DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        // 候选筛选跳过已满的工作流；这次计数只是预筛选，不能作为最终并发准入依据。
        var candidate = await connection.QueryFirstOrDefaultAsync<WorkflowRunClaimCandidateRow>(
            """
            SELECT run.run_id
            FROM workflow_run run
            LEFT JOIN workflow_deployment deployment
                ON deployment.deployment_id = run.deployment_id
            WHERE run.status = 'queued'
              AND (
                  run.trigger_type = 'interactive'
                  OR (
                      deployment.deployment_id IS NOT NULL
                      AND pg_try_advisory_xact_lock(
                          hashtextextended('workflow-external-execution:' || run.workflow_id::text, 0))
                      AND (
                          SELECT COUNT(*)
                          FROM workflow_run active
                          WHERE active.workflow_id = run.workflow_id
                            AND active.trigger_type = 'api'
                            AND active.status = 'running'
                      ) < deployment.max_concurrency
                  )
              )
            ORDER BY run.created_at
            LIMIT 1
            FOR UPDATE OF run SKIP LOCKED
            """,
            transaction: transaction);
        if (candidate is null) return null;

        // 锁在独立语句中取得，READ COMMITTED 的下一条语句才能看见前一认领者已提交的运行。
        // 候选行锁与工作流锁一直保留到本次更新提交，取消和其他 Worker 都不能穿过准入检查。
        var row = await connection.QueryFirstOrDefaultAsync<WorkflowRunRow>(
            $"""
            UPDATE workflow_run AS run
            SET status = 'running',
                started_at = COALESCE(run.started_at, @Now),
                updated_at = @Now
            WHERE run.run_id = @RunId AND run.status = 'queued'
              AND (
                  run.trigger_type = 'interactive'
                  OR EXISTS (
                      SELECT 1 FROM workflow_deployment deployment
                      WHERE deployment.deployment_id = run.deployment_id
                        AND (
                            SELECT COUNT(*) FROM workflow_run active
                            WHERE active.workflow_id = run.workflow_id
                              AND active.trigger_type = 'api'
                              AND active.status = 'running'
                        ) < deployment.max_concurrency
                  )
              )
            RETURNING {ReturnedRunColumns}
            """,
            new WorkflowRunClaimUpdateParams { RunId = candidate.RunId, Now = now },
            transaction);

        await transaction.CommitAsync(ct);
        return row?.ToDomain();
    }

    /// <summary>在事务中将重启遗留的运行标为失败，并将其执行中节点置失败、未开始节点置跳过。</summary>
    public async Task<int> FailInterruptedAsync(DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        // 与节点更新、取消采用相同锁序，避免状态日志与批量恢复之间发生锁反转。
        await connection.ExecuteAsync("""
            SELECT run_id FROM workflow_run run
            WHERE status='running' OR (input_json ? 'agui' AND status IN ('succeeded','failed','cancelled')
                AND EXISTS (SELECT 1 FROM workflow_node_run node WHERE node.run_id=run.run_id AND node.status IN ('queued','running','retrying')))
            ORDER BY run_id FOR UPDATE
            """, transaction: transaction);

        await connection.ExecuteAsync(
            """
            UPDATE workflow_node_run
            SET status = CASE WHEN status IN ('running','retrying') THEN 'failed' ELSE 'skipped' END,
                error = CASE WHEN status IN ('running','retrying')
                             THEN 'The server restarted while this node was running.'
                             ELSE error
                        END,
                finished_at = @Now
            WHERE status IN ('queued', 'running', 'retrying')
              AND run_id IN (SELECT run_id FROM workflow_run WHERE status = 'running')
            """,
            new WorkflowRunClaimParams { Now = now },
            transaction);

        var affected = await connection.ExecuteAsync(
            """
            UPDATE workflow_run
            SET status = 'failed',
                error = 'The server restarted while the workflow was running. Continue the failed run or run it again from the beginning.',
                updated_at = @Now,
                finished_at = @Now
            WHERE status = 'running'
            """,
            new WorkflowRunClaimParams { Now = now },
            transaction);

        // 若崩溃发生在主运行终态写入与节点收尾之间，AG-UI 日志不能永远等不到完成标记。
        // 这里只补齐已终止运行的节点，不恢复执行，不重新调用任何服务商；普通运行语义不变。
        await connection.ExecuteAsync("""
            UPDATE workflow_node_run node
            SET status = CASE WHEN run.status='cancelled' THEN 'cancelled'
                              WHEN node.status IN ('running','retrying') THEN 'failed' ELSE 'skipped' END,
                error = CASE WHEN node.status IN ('running','retrying') AND run.status<>'cancelled'
                             THEN COALESCE(node.error,'The server restarted before this node was finalized.') ELSE node.error END,
                finished_at = COALESCE(node.finished_at,@Now)
            FROM workflow_run run
            WHERE node.run_id=run.run_id AND run.input_json ? 'agui'
              AND run.status IN ('succeeded','failed','cancelled') AND node.status IN ('queued','running','retrying')
            """, new WorkflowRunClaimParams { Now = now }, transaction);

        await transaction.CommitAsync(ct);
        return affected;
    }

    /// <summary>更新仍未取消的工作流运行，取消或删除已赢得竞争时返回 false。</summary>
    public async Task<bool> UpdateAsync(WorkflowRun run, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var affected = await connection.ExecuteAsync(
            """
            UPDATE workflow_run
            SET status = @Status,
                error = @Error,
                updated_at = @UpdatedAt,
                started_at = @StartedAt,
                finished_at = @FinishedAt
            WHERE run_id = @RunId
              AND status <> 'cancelled'
            """,
            WorkflowRunWriteParams.From(run));

        return affected > 0;
    }

    /// <summary>更新仍未取消的工作流节点状态和结果，避免晚到成功覆盖取消。</summary>
    public async Task<bool> UpdateNodeAsync(WorkflowNodeRun node, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteAsync("SELECT run_id FROM workflow_run WHERE run_id=@RunId FOR UPDATE",
            new WorkflowRunIdParams { RunId = node.RunId.Value }, transaction);
        var affected = await connection.ExecuteAsync(
            """
            UPDATE workflow_node_run
            SET status = @Status,
                is_reused = @IsReused,
                progress = @Progress,
                output = @OutputJson::jsonb,
                error = @Error,
                started_at = @StartedAt,
                finished_at = @FinishedAt,
                attempt = @Attempt,
                max_attempts = @MaxAttempts,
                retry_at = @RetryAt,
                skip_reason = @SkipReason
            WHERE run_id = @RunId
              AND node_id = @NodeId
              AND status <> 'cancelled'
            """,
            WorkflowNodeRunWriteParams.From(node), transaction);

        await transaction.CommitAsync(ct);

        return affected > 0;
    }

    /// <summary>保存节点一次尝试，重复写入同一序号只更新最后状态。</summary>
    public async Task RecordNodeAttemptAsync(WorkflowNodeAttempt attempt, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await connection.ExecuteAsync(
            """
            INSERT INTO workflow_node_attempt
                (run_id, node_id, attempt, status, error, started_at, finished_at, retry_at)
            VALUES (@RunId, @NodeId, @Attempt, @Status, @Error, @StartedAt, @FinishedAt, @RetryAt)
            ON CONFLICT (run_id, node_id, attempt) DO UPDATE SET
                status = EXCLUDED.status, error = EXCLUDED.error,
                started_at = EXCLUDED.started_at, finished_at = EXCLUDED.finished_at,
                retry_at = EXCLUDED.retry_at
            """,
            WorkflowNodeAttemptWriteParams.From(attempt));
    }

    /// <summary>查询工作流是否已取消或被删除，任一情况都要求已认领执行停止。</summary>
    public async Task<bool> IsCancellationRequestedAsync(
        WorkflowRunId runId,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var status = await connection.ExecuteScalarAsync<string?>(
            "SELECT status FROM workflow_run WHERE run_id = @RunId",
            new WorkflowRunIdParams { RunId = runId.Value });

        // 删除数据行时也必须停止已认领工作；否则删除与启动的竞争可能让孤立的供应商请求
        // 在工作流被移除后仍继续运行。
        return status is null || string.Equals(status, "cancelled", StringComparison.Ordinal);
    }

    /// <summary>在可访问范围内原子取消排队或运行中的工作流及尚未结束的节点。</summary>
    public async Task<bool> CancelAsync(
        DeviceId deviceId,
        WorkflowId workflowId,
        WorkflowRunId runId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        // 仅排队或运行中的记录可以转为取消；主运行与尚未完成节点必须在同一事务内更新。
        await using var connection = await connections.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var affected = await connection.ExecuteAsync(
            """
            UPDATE workflow_run
            SET status = 'cancelled', updated_at = @Now, finished_at = @Now
            WHERE run_id = @RunId
              AND workflow_id = @WorkflowId
              AND device_id IN (SELECT device_id FROM creativeflow_accessible_device_ids(@DeviceId))
              AND status IN ('queued', 'running', 'retrying')
            """,
            new WorkflowRunCancelParams
            {
                RunId = runId.Value,
                WorkflowId = workflowId.Value,
                DeviceId = deviceId.Value,
                Now = now,
            },
            transaction);

        if (affected > 0)
        {
            await connection.ExecuteAsync(
                """
                UPDATE workflow_node_run
                SET status = 'cancelled', finished_at = @Now, retry_at = NULL
                WHERE run_id = @RunId AND status IN ('queued', 'running', 'retrying')
                """,
                new WorkflowRunCancelNodesParams { RunId = runId.Value, Now = now },
                transaction);
        }

        await transaction.CommitAsync(ct);
        return affected > 0;
    }

    /// <summary>仅在指定工作流的 API 触发范围内原子取消活动运行和未结束节点。</summary>
    public async Task<bool> CancelExternalAsync(
        WorkflowId workflowId,
        WorkflowRunId runId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        // 外部入口仅能取消 API 触发且属于指定工作流的活动运行；节点取消与主运行一起提交。
        await using var connection = await connections.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var affected = await connection.ExecuteAsync(
            """
            UPDATE workflow_run
            SET status = 'cancelled', updated_at = @Now, finished_at = @Now
            WHERE run_id = @RunId AND workflow_id = @WorkflowId
              AND trigger_type = 'api' AND status IN ('queued', 'running', 'retrying')
            """,
            new ExternalRunCancelParams
            {
                RunId = runId.Value,
                WorkflowId = workflowId.Value,
                Now = now,
            },
            transaction);

        if (affected > 0)
        {
            await connection.ExecuteAsync(
                """
                UPDATE workflow_node_run
                SET status = 'cancelled', finished_at = @Now, retry_at = NULL
                WHERE run_id = @RunId AND status IN ('queued', 'running', 'retrying')
                """,
                new WorkflowRunCancelNodesParams { RunId = runId.Value, Now = now },
                transaction);
        }

        await transaction.CommitAsync(ct);
        return affected > 0;
    }

    /// <summary>将指定运行仍处于排队状态的节点置为 skipped，并记录完成时间。</summary>
    public async Task SkipRemainingNodesAsync(
        WorkflowRunId runId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteAsync("SELECT run_id FROM workflow_run WHERE run_id=@RunId FOR UPDATE",
            new WorkflowRunIdParams { RunId = runId.Value }, transaction);
        await connection.ExecuteAsync(
            """
            UPDATE workflow_node_run
            SET status = 'skipped', finished_at = @Now, skip_reason = 'run stopped before node execution'
            WHERE run_id = @RunId AND status IN ('queued', 'retrying')
            """,
            new WorkflowRunCancelNodesParams { RunId = runId.Value, Now = now }, transaction);
        await transaction.CommitAsync(ct);
    }

    /// <summary>在运行锁内跳过一个尚未完成节点，拒绝覆盖已经成功或正在执行的外部调用。</summary>
    public async Task<bool> SkipNodeAsync(
        DeviceId deviceId,
        WorkflowId workflowId,
        WorkflowRunId runId,
        string nodeId,
        string reason,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteAsync("SELECT run_id FROM workflow_run WHERE run_id=@RunId FOR UPDATE",
            new WorkflowRunCancelNodesParams { RunId = runId.Value, Now = now }, transaction);
        var affected = await connection.ExecuteAsync(
            """
            UPDATE workflow_node_run node
            SET status='skipped', finished_at=@Now, retry_at=NULL,
                skip_reason=@Reason, error=NULL
            FROM workflow_run run
            WHERE node.run_id=@RunId AND run.run_id=node.run_id
              AND run.workflow_id=@WorkflowId
              AND run.device_id IN (SELECT device_id FROM creativeflow_accessible_device_ids(@DeviceId))
              AND run.status IN ('queued','running','failed')
              AND node.node_id=@NodeId AND node.status IN ('queued','failed','retrying')
            """,
            new WorkflowNodeSkipParams
            {
                RunId = runId.Value, WorkflowId = workflowId.Value, DeviceId = deviceId.Value,
                NodeId = nodeId, Reason = reason, Now = now,
            }, transaction);
        await transaction.CommitAsync(ct);
        return affected > 0;
    }
}

internal sealed class WorkflowRunIdParams
{
    /// <summary>工作流或 Agent 运行的唯一标识。</summary>
    public Guid RunId { get; init; }
}

internal sealed class WorkflowRunLookupParams
{
    /// <summary>关联工作流的唯一标识。</summary>
    public Guid WorkflowId { get; init; }
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; }
    /// <summary>查询是否只返回活跃状态的记录。</summary>
    public bool ActiveOnly { get; init; }
}

internal sealed class WorkflowRunScopeParams
{
    /// <summary>工作流或 Agent 运行的唯一标识。</summary>
    public Guid RunId { get; init; }
    /// <summary>关联工作流的唯一标识。</summary>
    public Guid WorkflowId { get; init; }
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; }
}

internal sealed class WorkflowRunClaimCandidateRow
{
    /// <summary>本次候选运行的标识。</summary>
    public Guid RunId { get; init; }
}

internal sealed class WorkflowRunClaimParams
{
    /// <summary>本次操作使用的时间基准。</summary>
    public DateTimeOffset Now { get; init; }
}

internal sealed class WorkflowRunClaimUpdateParams
{
    /// <summary>已取得行锁的候选运行。</summary>
    public Guid RunId { get; init; }
    /// <summary>本次认领运行的时间。</summary>
    public DateTimeOffset Now { get; init; }
}

internal sealed class WorkflowRunCancelParams
{
    /// <summary>工作流或 Agent 运行的唯一标识。</summary>
    public Guid RunId { get; init; }
    /// <summary>关联工作流的唯一标识。</summary>
    public Guid WorkflowId { get; init; }
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; }
    /// <summary>本次操作使用的时间基准。</summary>
    public DateTimeOffset Now { get; init; }
}

internal sealed class WorkflowRunCancelNodesParams
{
    /// <summary>工作流或 Agent 运行的唯一标识。</summary>
    public Guid RunId { get; init; }
    /// <summary>本次操作使用的时间基准。</summary>
    public DateTimeOffset Now { get; init; }
}

internal sealed class ExternalRunScopeParams
{
    /// <summary>工作流或 Agent 运行的唯一标识。</summary>
    public Guid RunId { get; init; }
    /// <summary>关联工作流的唯一标识。</summary>
    public Guid WorkflowId { get; init; }
}

internal sealed class IdempotencyLookupParams
{
    /// <summary>工作流 API Key 的标识。</summary>
    public Guid ApiKeyId { get; init; }
    /// <summary>调用方提供的幂等键，与 API Key 共同确定重放范围。</summary>
    public string IdempotencyKey { get; init; } = string.Empty;
}

internal sealed class ExternalRunListParams
{
    /// <summary>关联工作流的唯一标识。</summary>
    public Guid WorkflowId { get; init; }
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; }
    /// <summary>本次查询或处理允许返回的最大数量。</summary>
    public int Limit { get; init; }
}

internal sealed class WorkflowOnlyRunParams
{
    /// <summary>关联工作流的唯一标识。</summary>
    public Guid WorkflowId { get; init; }
}

internal sealed class ExternalRunCancelParams
{
    /// <summary>工作流或 Agent 运行的唯一标识。</summary>
    public Guid RunId { get; init; }
    /// <summary>关联工作流的唯一标识。</summary>
    public Guid WorkflowId { get; init; }
    /// <summary>本次操作使用的时间基准。</summary>
    public DateTimeOffset Now { get; init; }
}

internal sealed class WorkflowNodeSkipParams
{
    public Guid RunId { get; init; }
    public Guid WorkflowId { get; init; }
    public Guid DeviceId { get; init; }
    public string NodeId { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public DateTimeOffset Now { get; init; }
}

internal sealed class WorkflowRunWriteParams
{
    /// <summary>工作流或 Agent 运行的唯一标识。</summary>
    public Guid RunId { get; init; }
    /// <summary>关联工作流的唯一标识。</summary>
    public Guid WorkflowId { get; init; }
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; }
    /// <summary>按现有 schema 保存的完整图 JSON 快照。</summary>
    public string GraphJson { get; init; } = "{}";
    /// <summary>当前续跑所基于的原运行标识。</summary>
    public Guid? ResumedFromRunId { get; init; }
    /// <summary>当前对象的业务状态，存储和接口名称保持既有映射。</summary>
    public string Status { get; init; } = "queued";
    /// <summary>失败详情或错误分类，具体格式沿用所属契约。</summary>
    public string? Error { get; init; }
    /// <summary>记录创建时间，沿用当前 UTC 与序列化约定。</summary>
    public DateTimeOffset CreatedAt { get; init; }
    /// <summary>记录最近一次修改的 UTC 时间。</summary>
    public DateTimeOffset UpdatedAt { get; init; }
    /// <summary>运行、节点或工具开始执行的时间。</summary>
    public DateTimeOffset? StartedAt { get; init; }
    /// <summary>运行、节点或工具调用到达终止状态的时间。</summary>
    public DateTimeOffset? FinishedAt { get; init; }
    /// <summary>运行由交互界面还是外部 API 触发。</summary>
    public string TriggerType { get; init; } = "interactive";
    /// <summary>工作流发布版本的唯一标识。</summary>
    public Guid? DeploymentId { get; init; }
    /// <summary>工作流 API Key 的标识。</summary>
    public Guid? ApiKeyId { get; init; }
    /// <summary>一次外部 API 请求的关联标识。</summary>
    public string? RequestId { get; init; }
    /// <summary>调用方提供的幂等键，与 API Key 共同确定重放范围。</summary>
    public string? IdempotencyKey { get; init; }
    /// <summary>规范化输入与发布版本计算出的摘要，用于判断幂等冲突。</summary>
    public byte[]? RequestHash { get; init; }
    /// <summary>执行输入的 JSON 快照。</summary>
    public string? InputJson { get; init; }

    /// <summary>把业务记录投影为当前存储操作的静态参数。</summary>
    public static WorkflowRunWriteParams From(WorkflowRun run) => new()
    {
        RunId = run.Id.Value,
        WorkflowId = run.WorkflowId.Value,
        DeviceId = run.DeviceId.Value,
        GraphJson = run.GraphJson,
        ResumedFromRunId = run.ResumedFromRunId?.Value,
        Status = WorkflowRunStatusNames.ToWire(run.Status),
        Error = run.Error,
        CreatedAt = run.CreatedAt,
        UpdatedAt = run.UpdatedAt,
        StartedAt = run.StartedAt,
        FinishedAt = run.FinishedAt,
        TriggerType = run.TriggerType == WorkflowRunTriggerType.Api ? "api" : "interactive",
        DeploymentId = run.DeploymentId?.Value,
        ApiKeyId = run.ApiKeyId?.Value,
        RequestId = run.RequestId,
        IdempotencyKey = run.IdempotencyKey,
        RequestHash = run.RequestHash,
        InputJson = run.InputJson,
    };
}

internal sealed class WorkflowNodeRunWriteParams
{
    /// <summary>工作流或 Agent 运行的唯一标识。</summary>
    public Guid RunId { get; init; }
    /// <summary>节点图中的稳定节点标识。</summary>
    public string NodeId { get; init; } = string.Empty;
    /// <summary>节点图中既有的节点类型名称。</summary>
    public string NodeType { get; init; } = string.Empty;
    /// <summary>当前对象的业务状态，存储和接口名称保持既有映射。</summary>
    public string Status { get; init; } = "queued";
    /// <summary>本次运行是否复用既有成功节点的输出。</summary>
    public bool IsReused { get; init; }
    /// <summary>现有协议中的任务完成比例，可空时表示未知。</summary>
    public double? Progress { get; init; }
    /// <summary>按现有格式保存的节点或工具输出 JSON。</summary>
    public string? OutputJson { get; init; }
    /// <summary>失败详情或错误分类，具体格式沿用所属契约。</summary>
    public string? Error { get; init; }
    /// <summary>运行、节点或工具开始执行的时间。</summary>
    public DateTimeOffset? StartedAt { get; init; }
    /// <summary>运行、节点或工具调用到达终止状态的时间。</summary>
    public DateTimeOffset? FinishedAt { get; init; }
    /// <summary>本次节点执行尝试序号。</summary>
    public int Attempt { get; init; } = 1;
    /// <summary>节点允许的最大尝试次数。</summary>
    public int MaxAttempts { get; init; } = 1;
    /// <summary>下一次自动重试时间。</summary>
    public DateTimeOffset? RetryAt { get; init; }
    /// <summary>跳过原因。</summary>
    public string? SkipReason { get; init; }

    /// <summary>把业务记录投影为当前存储操作的静态参数。</summary>
    public static WorkflowNodeRunWriteParams From(WorkflowNodeRun node) => new()
    {
        RunId = node.RunId.Value,
        NodeId = node.NodeId,
        NodeType = node.NodeType,
        Status = WorkflowRunStatusNames.ToWire(node.Status),
        IsReused = node.IsReused,
        Progress = node.Progress,
        OutputJson = node.OutputJson,
        Error = node.Error,
        StartedAt = node.StartedAt,
        FinishedAt = node.FinishedAt,
        Attempt = node.Attempt,
        MaxAttempts = node.MaxAttempts,
        RetryAt = node.RetryAt,
        SkipReason = node.SkipReason,
    };
}

internal sealed class WorkflowNodeAttemptWriteParams
{
    public Guid RunId { get; init; }
    public string NodeId { get; init; } = string.Empty;
    public int Attempt { get; init; }
    public string Status { get; init; } = "running";
    public string? Error { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public DateTimeOffset? RetryAt { get; init; }

    public static WorkflowNodeAttemptWriteParams From(WorkflowNodeAttempt attempt) => new()
    {
        RunId = attempt.RunId.Value,
        NodeId = attempt.NodeId,
        Attempt = attempt.Attempt,
        Status = WorkflowRunStatusNames.ToWire(attempt.Status),
        Error = attempt.Error,
        StartedAt = attempt.StartedAt,
        FinishedAt = attempt.FinishedAt,
        RetryAt = attempt.RetryAt,
    };
}

internal class WorkflowRunRow
{
    /// <summary>工作流或 Agent 运行的唯一标识。</summary>
    public Guid RunId { get; init; }
    /// <summary>关联工作流的唯一标识。</summary>
    public Guid WorkflowId { get; init; }
    /// <summary>资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</summary>
    public Guid DeviceId { get; init; }
    /// <summary>按现有 schema 保存的完整图 JSON 快照。</summary>
    public string GraphJson { get; init; } = "{}";
    /// <summary>当前续跑所基于的原运行标识。</summary>
    public Guid? ResumedFromRunId { get; init; }
    /// <summary>当前对象的业务状态，存储和接口名称保持既有映射。</summary>
    public string Status { get; init; } = "queued";
    /// <summary>失败详情或错误分类，具体格式沿用所属契约。</summary>
    public string? Error { get; init; }
    /// <summary>记录创建时间，沿用当前 UTC 与序列化约定。</summary>
    public DateTime CreatedAt { get; init; }
    /// <summary>记录最近一次修改的 UTC 时间。</summary>
    public DateTime UpdatedAt { get; init; }
    /// <summary>运行、节点或工具开始执行的时间。</summary>
    public DateTime? StartedAt { get; init; }
    /// <summary>运行、节点或工具调用到达终止状态的时间。</summary>
    public DateTime? FinishedAt { get; init; }
    /// <summary>运行由交互界面还是外部 API 触发。</summary>
    public string TriggerType { get; init; } = "interactive";
    /// <summary>工作流发布版本的唯一标识。</summary>
    public Guid? DeploymentId { get; init; }
    /// <summary>工作流 API Key 的标识。</summary>
    public Guid? ApiKeyId { get; init; }
    /// <summary>一次外部 API 请求的关联标识。</summary>
    public string? RequestId { get; init; }
    /// <summary>调用方提供的幂等键，与 API Key 共同确定重放范围。</summary>
    public string? IdempotencyKey { get; init; }
    /// <summary>规范化输入与发布版本计算出的摘要，用于判断幂等冲突。</summary>
    public byte[]? RequestHash { get; init; }
    /// <summary>执行输入的 JSON 快照。</summary>
    public string? InputJson { get; init; }

    /// <summary>把数据库静态行映射为业务记录，保留既有状态和时间语义。</summary>
    public WorkflowRun ToDomain() => new WorkflowRun(
        new WorkflowRunId(RunId),
        new WorkflowId(WorkflowId),
        new DeviceId(DeviceId),
        GraphJson,
        ResumedFromRunId is { } sourceId ? new WorkflowRunId(sourceId) : null,
        WorkflowRunStatusNames.ParseRun(Status),
        Error,
        WorkflowPersistenceTime.ToOffset(CreatedAt),
        WorkflowPersistenceTime.ToOffset(UpdatedAt),
        WorkflowPersistenceTime.ToOffset(StartedAt),
        WorkflowPersistenceTime.ToOffset(FinishedAt))
    {
        TriggerType = TriggerType == "api"
            ? WorkflowRunTriggerType.Api
            : WorkflowRunTriggerType.Interactive,
        DeploymentId = DeploymentId is { } deploymentId
            ? new WorkflowDeploymentId(deploymentId)
            : null,
        ApiKeyId = ApiKeyId is { } apiKeyId ? new WorkflowApiKeyId(apiKeyId) : null,
        RequestId = RequestId,
        IdempotencyKey = IdempotencyKey,
        RequestHash = RequestHash,
        InputJson = InputJson,
    };
}

internal sealed class WorkflowExternalRunRow : WorkflowRunRow
{
    /// <summary>工作流 API Key 的显示名称。</summary>
    public string ApiKeyName { get; init; } = string.Empty;
    /// <summary>运行关联的发布版本号。</summary>
    public int DeploymentVersion { get; init; }

    /// <summary>将持久记录投影为列表摘要。</summary>
    public WorkflowExternalRunSummary ToSummary() =>
        new(base.ToDomain(), ApiKeyName, DeploymentVersion);
}

internal sealed class WorkflowNodeRunRow
{
    /// <summary>工作流或 Agent 运行的唯一标识。</summary>
    public Guid RunId { get; init; }
    /// <summary>节点图中的稳定节点标识。</summary>
    public string NodeId { get; init; } = string.Empty;
    /// <summary>节点图中既有的节点类型名称。</summary>
    public string NodeType { get; init; } = string.Empty;
    /// <summary>当前对象的业务状态，存储和接口名称保持既有映射。</summary>
    public string Status { get; init; } = "queued";
    /// <summary>本次运行是否复用既有成功节点的输出。</summary>
    public bool IsReused { get; init; }
    /// <summary>现有协议中的任务完成比例，可空时表示未知。</summary>
    public double? Progress { get; init; }
    /// <summary>按现有格式保存的节点或工具输出 JSON。</summary>
    public string? OutputJson { get; init; }
    /// <summary>失败详情或错误分类，具体格式沿用所属契约。</summary>
    public string? Error { get; init; }
    /// <summary>运行、节点或工具开始执行的时间。</summary>
    public DateTime? StartedAt { get; init; }
    /// <summary>运行、节点或工具调用到达终止状态的时间。</summary>
    public DateTime? FinishedAt { get; init; }
    /// <summary>本次节点执行尝试序号。</summary>
    public int Attempt { get; init; } = 1;
    /// <summary>节点允许的最大尝试次数。</summary>
    public int MaxAttempts { get; init; } = 1;
    /// <summary>下一次自动重试时间。</summary>
    public DateTime? RetryAt { get; init; }
    /// <summary>跳过原因。</summary>
    public string? SkipReason { get; init; }

    /// <summary>把数据库静态行映射为业务记录，保留既有状态和时间语义。</summary>
    public WorkflowNodeRun ToDomain() => new(
        new WorkflowRunId(RunId),
        NodeId,
        NodeType,
        WorkflowRunStatusNames.ParseNode(Status),
        IsReused,
        Progress,
        OutputJson,
        Error,
        WorkflowPersistenceTime.ToOffset(StartedAt),
        WorkflowPersistenceTime.ToOffset(FinishedAt))
    {
        Attempt = Attempt,
        MaxAttempts = MaxAttempts,
        RetryAt = WorkflowPersistenceTime.ToOffset(RetryAt),
        SkipReason = SkipReason,
    };
}

internal static class WorkflowRunStatusNames
{
    /// <summary>将业务枚举映射为既有存储或接口字符串。</summary>
    public static string ToWire(WorkflowRunStatus status) => status switch
    {
        WorkflowRunStatus.Queued => "queued",
        WorkflowRunStatus.Running => "running",
        WorkflowRunStatus.Succeeded => "succeeded",
        WorkflowRunStatus.Failed => "failed",
        _ => "cancelled",
    };

    /// <summary>将业务枚举映射为既有存储或接口字符串。</summary>
    public static string ToWire(WorkflowNodeRunStatus status) => status switch
    {
        WorkflowNodeRunStatus.Queued => "queued",
        WorkflowNodeRunStatus.Running => "running",
        WorkflowNodeRunStatus.Retrying => "retrying",
        WorkflowNodeRunStatus.Succeeded => "succeeded",
        WorkflowNodeRunStatus.Failed => "failed",
        WorkflowNodeRunStatus.Cancelled => "cancelled",
        _ => "skipped",
    };

    /// <summary>将运行状态的持久化字符串还原为枚举。</summary>
    public static WorkflowRunStatus ParseRun(string status) => status switch
    {
        "queued" => WorkflowRunStatus.Queued,
        "running" => WorkflowRunStatus.Running,
        "succeeded" => WorkflowRunStatus.Succeeded,
        "failed" => WorkflowRunStatus.Failed,
        _ => WorkflowRunStatus.Cancelled,
    };

    /// <summary>将节点状态的持久化字符串还原为业务枚举。</summary>
    public static WorkflowNodeRunStatus ParseNode(string status) => status switch
    {
        "queued" => WorkflowNodeRunStatus.Queued,
        "running" => WorkflowNodeRunStatus.Running,
        "retrying" => WorkflowNodeRunStatus.Retrying,
        "succeeded" => WorkflowNodeRunStatus.Succeeded,
        "failed" => WorkflowNodeRunStatus.Failed,
        "cancelled" => WorkflowNodeRunStatus.Cancelled,
        _ => WorkflowNodeRunStatus.Skipped,
    };
}
