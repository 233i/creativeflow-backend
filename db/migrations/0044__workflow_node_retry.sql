-- 节点尝试和自动重试是运行状态的一部分，不复制请求正文或敏感配置。
ALTER TABLE workflow_node_run DROP CONSTRAINT IF EXISTS ck_workflow_node_run_status;
ALTER TABLE workflow_node_run ADD CONSTRAINT ck_workflow_node_run_status
    CHECK (status IN ('queued', 'running', 'retrying', 'succeeded', 'failed', 'cancelled', 'skipped'));
ALTER TABLE workflow_node_run ADD COLUMN IF NOT EXISTS attempt integer NOT NULL DEFAULT 1;
ALTER TABLE workflow_node_run ADD COLUMN IF NOT EXISTS max_attempts integer NOT NULL DEFAULT 1;
ALTER TABLE workflow_node_run ADD COLUMN IF NOT EXISTS retry_at timestamptz NULL;
ALTER TABLE workflow_node_run ADD COLUMN IF NOT EXISTS skip_reason text NULL;
ALTER TABLE workflow_node_run DROP CONSTRAINT IF EXISTS ck_workflow_node_attempt_bounds;
ALTER TABLE workflow_node_run ADD CONSTRAINT ck_workflow_node_attempt_bounds
    CHECK (attempt BETWEEN 1 AND 20 AND max_attempts BETWEEN 1 AND 20 AND attempt <= max_attempts);
ALTER TABLE workflow_node_run DROP CONSTRAINT IF EXISTS ck_workflow_node_skip_reason_length;
ALTER TABLE workflow_node_run ADD CONSTRAINT ck_workflow_node_skip_reason_length
    CHECK (skip_reason IS NULL OR char_length(skip_reason) <= 1000);

CREATE TABLE IF NOT EXISTS workflow_node_attempt (
    run_id      uuid NOT NULL REFERENCES workflow_run(run_id) ON DELETE CASCADE,
    node_id     text NOT NULL,
    attempt     integer NOT NULL CHECK (attempt BETWEEN 1 AND 20),
    status      text NOT NULL CHECK (status IN ('queued', 'running', 'retrying', 'succeeded', 'failed', 'cancelled', 'skipped')),
    error       text NULL,
    started_at  timestamptz NULL,
    finished_at timestamptz NULL,
    retry_at    timestamptz NULL,
    created_at  timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (run_id, node_id, attempt)
);
CREATE INDEX IF NOT EXISTS ix_workflow_node_attempt_run ON workflow_node_attempt(run_id, node_id, attempt);

-- AG-UI 节点事件必须反映 retrying 和尝试序号，否则客户端无法解释重试中的节点。
CREATE OR REPLACE FUNCTION sol_finish_workflow_events(p_run uuid) RETURNS void
LANGUAGE plpgsql AS $$
DECLARE r workflow_run;
BEGIN
    SELECT * INTO r FROM workflow_run WHERE run_id=p_run AND input_json ? 'agui' FOR UPDATE;
    IF NOT FOUND OR r.status NOT IN ('succeeded','failed','cancelled') THEN RETURN; END IF;
    IF EXISTS (SELECT 1 FROM workflow_node_run WHERE run_id=p_run AND status IN ('queued','running','retrying'))
        OR EXISTS (SELECT 1 FROM workflow_run_event WHERE run_id=p_run AND kind='run.finished') THEN RETURN; END IF;
    PERFORM sol_append_workflow_event(p_run,'run.finished',jsonb_build_object('status',r.status,'error',r.error));
END;
$$;

CREATE OR REPLACE FUNCTION sol_journal_workflow_node() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    node_name text;
BEGIN
    IF TG_OP='UPDATE' AND (NEW.status,NEW.progress,NEW.error,NEW.is_reused,NEW.started_at,NEW.finished_at,NEW.attempt,NEW.max_attempts,NEW.retry_at,NEW.skip_reason)
        IS NOT DISTINCT FROM (OLD.status,OLD.progress,OLD.error,OLD.is_reused,OLD.started_at,OLD.finished_at,OLD.attempt,OLD.max_attempts,OLD.retry_at,OLD.skip_reason)
        THEN RETURN NEW; END IF;
    SELECT COALESCE(
        NULLIF(node->'data'->>'title', ''),
        NULLIF(node->'data'->>'label', ''),
        NULLIF(node->>'name', ''),
        CASE node->>'type'
            WHEN 'start' THEN '开始' WHEN 'end' THEN '结束' WHEN 'text' THEN '文本输入' WHEN 'image' THEN '图片输入'
            WHEN 'llm' THEN 'LLM' WHEN 'knowledgeRetrieve' THEN '知识检索' WHEN 'knowledgeAnswer' THEN '知识问答'
            WHEN 'parameterExtract' THEN '参数提取' WHEN 'intentClassify' THEN '意图分类' WHEN 'transform' THEN '变量处理'
            WHEN 'loop' THEN '循环' WHEN 'batch' THEN '批处理' WHEN 'condition' THEN '条件分支' WHEN 'merge' THEN '结果汇合'
            WHEN 'httpRequest' THEN 'HTTP 请求' WHEN 'imageGenerate' THEN '图片生成' WHEN 'videoGenerate' THEN '视频生成'
            WHEN 'videoExtend' THEN '视频延长' WHEN 'videoFrame' THEN '提取尾帧' WHEN 'imageOutput' THEN '图片结果'
            WHEN 'videoOutput' THEN '视频结果' WHEN 'output' THEN '输出' ELSE NULLIF(node->>'type', '') END,
        NULLIF(NEW.node_type, ''), NEW.node_id)
    INTO node_name
    FROM workflow_run run
    LEFT JOIN LATERAL jsonb_array_elements(
        CASE WHEN jsonb_typeof(run.graph_snapshot->'nodes') = 'array'
             THEN run.graph_snapshot->'nodes' ELSE '[]'::jsonb END) node ON TRUE
    WHERE run.run_id=NEW.run_id AND node->>'id'=NEW.node_id
    LIMIT 1;
    node_name := COALESCE(node_name, NULLIF(NEW.node_type, ''), NEW.node_id);
    PERFORM sol_append_workflow_event(NEW.run_id,'node.state',jsonb_build_object(
        'id',NEW.node_id,'name',node_name,'type',NEW.node_type,'status',NEW.status,'progress',NEW.progress,
        'isReused',NEW.is_reused,'attempt',NEW.attempt,'maxAttempts',NEW.max_attempts,'retryAt',NEW.retry_at,
        'skipReason',NEW.skip_reason,'error',NEW.error,'startedAt',NEW.started_at,'finishedAt',NEW.finished_at));
    RETURN NEW;
END;
$$;
