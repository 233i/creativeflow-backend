-- API workflows belong to the administration surface. Keep existing deployment IDs,
-- keys and run history intact while removing their drafts from ordinary users.
ALTER TABLE workflow ADD COLUMN IF NOT EXISTS is_api boolean NOT NULL DEFAULT false;

UPDATE workflow w SET is_api = true
WHERE EXISTS (SELECT 1 FROM workflow_deployment d WHERE d.workflow_id = w.workflow_id);

CREATE INDEX IF NOT EXISTS ix_workflow_api_updated
    ON workflow (updated_at DESC) WHERE is_api;
