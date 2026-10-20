-- Workflow projects are intentionally separate from free-form canvases. Their graph is validated
-- and executed by the server, while the existing canvas table and client-side canvas runtime stay
-- unchanged.

CREATE TABLE IF NOT EXISTS workflow (
    workflow_id    uuid PRIMARY KEY,
    device_id      uuid NOT NULL REFERENCES device(device_id) ON DELETE CASCADE,
    name           text NOT NULL,
    schema_version integer NOT NULL DEFAULT 1,
    graph          jsonb NOT NULL DEFAULT '{"nodes":[],"edges":[]}'::jsonb,
    created_at     timestamptz NOT NULL,
    updated_at     timestamptz NOT NULL,

    CONSTRAINT ck_workflow_name_length CHECK (char_length(name) BETWEEN 1 AND 200),
    CONSTRAINT ck_workflow_schema_version CHECK (schema_version BETWEEN 1 AND 100)
);

CREATE INDEX IF NOT EXISTS ix_workflow_device_updated
    ON workflow (device_id, updated_at DESC);

CREATE TABLE IF NOT EXISTS workflow_run (
    run_id         uuid PRIMARY KEY,
    workflow_id    uuid NOT NULL REFERENCES workflow(workflow_id) ON DELETE CASCADE,
    device_id      uuid NOT NULL REFERENCES device(device_id) ON DELETE CASCADE,
    graph_snapshot jsonb NOT NULL,
    status         text NOT NULL,
    error          text NULL,
    created_at     timestamptz NOT NULL,
    updated_at     timestamptz NOT NULL,
    started_at     timestamptz NULL,
    finished_at    timestamptz NULL,

    CONSTRAINT ck_workflow_run_status
        CHECK (status IN ('queued', 'running', 'succeeded', 'failed', 'cancelled'))
);

CREATE INDEX IF NOT EXISTS ix_workflow_run_workflow_created
    ON workflow_run (workflow_id, created_at DESC);

-- A double click cannot enqueue two billable executions of the same workflow.
CREATE UNIQUE INDEX IF NOT EXISTS uq_workflow_run_active
    ON workflow_run (workflow_id)
    WHERE status IN ('queued', 'running');

CREATE INDEX IF NOT EXISTS ix_workflow_run_queue
    ON workflow_run (created_at)
    WHERE status = 'queued';

CREATE TABLE IF NOT EXISTS workflow_node_run (
    run_id      uuid NOT NULL REFERENCES workflow_run(run_id) ON DELETE CASCADE,
    node_id     text NOT NULL,
    node_type   text NOT NULL,
    status      text NOT NULL,
    progress    double precision NULL,
    output      jsonb NULL,
    error       text NULL,
    started_at  timestamptz NULL,
    finished_at timestamptz NULL,

    PRIMARY KEY (run_id, node_id),
    CONSTRAINT ck_workflow_node_run_status
        CHECK (status IN ('queued', 'running', 'succeeded', 'failed', 'cancelled', 'skipped')),
    CONSTRAINT ck_workflow_node_progress
        CHECK (progress IS NULL OR (progress >= 0 AND progress <= 1))
);
