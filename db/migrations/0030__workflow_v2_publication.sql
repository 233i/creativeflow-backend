-- v1 keeps its existing graph/contract and NULL execution snapshot.
ALTER TABLE workflow_deployment ADD COLUMN execution_snapshot jsonb NULL
    CHECK (execution_snapshot IS NULL OR jsonb_typeof(execution_snapshot) = 'object');

-- Workflow invocation permission never implicitly grants knowledge access.
CREATE TABLE knowledge_api_key_grant (
    base_id uuid NOT NULL REFERENCES knowledge_base(id),
    api_key_id uuid NOT NULL REFERENCES workflow_api_key(api_key_id) ON DELETE CASCADE,
    enabled boolean NOT NULL,
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (base_id, api_key_id)
);
CREATE TABLE knowledge_api_key_grant_audit (
    id uuid PRIMARY KEY,
    base_id uuid NOT NULL REFERENCES knowledge_base(id),
    api_key_id uuid NOT NULL,
    admin_id uuid NOT NULL,
    enabled boolean NOT NULL,
    reason text NOT NULL CHECK (length(reason) BETWEEN 1 AND 300),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
CREATE INDEX knowledge_api_key_grant_audit_base_idx ON knowledge_api_key_grant_audit(base_id, created_at DESC);
