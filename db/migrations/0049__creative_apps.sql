-- Public media applications have their own publication channel. API deployments are unchanged.
CREATE TABLE creative_app (
    workflow_id uuid PRIMARY KEY REFERENCES workflow(workflow_id),
    active_release_id uuid NULL,
    updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE creative_app_release (
    release_id uuid PRIMARY KEY,
    workflow_id uuid NOT NULL REFERENCES creative_app(workflow_id),
    version integer NOT NULL,
    metadata jsonb NOT NULL,
    graph_snapshot jsonb NOT NULL,
    input_schema jsonb NOT NULL,
    output_schema jsonb NOT NULL,
    max_concurrency integer NOT NULL CHECK (max_concurrency BETWEEN 1 AND 32),
    user_concurrency integer NOT NULL CHECK (user_concurrency BETWEEN 1 AND 8),
    daily_limit integer NOT NULL CHECK (daily_limit BETWEEN 1 AND 10000),
    created_by uuid NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE(workflow_id, version),
    UNIQUE(workflow_id, release_id)
);
ALTER TABLE creative_app ADD CONSTRAINT creative_app_active_release_fk
    FOREIGN KEY(workflow_id, active_release_id) REFERENCES creative_app_release(workflow_id, release_id);
CREATE TABLE creative_app_run (
    run_id uuid PRIMARY KEY REFERENCES workflow_run(run_id),
    release_id uuid NOT NULL REFERENCES creative_app_release(release_id),
    account_id uuid NOT NULL REFERENCES creativeflow_account(account_id),
    workflow_id uuid NOT NULL REFERENCES creative_app(workflow_id),
    idempotency_key text NOT NULL CHECK (length(idempotency_key) BETWEEN 1 AND 128),
    request_hash text NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE(account_id, workflow_id, idempotency_key),
    FOREIGN KEY(workflow_id, release_id) REFERENCES creative_app_release(workflow_id, release_id)
);
CREATE INDEX creative_app_run_history ON creative_app_run(account_id, created_at DESC, run_id DESC);
CREATE INDEX creative_app_run_admission ON creative_app_run(workflow_id, account_id, created_at);
ALTER TABLE workflow_run DROP CONSTRAINT ck_workflow_run_trigger_type;
ALTER TABLE workflow_run ADD CONSTRAINT ck_workflow_run_trigger_type
    CHECK(trigger_type IN ('interactive','api','public_creative'));
ALTER TABLE workflow_run DROP CONSTRAINT ck_workflow_run_api_metadata;
ALTER TABLE workflow_run ADD CONSTRAINT ck_workflow_run_api_metadata CHECK (
    (trigger_type IN ('interactive','public_creative') AND deployment_id IS NULL AND api_key_id IS NULL
        AND request_id IS NULL AND idempotency_key IS NULL AND request_hash IS NULL)
    OR (trigger_type='api' AND deployment_id IS NOT NULL AND api_key_id IS NOT NULL
        AND request_id IS NOT NULL AND idempotency_key IS NOT NULL AND request_hash IS NOT NULL AND input_json IS NOT NULL)
);
CREATE TABLE creative_app_audit (
    event_id uuid PRIMARY KEY,
    workflow_id uuid NOT NULL REFERENCES creative_app(workflow_id),
    release_id uuid NULL REFERENCES creative_app_release(release_id),
    actor_id uuid NOT NULL,
    action text NOT NULL CHECK(action IN ('publish','activate','unpublish')),
    created_at timestamptz NOT NULL DEFAULT now()
);
-- Published fixed images and covers participate in the existing retention index.
CREATE TRIGGER asset_reference_guard BEFORE INSERT OR UPDATE OR DELETE ON creative_app_release
    FOR EACH STATEMENT EXECUTE FUNCTION creativeflow_asset_reference_lock();
CREATE TRIGGER asset_reference_sync AFTER INSERT OR UPDATE OR DELETE ON creative_app_release
    FOR EACH ROW EXECUTE FUNCTION creativeflow_sync_asset_references('release_id','','graph_snapshot','metadata');

CREATE OR REPLACE VIEW asset_reference_context AS
SELECT r.*,
    CASE r.source_kind
      WHEN 'creative_app_release' THEN '公共创作 '||car.version||' · '||(car.metadata->>'name')
      WHEN 'canvas' THEN COALESCE(c.name,'画布')
      WHEN 'workflow' THEN COALESCE(w.name,'工作流草稿')
      WHEN 'workflow_template' THEN COALESCE(t.name,'模板')
      WHEN 'workflow_deployment' THEN 'API 发布版本 '||d.version
      WHEN 'workflow_run' THEN '工作流运行'
      WHEN 'workflow_node_run' THEN '节点输出 · '||r.source_slot
      WHEN 'video_job' THEN '视频生成任务'
      WHEN 'image_job' THEN '图片生成任务'
      WHEN 'image_job_output' THEN '图片生成结果'
      WHEN 'canvas_generation_request' THEN COALESCE(cr.canvas_name,'画布生成记录')
      WHEN 'workflow_external_asset' THEN '外部 API 上传'
      WHEN 'canvas_asset' THEN '派生或复用资产'
      WHEN 'knowledge_document' THEN '知识库原始文档'
      ELSE '会话或流式历史' END AS reference_name,
    CASE
      WHEN r.source_kind='creative_app_release' THEN CASE WHEN ca.active_release_id=car.release_id THEN 'protected' ELSE 'history' END
      WHEN r.source_kind='canvas' THEN 'protected'
      WHEN r.source_kind='workflow' THEN CASE WHEN w.deleted_at IS NULL THEN 'protected' ELSE 'history' END
      WHEN r.source_kind='workflow_template' THEN CASE WHEN t.deleted_at IS NULL THEN 'protected' ELSE 'history' END
      WHEN r.source_kind='workflow_deployment' THEN CASE WHEN d.status='published' THEN 'protected' ELSE 'history' END
      WHEN r.source_kind IN ('workflow_run','workflow_node_run','workflow_run_text_event') THEN
        CASE WHEN wr.status IN ('queued','running') THEN 'active' ELSE 'history' END
      WHEN r.source_kind='video_job' THEN CASE WHEN vj.status IN ('pending','running') THEN 'active' ELSE 'history' END
      WHEN r.source_kind IN ('image_job','image_job_output') THEN CASE WHEN ij.status IN ('queued','generating','saving') THEN 'active' ELSE 'history' END
      WHEN r.source_kind='canvas_generation_request' THEN CASE WHEN cr.status='running' THEN 'active' ELSE 'history' END
      WHEN r.source_kind='workflow_external_asset' THEN CASE WHEN ea.consumed_at IS NOT NULL THEN 'history'
        WHEN ea.expires_at IS NOT NULL AND ea.expires_at < now() THEN 'expired' ELSE 'protected' END
      WHEN r.source_kind='canvas_asset' THEN CASE WHEN derived.purged_at IS NOT NULL THEN 'expired' ELSE 'history' END
      WHEN r.source_kind='knowledge_document' THEN 'protected'
      WHEN ar.status IN ('queued','running','awaiting_canvas','awaiting_approval') THEN 'active'
      ELSE 'history' END AS protection,
    COALESCE(w.workflow_id,d.workflow_id,wr.workflow_id,ea.workflow_id,car.workflow_id) AS workflow_id,
    wr.run_id, CASE WHEN r.source_kind='workflow_node_run' THEN r.source_slot ELSE NULL END AS node_id
FROM asset_reference r
LEFT JOIN creative_app_release car ON r.source_kind='creative_app_release' AND car.release_id=r.source_id
LEFT JOIN creative_app ca ON ca.workflow_id=car.workflow_id
LEFT JOIN canvas c ON r.source_kind='canvas' AND c.canvas_id=r.source_id
LEFT JOIN workflow w ON r.source_kind='workflow' AND w.workflow_id=r.source_id
LEFT JOIN workflow_template t ON r.source_kind='workflow_template' AND t.template_id=r.source_id
LEFT JOIN workflow_deployment d ON r.source_kind='workflow_deployment' AND d.deployment_id=r.source_id
LEFT JOIN workflow_run wr ON r.source_kind IN ('workflow_run','workflow_node_run','workflow_run_text_event') AND wr.run_id=r.source_id
LEFT JOIN video_job vj ON r.source_kind='video_job' AND vj.job_id=r.source_id
LEFT JOIN image_job ij ON r.source_kind IN ('image_job','image_job_output') AND ij.job_id=r.source_id
LEFT JOIN canvas_generation_request cr ON r.source_kind='canvas_generation_request' AND cr.request_id=r.source_id
LEFT JOIN workflow_external_asset ea ON r.source_kind='workflow_external_asset' AND ea.asset_id=r.source_id
LEFT JOIN canvas_asset derived ON r.source_kind='canvas_asset' AND derived.asset_id=r.source_id
LEFT JOIN agent_message am ON r.source_kind='agent_message' AND am.message_id=r.source_id
LEFT JOIN agent_tool_call atc ON r.source_kind='agent_tool_call' AND atc.tool_call_id=r.source_id
LEFT JOIN agent_run ar ON ar.run_id=COALESCE(am.run_id,atc.run_id,CASE WHEN r.source_kind='agent_event' THEN r.source_id END);
