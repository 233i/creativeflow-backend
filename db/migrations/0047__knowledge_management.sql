-- Knowledge bodies remain versioned; deleting a library retains metadata and audit tombstones.
ALTER TABLE knowledge_base ADD COLUMN revision bigint NOT NULL DEFAULT 1;
ALTER TABLE knowledge_base ADD COLUMN deletion_status text NOT NULL DEFAULT 'none'
    CHECK (deletion_status IN ('none','deleting','delete_failed','deleted'));
ALTER TABLE knowledge_base ADD COLUMN deletion_error text NULL;
ALTER TABLE knowledge_base ADD COLUMN deleted_at timestamptz NULL;
ALTER TABLE knowledge_base ADD CONSTRAINT knowledge_deleted_disabled CHECK (deletion_status = 'none' OR NOT enabled);

CREATE TABLE knowledge_management_audit (
    id uuid PRIMARY KEY,
    base_id uuid NOT NULL REFERENCES knowledge_base(id),
    admin_id uuid NULL REFERENCES creativeflow_admin(admin_id),
    action text NOT NULL,
    reason text NOT NULL DEFAULT '',
    before_json jsonb NULL,
    after_json jsonb NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
CREATE INDEX knowledge_management_audit_base ON knowledge_management_audit(base_id, created_at DESC);

CREATE FUNCTION knowledge_management_revision() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF (NEW.name, NEW.description, NEW.enabled, NEW.active_version, NEW.deletion_status)
        IS DISTINCT FROM (OLD.name, OLD.description, OLD.enabled, OLD.active_version, OLD.deletion_status) THEN
        NEW.revision := OLD.revision + 1;
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER knowledge_management_revision BEFORE UPDATE ON knowledge_base
    FOR EACH ROW EXECUTE FUNCTION knowledge_management_revision();

-- Use the same lock as publication, run and draft pins. A preflight is never an authorization.
CREATE FUNCTION knowledge_management_pin_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    PERFORM pg_advisory_xact_lock(721903263);
    IF EXISTS (SELECT 1 FROM knowledge_version v JOIN knowledge_base b ON b.id = v.base_id
        WHERE v.id = NEW.version_id AND b.deletion_status <> 'none') THEN
        RAISE EXCEPTION 'Knowledge library is being deleted' USING ERRCODE = '23514', CONSTRAINT = 'knowledge_version_unavailable';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER knowledge_management_pin_guard BEFORE INSERT OR UPDATE ON knowledge_version_pin
    FOR EACH ROW EXECUTE FUNCTION knowledge_management_pin_guard();

-- Drafts normally tolerate incomplete graphs, but must not silently add a reference to a sealed library.
CREATE FUNCTION knowledge_management_graph_guard() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE graph_json jsonb; record_json jsonb;
BEGIN
    record_json := to_jsonb(NEW); graph_json := record_json->TG_ARGV[0];
    IF TG_TABLE_NAME = 'workflow_run' AND record_json->'input_json'->'nodeDebug' IS NOT NULL THEN
        SELECT jsonb_build_object('nodes', COALESCE(jsonb_agg(jsonb_build_object(
            'type', 'knowledgeRetrieve', 'data', jsonb_build_object('versionId', value->>'versionId'))), '[]'::jsonb))
        INTO graph_json FROM jsonb_each(COALESCE(record_json->'input_json'->'knowledge'->'references', '{}'::jsonb));
    END IF;
    IF NOT COALESCE(graph_json @> '{"nodes":[{"type":"knowledgeRetrieve"}]}'::jsonb, false) THEN RETURN NEW; END IF;
    PERFORM pg_advisory_xact_lock(721903263);
    IF EXISTS (SELECT 1 FROM jsonb_array_elements(CASE WHEN jsonb_typeof(graph_json->'nodes') = 'array' THEN graph_json->'nodes' ELSE '[]'::jsonb END) n
        JOIN knowledge_version v ON n->>'type' = 'knowledgeRetrieve' AND n->'data'->>'versionId' = v.id::text
        JOIN knowledge_base b ON b.id = v.base_id WHERE b.deletion_status <> 'none') THEN
        RAISE EXCEPTION 'Knowledge library is being deleted' USING ERRCODE = '23514', CONSTRAINT = 'knowledge_version_unavailable';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER knowledge_management_workflow_guard BEFORE INSERT OR UPDATE OF graph ON workflow
    FOR EACH ROW EXECUTE FUNCTION knowledge_management_graph_guard('graph');
CREATE TRIGGER knowledge_management_template_guard BEFORE INSERT OR UPDATE OF graph ON workflow_template
    FOR EACH ROW EXECUTE FUNCTION knowledge_management_graph_guard('graph');
CREATE TRIGGER knowledge_management_deployment_guard BEFORE INSERT OR UPDATE OF graph_snapshot ON workflow_deployment
    FOR EACH ROW EXECUTE FUNCTION knowledge_management_graph_guard('graph_snapshot');
CREATE TRIGGER knowledge_management_run_guard BEFORE INSERT OR UPDATE OF graph_snapshot, input_json ON workflow_run
    FOR EACH ROW EXECUTE FUNCTION knowledge_management_graph_guard('graph_snapshot');

-- Complete only after every collection has been confirmed deleted and its local body removed.
CREATE FUNCTION knowledge_management_cleanup_progress() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE changed uuid;
BEGIN
    IF NEW.status = 'cleanup_failed' THEN
        UPDATE knowledge_base SET deletion_status = 'delete_failed', deletion_error = NEW.error
        WHERE id = NEW.base_id AND deletion_status = 'deleting' RETURNING id INTO changed;
        IF changed IS NOT NULL THEN
            INSERT INTO knowledge_management_audit(id, base_id, action, reason)
            VALUES (gen_random_uuid(), changed, 'delete_failed', COALESCE(NEW.error, 'Index cleanup failed'));
        END IF;
    ELSIF NEW.status = 'deleted' THEN
        UPDATE knowledge_base SET deletion_status = 'deleted', deletion_error = NULL, deleted_at = clock_timestamp()
        WHERE id = NEW.base_id AND deletion_status IN ('deleting','delete_failed')
            AND NOT EXISTS (SELECT 1 FROM knowledge_version WHERE base_id = NEW.base_id AND status <> 'deleted')
        RETURNING id INTO changed;
        IF changed IS NOT NULL THEN
            INSERT INTO knowledge_management_audit(id, base_id, action) VALUES (gen_random_uuid(), changed, 'delete_completed');
        END IF;
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER knowledge_management_cleanup_progress AFTER UPDATE OF status ON knowledge_version
    FOR EACH ROW EXECUTE FUNCTION knowledge_management_cleanup_progress();
