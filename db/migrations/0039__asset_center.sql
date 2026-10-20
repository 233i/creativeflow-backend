-- 统一资产管理；不删除历史资产、不改写工作流正文、不调用模型。
ALTER TABLE canvas_asset
    ADD COLUMN name text NOT NULL DEFAULT '',
    ADD COLUMN tags text[] NOT NULL DEFAULT '{}',
    ADD COLUMN origin text NOT NULL DEFAULT 'unknown',
    ADD COLUMN deleted_at timestamptz,
    ADD COLUMN deleted_by uuid,
    ADD COLUMN purged_at timestamptz,
    ADD COLUMN purge_pending boolean NOT NULL DEFAULT false,
    ADD COLUMN cleanup_error text,
    ADD COLUMN is_public boolean NOT NULL DEFAULT false,
    ADD COLUMN copied_from_asset_id uuid,
    ADD COLUMN sha256 text,
    ADD COLUMN width integer,
    ADD COLUMN height integer,
    ADD COLUMN duration double precision,
    ADD COLUMN management_version bigint NOT NULL DEFAULT 1,
    ADD COLUMN updated_at timestamptz NOT NULL DEFAULT now();

ALTER TABLE canvas_asset ADD CONSTRAINT ck_asset_center_metadata CHECK (
    char_length(name) <= 200 AND cardinality(tags) <= 20
    AND origin IN ('unknown','upload','generated','derived','reused')
    AND (NOT is_public OR (deleted_at IS NULL AND purged_at IS NULL AND NOT purge_pending))
    AND (purged_at IS NULL OR deleted_at IS NOT NULL));
UPDATE canvas_asset SET origin = CASE WHEN source_asset_id IS NOT NULL THEN 'derived'
    WHEN prompt IS NOT NULL THEN 'generated' ELSE 'unknown' END;
CREATE INDEX ix_asset_center_created ON canvas_asset(created_at DESC,asset_id DESC);
CREATE INDEX ix_asset_center_deleted ON canvas_asset(deleted_at,asset_id) WHERE deleted_at IS NOT NULL;
CREATE INDEX ix_asset_center_storage ON canvas_asset(storage_path);
CREATE INDEX ix_asset_center_tags ON canvas_asset USING gin(tags);
CREATE INDEX ix_asset_center_public ON canvas_asset(created_at DESC,asset_id DESC) WHERE is_public;

CREATE TABLE asset_management_audit (
    id bigserial PRIMARY KEY, asset_id uuid, action text NOT NULL,
    actor_id uuid NOT NULL, reason text NOT NULL, created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_asset_audit ON asset_management_audit(asset_id,id DESC);
CREATE TABLE asset_storage_policy (
    singleton boolean PRIMARY KEY DEFAULT true CHECK(singleton),
    retention_days integer NOT NULL DEFAULT 7 CHECK(retention_days BETWEEN 0 AND 365),
    warning_bytes bigint NOT NULL DEFAULT 10737418240 CHECK(warning_bytes >= 0),
    account_warning_bytes bigint NOT NULL DEFAULT 2147483648 CHECK(account_warning_bytes >= 0)
);
INSERT INTO asset_storage_policy(singleton) VALUES (true);
CREATE TABLE asset_admin_owner (
    admin_id uuid PRIMARY KEY REFERENCES sol_admin(admin_id),
    device_id uuid NOT NULL UNIQUE REFERENCES device(device_id)
);

-- 缩略图独立于可选素材；不会在素材列表中产生重复条目。
CREATE TABLE asset_preview (
    asset_id uuid PRIMARY KEY REFERENCES canvas_asset(asset_id) ON DELETE CASCADE,
    storage_path text NOT NULL, byte_size bigint NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);

-- 内容寻址用于新写入字节去重。写入租约保护 SaveAsync 与元数据提交之间的窗口。
ALTER TABLE sol_asset_blob ADD COLUMN sha256 text,
    ADD COLUMN retain_until timestamptz NOT NULL DEFAULT '-infinity';
CREATE TABLE asset_storage_cleanup (
    storage_path text PRIMARY KEY, requested_at timestamptz NOT NULL DEFAULT now(),
    attempts integer NOT NULL DEFAULT 0, last_attempt_at timestamptz
);

-- 保守索引 JSON 中的 UUID，包括尚未保存的 image_job_output 预留资产 ID。
-- 不设资产外键：任务可以先生成 assetId，再保存对应的媒体；未知 ID 不能被误判为无引用。
CREATE TABLE asset_reference (
    asset_id uuid NOT NULL, source_kind text NOT NULL, source_id uuid NOT NULL,
    source_slot text NOT NULL DEFAULT '',
    PRIMARY KEY(asset_id,source_kind,source_id,source_slot)
);
CREATE INDEX ix_asset_reference_owner ON asset_reference(source_kind,source_id,source_slot);
CREATE FUNCTION sol_asset_ids(payload jsonb) RETURNS SETOF uuid LANGUAGE sql IMMUTABLE AS $$
    SELECT DISTINCT lower(m[1])::uuid FROM regexp_matches(COALESCE(payload::text,''),
        '([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})','gi') m
$$;

-- 写图/受理任务先取得共享事务锁，清理先取得同一独占锁；锁在语句之前获得，
-- 避免清理与图、任务行锁逆序，并保证最终引用检查能看到先行提交的写入。
CREATE FUNCTION sol_asset_reference_lock() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    PERFORM pg_advisory_xact_lock_shared(721909190);
    RETURN NULL;
END $$;

CREATE FUNCTION sol_replace_asset_references(ref_kind text, ref_owner uuid, ref_slot text, payload jsonb, strict_mode integer)
RETURNS void LANGUAGE plpgsql AS $$
BEGIN
    IF strict_mode > 0 AND EXISTS (
        SELECT 1 FROM sol_asset_ids(payload) x JOIN canvas_asset a ON a.asset_id=x
        WHERE (a.purge_pending OR a.purged_at IS NOT NULL OR a.deleted_at IS NOT NULL)
          AND (strict_mode = 2 OR NOT EXISTS (SELECT 1 FROM asset_reference r
              WHERE r.asset_id=a.asset_id AND r.source_kind=ref_kind AND r.source_id=ref_owner AND r.source_slot=ref_slot))
    ) THEN
        RAISE EXCEPTION 'Asset is recycled or permanently removed; restore or replace it before use'
            USING ERRCODE='23514', CONSTRAINT='asset_not_available';
    END IF;
    DELETE FROM asset_reference WHERE source_kind=ref_kind AND source_id=ref_owner AND source_slot=ref_slot;
    INSERT INTO asset_reference(asset_id,source_kind,source_id,source_slot)
    SELECT x,ref_kind,ref_owner,ref_slot FROM sol_asset_ids(payload) x ON CONFLICT DO NOTHING;
END $$;

CREATE FUNCTION sol_sync_asset_references() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE row_json jsonb; body jsonb := '[]'; owner uuid; slot text; i integer; strict_mode integer := 1;
BEGIN
    -- 不对可能包含大块二进制检查点的 image_job_output 整行做 to_jsonb。
    IF TG_TABLE_NAME = 'image_job_output' THEN
        IF TG_OP='DELETE' THEN
            row_json := jsonb_build_object('job_id',OLD.job_id,'output_index',OLD.output_index,'asset_id',OLD.asset_id);
        ELSE
            row_json := jsonb_build_object('job_id',NEW.job_id,'output_index',NEW.output_index,'asset_id',NEW.asset_id);
        END IF;
    ELSE
        row_json := CASE WHEN TG_OP='DELETE' THEN to_jsonb(OLD) ELSE to_jsonb(NEW) END;
    END IF;
    owner := (row_json->>TG_ARGV[0])::uuid;
    slot := COALESCE(row_json->>TG_ARGV[1],'');
    IF TG_OP='DELETE' THEN
        DELETE FROM asset_reference WHERE source_kind=TG_TABLE_NAME AND source_id=owner AND source_slot=slot;
        RETURN OLD;
    END IF;
    FOR i IN 2..TG_NARGS-1 LOOP body := body || jsonb_build_array(row_json->TG_ARGV[i]); END LOOP;
    -- 已有历史可以维持原引用，但新运行、重新入队与发布不得使用回收的内容。
    IF (TG_TABLE_NAME IN ('workflow_run','image_job','video_job','agent_run')
        AND row_json->>'status' IN ('queued','pending','running','generating','saving'))
        OR (TG_TABLE_NAME IN ('workflow_deployment','workflow_template') AND row_json->>'status'='published')
    THEN strict_mode := 2; END IF;
    PERFORM sol_replace_asset_references(TG_TABLE_NAME,owner,slot,body,strict_mode);
    RETURN NEW;
END $$;

-- 所有当前媒体引用载体在一次迁移内回填；同时安装事务内增量维护。
DO $$
DECLARE entry record; columns_sql text; arguments_sql text; r record;
BEGIN
    FOR entry IN SELECT * FROM (VALUES
        ('canvas','canvas_id','',ARRAY['graph']),
        ('workflow','workflow_id','',ARRAY['graph']),
        ('workflow_template','template_id','',ARRAY['graph']),
        ('workflow_deployment','deployment_id','',ARRAY['graph_snapshot','input_schema','output_schema']),
        ('workflow_run','run_id','',ARRAY['graph_snapshot','input_json']),
        ('workflow_node_run','run_id','node_id',ARRAY['output']),
        ('video_job','job_id','',ARRAY['request','asset_id']),
        ('image_job','job_id','',ARRAY['request']),
        ('image_job_output','job_id','output_index',ARRAY['asset_id']),
        ('canvas_generation_request','request_id','',ARRAY['input','output']),
        ('workflow_external_asset','asset_id','',ARRAY['asset_id']),
        ('canvas_asset','asset_id','',ARRAY['source_asset_id','copied_from_asset_id']),
        ('agent_message','message_id','',ARRAY['content']),
        ('agent_tool_call','tool_call_id','',ARRAY['input','result']),
        ('agent_event','run_id','seq',ARRAY['payload']),
        ('workflow_run_text_event','run_id','sequence',ARRAY['data']),
        ('knowledge_document','id','',ARRAY['body'])
    ) AS data(table_name,id_column,slot_column,columns)
    LOOP
        SELECT string_agg(format('to_jsonb(%I)',c),',') INTO columns_sql FROM unnest(entry.columns) c;
        SELECT string_agg(quote_literal(c),',') INTO arguments_sql FROM unnest(entry.columns) c;
        FOR r IN EXECUTE format('SELECT %I AS owner,%s AS slot,jsonb_build_array(%s) AS body FROM %I',
            entry.id_column,CASE WHEN entry.slot_column='' THEN quote_literal('') ELSE quote_ident(entry.slot_column)||'::text' END,
            columns_sql,entry.table_name)
        LOOP
            PERFORM sol_replace_asset_references(entry.table_name,r.owner,r.slot,r.body,0);
        END LOOP;
        EXECUTE format('CREATE TRIGGER asset_reference_guard BEFORE INSERT OR UPDATE OR DELETE ON %I FOR EACH STATEMENT EXECUTE FUNCTION sol_asset_reference_lock()',entry.table_name);
        EXECUTE format('CREATE TRIGGER asset_reference_sync AFTER INSERT OR UPDATE OR DELETE ON %I FOR EACH ROW EXECUTE FUNCTION sol_sync_asset_references(%L,%L,%s)',
            entry.table_name,entry.id_column,entry.slot_column,arguments_sql);
    END LOOP;
END $$;

-- 保护级别按当前资源状态计算，而不是依赖可能过时的引用计数。
CREATE VIEW asset_reference_context AS
SELECT r.*,
    CASE r.source_kind
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
    COALESCE(w.workflow_id,d.workflow_id,wr.workflow_id,ea.workflow_id) AS workflow_id,
    wr.run_id, CASE WHEN r.source_kind='workflow_node_run' THEN r.source_slot ELSE NULL END AS node_id
FROM asset_reference r
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
