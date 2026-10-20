-- AG-UI identity and ordered public events belong to the existing durable run, not a second engine.
CREATE UNIQUE INDEX uq_workflow_run_agui_identity
    ON workflow_run (api_key_id, (input_json #>> '{agui,runId}'))
    WHERE trigger_type = 'api' AND input_json ? 'agui';

CREATE TABLE workflow_run_event (
    run_id uuid NOT NULL REFERENCES workflow_run(run_id) ON DELETE CASCADE,
    sequence bigint NOT NULL CHECK (sequence > 0),
    kind text NOT NULL CHECK (kind IN ('run.created','run.state','node.state','text.delta','run.finished')),
    data jsonb NOT NULL CHECK (jsonb_typeof(data) = 'object' AND octet_length(data::text) <= 32768),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (run_id, sequence)
);
CREATE UNIQUE INDEX uq_workflow_run_event_finished ON workflow_run_event(run_id) WHERE kind = 'run.finished';

-- Every writer takes the parent run lock first; the event commits with the corresponding state change.
CREATE FUNCTION sol_append_workflow_event(p_run uuid, p_kind text, p_data jsonb) RETURNS void
LANGUAGE plpgsql AS $$
BEGIN
    PERFORM 1 FROM workflow_run WHERE run_id = p_run AND trigger_type = 'api'
        AND input_json ? 'agui' FOR UPDATE;
    IF NOT FOUND THEN RETURN; END IF;
    -- Late callbacks must not append events after the immutable protocol terminal marker.
    IF EXISTS (SELECT 1 FROM workflow_run_event WHERE run_id=p_run AND kind='run.finished') THEN RETURN; END IF;
    INSERT INTO workflow_run_event(run_id,sequence,kind,data)
        SELECT p_run, COALESCE(MAX(sequence),0)+1, p_kind, p_data
        FROM workflow_run_event WHERE run_id=p_run;
END;
$$;

-- A terminal marker is emitted only after every node is terminal, including cancellation and restart recovery.
CREATE FUNCTION sol_finish_workflow_events(p_run uuid) RETURNS void
LANGUAGE plpgsql AS $$
DECLARE r workflow_run;
BEGIN
    SELECT * INTO r FROM workflow_run WHERE run_id=p_run AND input_json ? 'agui' FOR UPDATE;
    IF NOT FOUND OR r.status NOT IN ('succeeded','failed','cancelled') THEN RETURN; END IF;
    IF EXISTS (SELECT 1 FROM workflow_node_run WHERE run_id=p_run AND status IN ('queued','running'))
        OR EXISTS (SELECT 1 FROM workflow_run_event WHERE run_id=p_run AND kind='run.finished') THEN RETURN; END IF;
    PERFORM sol_append_workflow_event(p_run,'run.finished',jsonb_build_object('status',r.status,'error',r.error));
END;
$$;

CREATE FUNCTION sol_journal_workflow_run() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.trigger_type <> 'api' OR NOT (NEW.input_json ? 'agui') THEN RETURN NEW; END IF;
    IF TG_OP='INSERT' THEN
        PERFORM sol_append_workflow_event(NEW.run_id,'run.created',NEW.input_json->'agui');
    ELSIF (NEW.status,NEW.error,NEW.started_at,NEW.finished_at) IS NOT DISTINCT FROM
        (OLD.status,OLD.error,OLD.started_at,OLD.finished_at) THEN RETURN NEW;
    END IF;
    PERFORM sol_append_workflow_event(NEW.run_id,'run.state',jsonb_build_object(
        'workflowId',NEW.workflow_id,'status',NEW.status,'error',NEW.error,
        'startedAt',NEW.started_at,'finishedAt',NEW.finished_at));
    RETURN NEW;
END;
$$;
CREATE TRIGGER journal_workflow_run AFTER INSERT OR UPDATE ON workflow_run
    FOR EACH ROW EXECUTE FUNCTION sol_journal_workflow_run();

CREATE FUNCTION sol_journal_workflow_node() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP='UPDATE' AND (NEW.status,NEW.progress,NEW.error,NEW.is_reused,NEW.started_at,NEW.finished_at)
        IS NOT DISTINCT FROM (OLD.status,OLD.progress,OLD.error,OLD.is_reused,OLD.started_at,OLD.finished_at)
        THEN RETURN NEW; END IF;
    PERFORM sol_append_workflow_event(NEW.run_id,'node.state',jsonb_build_object(
        'id',NEW.node_id,'type',NEW.node_type,'status',NEW.status,'progress',NEW.progress,
        'isReused',NEW.is_reused,'error',NEW.error,'startedAt',NEW.started_at,'finishedAt',NEW.finished_at));
    RETURN NEW;
END;
$$;
CREATE TRIGGER journal_workflow_node AFTER INSERT OR UPDATE ON workflow_node_run
    FOR EACH ROW EXECUTE FUNCTION sol_journal_workflow_node();

CREATE FUNCTION sol_journal_workflow_text() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    PERFORM sol_append_workflow_event(NEW.run_id,'text.delta',NEW.data);
    RETURN NEW;
END;
$$;
CREATE TRIGGER journal_workflow_text AFTER INSERT ON workflow_run_text_event
    FOR EACH ROW EXECUTE FUNCTION sol_journal_workflow_text();

-- Bulk node updates make all rows visible before AFTER row triggers run. Defer finalization
-- until commit so every node event is appended before the single terminal event.
CREATE FUNCTION sol_finalize_workflow_journal() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    PERFORM sol_finish_workflow_events(NEW.run_id);
    RETURN NEW;
END;
$$;
CREATE CONSTRAINT TRIGGER finish_workflow_run_journal AFTER INSERT OR UPDATE ON workflow_run
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION sol_finalize_workflow_journal();
CREATE CONSTRAINT TRIGGER finish_workflow_node_journal AFTER INSERT OR UPDATE ON workflow_node_run
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION sol_finalize_workflow_journal();
