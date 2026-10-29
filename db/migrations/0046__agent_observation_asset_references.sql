-- Agent receipts, assistant replies and events describe what happened; storing those
-- observations must not be mistaken for selecting an asset for a new operation.
-- Keep every reference for retention/audit, and keep the original guard on tool
-- inputs, user image attachments, workflow graphs, publications and new jobs.
CREATE OR REPLACE FUNCTION creativeflow_sync_asset_references() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    row_json jsonb;
    body jsonb := '[]';
    guard_body jsonb;
    owner uuid;
    slot text;
    i integer;
    strict_mode integer := 1;
BEGIN
    -- Avoid serializing the former binary checkpoint columns of image_job_output.
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

    -- These admission rules are unchanged: replaying jobs or publishing a version
    -- cannot turn an existing historical reference into permission to reuse bytes.
    IF (TG_TABLE_NAME IN ('workflow_run','image_job','video_job','agent_run')
        AND row_json->>'status' IN ('queued','pending','running','generating','saving'))
        OR (TG_TABLE_NAME IN ('workflow_deployment','workflow_template') AND row_json->>'status'='published')
    THEN strict_mode := 2; END IF;

    guard_body := body;
    IF TG_TABLE_NAME='agent_tool_call' THEN
        -- Tool inputs still pass admission. A receipt can mention unavailable assets
        -- while reporting an existing canvas, an error or an operation's outcome.
        guard_body := jsonb_build_array(row_json->'input');
    ELSIF TG_TABLE_NAME='agent_event' OR (TG_TABLE_NAME='agent_message' AND row_json->>'role'='assistant') THEN
        guard_body := '[]'::jsonb;
    ELSIF TG_TABLE_NAME='agent_message' AND jsonb_typeof(row_json->'content')='array' THEN
        -- The server writes tool_result blocks into history using the user role.
        -- Validate all other blocks, including newly submitted image attachments.
        SELECT jsonb_build_array(COALESCE(jsonb_agg(block), '[]'::jsonb)) INTO guard_body
        FROM jsonb_array_elements(row_json->'content') AS block
        WHERE block->>'kind' IS DISTINCT FROM 'tool_result';
    END IF;

    IF guard_body IS DISTINCT FROM body THEN
        -- Validate before replacing the full index; both writes share the original
        -- statement's transaction and advisory lock, so a failure rolls back both.
        PERFORM creativeflow_replace_asset_references(TG_TABLE_NAME,owner,slot,guard_body,strict_mode);
        strict_mode := 0;
    END IF;
    PERFORM creativeflow_replace_asset_references(TG_TABLE_NAME,owner,slot,body,strict_mode);
    RETURN NEW;
END $$;
