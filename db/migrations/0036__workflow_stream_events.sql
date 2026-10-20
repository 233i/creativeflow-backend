-- Only public, provisional text is retained. Deleting a run also deletes its stream history.
CREATE TABLE workflow_run_text_event (
    run_id uuid NOT NULL REFERENCES workflow_run(run_id) ON DELETE CASCADE,
    sequence bigint NOT NULL CHECK (sequence > 0),
    data jsonb NOT NULL CHECK (jsonb_typeof(data) = 'object' AND octet_length(data::text) <= 32768),
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (run_id, sequence)
);
