-- Continuation runs preserve the failed attempt while reusing durable outputs from nodes that
-- already completed. The node flag lets clients distinguish reused work from newly executed work.

ALTER TABLE workflow_run
    ADD COLUMN IF NOT EXISTS resumed_from_run_id uuid NULL
        REFERENCES workflow_run(run_id) ON DELETE SET NULL;

CREATE INDEX IF NOT EXISTS ix_workflow_run_resumed_from
    ON workflow_run (resumed_from_run_id)
    WHERE resumed_from_run_id IS NOT NULL;

ALTER TABLE workflow_node_run
    ADD COLUMN IF NOT EXISTS is_reused boolean NOT NULL DEFAULT false;
