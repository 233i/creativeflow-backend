-- Independent, bounded knowledge-library pilot. No changes to old graph/media tables.
CREATE TABLE knowledge_base (
    id uuid PRIMARY KEY,
    name text NOT NULL,
    description text NOT NULL,
    model_id uuid NOT NULL REFERENCES ai_model(model_id),
    enabled boolean NOT NULL DEFAULT true,
    active_version uuid NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
CREATE TABLE knowledge_version (
    id uuid PRIMARY KEY,
    base_id uuid NOT NULL REFERENCES knowledge_base(id),
    digest text NOT NULL,
    model_snapshot jsonb NOT NULL,
    documents_json jsonb NOT NULL,
    chunks_json jsonb NOT NULL,
    vectors_json jsonb NOT NULL DEFAULT '{}',
    status text NOT NULL CHECK (status IN ('queued','embedding','writing','ready','failed','unknown')),
    error text NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    UNIQUE(base_id, digest),
    UNIQUE(base_id, id)
);
ALTER TABLE knowledge_base ADD CONSTRAINT knowledge_active_version_fk
    FOREIGN KEY (id, active_version) REFERENCES knowledge_version(base_id, id);
CREATE INDEX knowledge_pending_idx ON knowledge_version(created_at) WHERE status IN ('queued','embedding','writing');
CREATE UNIQUE INDEX knowledge_one_candidate_idx ON knowledge_version(base_id)
    WHERE status IN ('queued','embedding','writing');
