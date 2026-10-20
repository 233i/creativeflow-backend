-- Intentional storage cutover. Never discard existing files during an ordinary startup.
-- The operator must stop all writers and explicitly opt in for a non-empty old store.
DO $$ BEGIN
    IF (EXISTS(SELECT 1 FROM sol_asset_blob) OR EXISTS(SELECT 1 FROM sol_skill_file)
        OR EXISTS(SELECT 1 FROM image_job_output WHERE payload IS NOT NULL)
        OR EXISTS(SELECT 1 FROM canvas_asset WHERE purged_at IS NULL)
        OR EXISTS(SELECT 1 FROM skill))
       AND current_setting('creativeflow.discard_database_files', true) IS DISTINCT FROM 'on' THEN
        RAISE EXCEPTION 'File storage cutover requires explicit --discard-database-files with --migrate-only. Existing media and installed skills will be discarded; stop all writers first.';
    END IF;
END $$;

-- Keep historical identities and audit trails, not old file contents or recovery claims.
UPDATE canvas_asset SET purged_at=now(), deleted_at=COALESCE(deleted_at,now()),
    is_public=false, purge_pending=false, cleanup_error=NULL, updated_at=now(),
    management_version=management_version+1 WHERE purged_at IS NULL;
DELETE FROM asset_preview;
DELETE FROM asset_storage_cleanup;
UPDATE image_job SET status='failed', failure_stage='saving', has_checkpoint=false,
    error='旧文件存储已废弃，请重新上传素材；本任务不会自动重新生成。',
    lease_id=NULL, lease_expires_at=NULL, finished_at=now()
    WHERE status IN ('queued','generating','saving') OR (status='failed' AND has_checkpoint);
UPDATE image_job SET has_checkpoint=false WHERE has_checkpoint;
UPDATE canvas_generation_request r SET status='failed', error=j.error, finished_at=now()
    FROM image_job j WHERE r.request_id=j.job_id AND j.error LIKE '旧文件存储已废弃%';
DELETE FROM image_job_output;
UPDATE video_job SET status='failed', error='旧文件存储已废弃，本任务不会自动重新生成。', updated_at=now()
    WHERE status IN ('pending','running');
UPDATE workflow_run SET status='failed', error='文件存储已切换，请检查素材后重新提交。', finished_at=now(),updated_at=now()
    WHERE status IN ('queued','running') AND (
        EXISTS(SELECT 1 FROM asset_reference r JOIN canvas_asset a USING(asset_id)
            WHERE r.source_kind IN ('workflow_run','workflow_node_run','workflow_run_text_event')
                AND r.source_id=workflow_run.run_id AND a.purged_at IS NOT NULL)
        OR graph_snapshot @> '{"nodes":[{"type":"imageGenerate"}]}'
        OR graph_snapshot @> '{"nodes":[{"type":"videoGenerate"}]}'
        OR graph_snapshot @> '{"nodes":[{"type":"videoExtend"}]}');
UPDATE workflow_node_run SET status='skipped',finished_at=now()
    WHERE status IN ('queued','running') AND run_id IN
        (SELECT run_id FROM workflow_run WHERE error='文件存储已切换，请检查素材后重新提交。');
DELETE FROM skill;
DROP TABLE sol_skill_file;
DROP TABLE sol_asset_blob;
ALTER TABLE image_job_output DROP COLUMN payload;

-- Immutable content is addressed by its SHA-256. No file bytes belong in either table.
CREATE TABLE storage_object (
    storage_path text PRIMARY KEY,
    sha256 text NOT NULL CHECK (sha256 ~ '^[0-9a-f]{64}$'),
    media_type text NOT NULL,
    byte_size bigint NOT NULL CHECK (byte_size > 0),
    state text NOT NULL CHECK (state IN ('ready','deleting','deleted')),
    retain_until timestamptz NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    CHECK (storage_path = 'objects/' || left(sha256,2) || '/' || sha256)
);
CREATE INDEX storage_object_collect ON storage_object(retain_until,storage_path) WHERE state<>'deleted';

-- A reservation exists before touching disk. A .complete file is an immutable,
-- recoverable checkpoint even if the subsequent database transaction fails.
CREATE TABLE storage_write (
    write_id uuid PRIMARY KEY,
    operation_key text NOT NULL UNIQUE CHECK (length(operation_key) BETWEEN 1 AND 300),
    lease_id uuid NOT NULL,
    lease_expires_at timestamptz NOT NULL,
    media_type text NOT NULL,
    max_bytes bigint NOT NULL CHECK (max_bytes > 0),
    state text NOT NULL CHECK (state IN ('writing','ready','abandoned')),
    storage_path text NULL REFERENCES storage_object(storage_path),
    sha256 text NULL,
    byte_size bigint NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX storage_write_expiry ON storage_write(lease_expires_at,write_id);
ALTER TABLE image_job_output ADD COLUMN storage_path text NULL REFERENCES storage_object(storage_path),
    ADD COLUMN storage_operation_key text NOT NULL;
CREATE UNIQUE INDEX image_output_storage_operation ON image_job_output(storage_operation_key);
CREATE INDEX image_output_storage_path ON image_job_output(storage_path) WHERE NOT saved;

-- New references may only attach a ready file. Old purged metadata is a tombstone,
-- not a compatibility read path. The shared lock follows the existing asset domain.
CREATE FUNCTION sol_check_file_reference() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_TABLE_NAME='canvas_asset' THEN
        IF NEW.purged_at IS NOT NULL THEN RETURN NEW; END IF;
    END IF;
    IF NEW.storage_path IS NULL THEN RETURN NEW; END IF;
    PERFORM pg_advisory_xact_lock_shared(721909190);
    PERFORM 1 FROM storage_object WHERE storage_path=NEW.storage_path AND state='ready' FOR KEY SHARE;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'File is not ready or has been removed' USING ERRCODE='23514', CONSTRAINT='storage_file_not_ready';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER storage_file_reference BEFORE INSERT OR UPDATE OF storage_path,purged_at ON canvas_asset
    FOR EACH ROW EXECUTE FUNCTION sol_check_file_reference();
CREATE TRIGGER storage_file_reference BEFORE INSERT OR UPDATE OF storage_path ON asset_preview
    FOR EACH ROW EXECUTE FUNCTION sol_check_file_reference();
CREATE TRIGGER storage_file_reference BEFORE INSERT OR UPDATE OF storage_path ON image_job_output
    FOR EACH ROW EXECUTE FUNCTION sol_check_file_reference();

CREATE FUNCTION sol_file_is_referenced(path text) RETURNS boolean LANGUAGE sql STABLE AS $$
    SELECT EXISTS(SELECT 1 FROM canvas_asset WHERE storage_path=path AND purged_at IS NULL AND NOT purge_pending)
        OR EXISTS(SELECT 1 FROM asset_preview WHERE storage_path=path)
        OR EXISTS(SELECT 1 FROM image_job_output WHERE storage_path=path AND NOT saved)
        OR EXISTS(SELECT 1 FROM storage_write w JOIN image_job_output o ON o.storage_operation_key=w.operation_key
                  WHERE w.storage_path=path AND NOT o.saved)
        OR EXISTS(SELECT 1 FROM storage_write w JOIN video_job v ON w.operation_key='video-job:'||replace(v.job_id::text,'-','')
                  WHERE w.storage_path=path AND v.asset_id IS NULL AND v.status<>'cancelled')
$$;
