-- Published workflow API versions are immutable snapshots. Draft edits remain isolated until the
-- owner explicitly publishes again.

CREATE TABLE IF NOT EXISTS workflow_deployment (
    deployment_id  uuid PRIMARY KEY,
    workflow_id    uuid NOT NULL REFERENCES workflow(workflow_id) ON DELETE CASCADE,
    version        integer NOT NULL,
    schema_version integer NOT NULL,
    graph_snapshot jsonb NOT NULL,
    input_schema   jsonb NOT NULL,
    output_schema  jsonb NOT NULL,
    description    text NOT NULL DEFAULT '',
    status         text NOT NULL,
    max_concurrency integer NOT NULL DEFAULT 2,
    created_at     timestamptz NOT NULL,
    published_at   timestamptz NOT NULL,
    disabled_at    timestamptz NULL,

    CONSTRAINT uq_workflow_deployment_version UNIQUE (workflow_id, version),
    CONSTRAINT ck_workflow_deployment_status CHECK (status IN ('published', 'disabled')),
    CONSTRAINT ck_workflow_deployment_concurrency CHECK (max_concurrency BETWEEN 1 AND 32),
    CONSTRAINT ck_workflow_deployment_description CHECK (char_length(description) <= 1000)
);

CREATE UNIQUE INDEX IF NOT EXISTS uq_workflow_deployment_published
    ON workflow_deployment (workflow_id)
    WHERE status = 'published';

CREATE INDEX IF NOT EXISTS ix_workflow_deployment_published_at
    ON workflow_deployment (published_at DESC)
    WHERE status = 'published';

CREATE TABLE IF NOT EXISTS workflow_api_key (
    api_key_id   uuid PRIMARY KEY,
    workflow_id uuid NOT NULL REFERENCES workflow(workflow_id) ON DELETE CASCADE,
    name         text NOT NULL,
    key_prefix   text NOT NULL,
    key_hint     text NOT NULL,
    key_hash     bytea NOT NULL,
    enabled      boolean NOT NULL DEFAULT true,
    expires_at   timestamptz NULL,
    last_used_at timestamptz NULL,
    created_at   timestamptz NOT NULL,
    revoked_at   timestamptz NULL,

    CONSTRAINT uq_workflow_api_key_prefix UNIQUE (key_prefix),
    CONSTRAINT ck_workflow_api_key_name CHECK (char_length(name) BETWEEN 1 AND 100),
    CONSTRAINT ck_workflow_api_key_hash CHECK (octet_length(key_hash) = 32)
);

CREATE INDEX IF NOT EXISTS ix_workflow_api_key_workflow_created
    ON workflow_api_key (workflow_id, created_at DESC);

CREATE TABLE IF NOT EXISTS workflow_external_asset (
    asset_id    uuid PRIMARY KEY REFERENCES canvas_asset(asset_id) ON DELETE CASCADE,
    workflow_id uuid NOT NULL REFERENCES workflow(workflow_id) ON DELETE CASCADE,
    api_key_id  uuid NOT NULL REFERENCES workflow_api_key(api_key_id) ON DELETE CASCADE,
    created_at  timestamptz NOT NULL,
    expires_at  timestamptz NULL,
    consumed_at timestamptz NULL
);

CREATE INDEX IF NOT EXISTS ix_workflow_external_asset_scope
    ON workflow_external_asset (workflow_id, asset_id);

ALTER TABLE workflow_run
    ADD COLUMN IF NOT EXISTS deployment_id uuid NULL
        REFERENCES workflow_deployment(deployment_id) ON DELETE CASCADE,
    ADD COLUMN IF NOT EXISTS trigger_type text NOT NULL DEFAULT 'interactive',
    ADD COLUMN IF NOT EXISTS api_key_id uuid NULL
        REFERENCES workflow_api_key(api_key_id) ON DELETE CASCADE,
    ADD COLUMN IF NOT EXISTS request_id text NULL,
    ADD COLUMN IF NOT EXISTS idempotency_key text NULL,
    ADD COLUMN IF NOT EXISTS request_hash bytea NULL,
    ADD COLUMN IF NOT EXISTS input_json jsonb NULL;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'ck_workflow_run_trigger_type'
          AND conrelid = 'workflow_run'::regclass
    ) THEN
        ALTER TABLE workflow_run
            ADD CONSTRAINT ck_workflow_run_trigger_type
            CHECK (trigger_type IN ('interactive', 'api'));
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'ck_workflow_run_api_metadata'
          AND conrelid = 'workflow_run'::regclass
    ) THEN
        ALTER TABLE workflow_run
            ADD CONSTRAINT ck_workflow_run_api_metadata
            CHECK (
                (trigger_type = 'interactive' AND deployment_id IS NULL AND api_key_id IS NULL
                    AND request_id IS NULL AND idempotency_key IS NULL AND request_hash IS NULL)
                OR
                (trigger_type = 'api' AND deployment_id IS NOT NULL AND api_key_id IS NOT NULL
                    AND request_id IS NOT NULL AND idempotency_key IS NOT NULL
                    AND request_hash IS NOT NULL AND input_json IS NOT NULL)
            );
    END IF;
END
$$;

DROP INDEX IF EXISTS uq_workflow_run_active;

CREATE UNIQUE INDEX IF NOT EXISTS uq_workflow_run_active_interactive
    ON workflow_run (workflow_id)
    WHERE trigger_type = 'interactive' AND status IN ('queued', 'running');

CREATE UNIQUE INDEX IF NOT EXISTS uq_workflow_run_idempotency
    ON workflow_run (api_key_id, idempotency_key)
    WHERE api_key_id IS NOT NULL AND idempotency_key IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_workflow_run_external_history
    ON workflow_run (workflow_id, created_at DESC)
    WHERE trigger_type = 'api';

CREATE INDEX IF NOT EXISTS ix_workflow_run_deployment_active
    ON workflow_run (deployment_id, status)
    WHERE trigger_type = 'api' AND status IN ('queued', 'running');
