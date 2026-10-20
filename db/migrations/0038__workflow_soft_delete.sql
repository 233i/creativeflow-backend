-- 管理工作流与历史模板使用软删除；既有运行、发布快照、素材和授权审计不级联清理。
ALTER TABLE workflow ADD COLUMN deleted_at timestamptz;
ALTER TABLE workflow_template ADD COLUMN deleted_at timestamptz;

CREATE TABLE workflow_deletion_audit (
    source text NOT NULL CHECK (source IN ('workflow', 'template')),
    resource_id uuid NOT NULL,
    resource_name text NOT NULL,
    actor_id uuid NOT NULL,
    deleted_at timestamptz NOT NULL,
    impact jsonb NOT NULL,
    PRIMARY KEY (source, resource_id)
);

-- 排队受理及发布与删除共用行锁。模板先于调试图加锁，防止模板删除后的迟到提交。
CREATE FUNCTION sol_lock_live_workflow(target uuid) RETURNS void LANGUAGE plpgsql AS $$
DECLARE parent_id uuid;
BEGIN
    SELECT debug_template_id INTO parent_id FROM workflow WHERE workflow_id = target;
    IF parent_id IS NOT NULL THEN
        PERFORM 1 FROM workflow_template WHERE template_id = parent_id AND deleted_at IS NULL FOR SHARE;
        IF NOT FOUND THEN
            RAISE EXCEPTION 'Workflow template has been deleted' USING ERRCODE = '23514', CONSTRAINT = 'workflow_deleted';
        END IF;
    END IF;
    PERFORM 1 FROM workflow WHERE workflow_id = target AND deleted_at IS NULL FOR SHARE;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Workflow has been deleted' USING ERRCODE = '23514', CONSTRAINT = 'workflow_deleted';
    END IF;
END $$;

CREATE FUNCTION sol_guard_live_workflow_run() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.status IN ('queued', 'running') THEN
        PERFORM sol_lock_live_workflow(NEW.workflow_id);
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER workflow_run_live_guard BEFORE INSERT OR UPDATE OF status ON workflow_run
    FOR EACH ROW EXECUTE FUNCTION sol_guard_live_workflow_run();

CREATE FUNCTION sol_guard_live_workflow_deployment() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.status = 'published' THEN PERFORM sol_lock_live_workflow(NEW.workflow_id); END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER workflow_deployment_live_guard BEFORE INSERT OR UPDATE OF status ON workflow_deployment
    FOR EACH ROW EXECUTE FUNCTION sol_guard_live_workflow_deployment();

CREATE FUNCTION sol_guard_live_template_debug() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.debug_template_id IS NOT NULL THEN
        PERFORM 1 FROM workflow_template WHERE template_id = NEW.debug_template_id AND deleted_at IS NULL FOR SHARE;
        IF NOT FOUND THEN
            RAISE EXCEPTION 'Workflow template has been deleted' USING ERRCODE = '23514', CONSTRAINT = 'workflow_deleted';
        END IF;
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER workflow_template_debug_live_guard BEFORE INSERT OR UPDATE OF graph ON workflow
    FOR EACH ROW EXECUTE FUNCTION sol_guard_live_template_debug();
