-- A template draft is edited directly, but executions continue to use the existing durable
-- workflow runner. Each browser/device gets a hidden workflow copy for a template so debug runs
-- never appear in the user's normal project list and do not interfere with another administrator.

ALTER TABLE workflow
    ADD COLUMN IF NOT EXISTS debug_template_id uuid NULL
        REFERENCES workflow_template(template_id) ON DELETE CASCADE;

CREATE UNIQUE INDEX IF NOT EXISTS uq_workflow_template_debug_device
    ON workflow (debug_template_id, device_id)
    WHERE debug_template_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_workflow_template_debug
    ON workflow (debug_template_id)
    WHERE debug_template_id IS NOT NULL;
