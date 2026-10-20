-- 数据转换由 HttpCredentialConsolidation 在同一事务中先执行；直接执行 SQL 不得跳过引用检查。
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM workflow WHERE graph @> '{"nodes":[{"type":"externalCall"}]}')
       OR EXISTS (SELECT 1 FROM workflow_template WHERE graph @> '{"nodes":[{"type":"externalCall"}]}')
       OR EXISTS (SELECT 1 FROM workflow_deployment WHERE graph_snapshot @> '{"nodes":[{"type":"externalCall"}]}' OR execution_snapshot ? 'external')
       OR EXISTS (SELECT 1 FROM workflow_run WHERE graph_snapshot @> '{"nodes":[{"type":"externalCall"}]}' OR input_json ? 'external')
       OR EXISTS (SELECT 1 FROM workflow_node_run WHERE node_type='externalCall')
       OR EXISTS (SELECT 1 FROM workflow_http_operation o WHERE NOT EXISTS(SELECT 1 FROM workflow_http_credential c WHERE c.id=o.id))
       OR EXISTS (SELECT 1 FROM workflow_http_revision r WHERE NOT EXISTS(SELECT 1 FROM workflow_http_credential_revision c WHERE c.id=r.id))
       OR EXISTS (SELECT 1 FROM workflow_http_grant g WHERE NOT EXISTS(SELECT 1 FROM workflow_http_credential_grant c WHERE to_jsonb(c)-'credential_id'=to_jsonb(g)-'operation_id' AND c.credential_id=g.operation_id))
       OR EXISTS (SELECT 1 FROM workflow_http_grant_audit g WHERE NOT EXISTS(SELECT 1 FROM workflow_http_credential_grant_audit c WHERE to_jsonb(c)-'credential_id'=to_jsonb(g)-'operation_id' AND c.credential_id=g.operation_id)) THEN
       RAISE EXCEPTION 'Legacy HTTP references remain. Run the application data migration before retiring operation tables.';
    END IF;
END $$;
CREATE TABLE IF NOT EXISTS workflow_http_consolidation_audit (
    version integer PRIMARY KEY,
    summary jsonb NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
-- 只保留防重复的 Key/幂等键，不保留旧输入、输出或旧执行引擎。
CREATE TABLE IF NOT EXISTS workflow_run_retired_idempotency (
    api_key_id uuid NOT NULL REFERENCES workflow_api_key(api_key_id) ON DELETE CASCADE,
    idempotency_key text NOT NULL,
    retired_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY(api_key_id,idempotency_key)
);
CREATE FUNCTION sol_prevent_retired_workflow_replay() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.trigger_type='api' AND EXISTS (
        SELECT 1 FROM workflow_run_retired_idempotency
        WHERE api_key_id=NEW.api_key_id AND idempotency_key=NEW.idempotency_key
    ) THEN
        RAISE EXCEPTION 'The idempotency key belongs to a retired run.'
            USING ERRCODE='23505', CONSTRAINT='uq_workflow_run_idempotency';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER workflow_retired_idempotency_guard BEFORE INSERT ON workflow_run
    FOR EACH ROW EXECUTE FUNCTION sol_prevent_retired_workflow_replay();
DROP TABLE workflow_http_grant_audit;
DROP TABLE workflow_http_grant;
ALTER TABLE workflow_http_operation DROP CONSTRAINT fk_workflow_http_current_revision;
DROP TABLE workflow_http_revision;
DROP TABLE workflow_http_operation;
