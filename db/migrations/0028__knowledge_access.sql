-- Explicit account grants. Existing accounts and API keys receive no implicit access.
CREATE TABLE knowledge_grant (
    base_id uuid NOT NULL REFERENCES knowledge_base(id),
    account_id uuid NOT NULL REFERENCES sol_account(account_id) ON DELETE CASCADE,
    enabled boolean NOT NULL,
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (base_id, account_id)
);
CREATE TABLE knowledge_grant_audit (
    id uuid PRIMARY KEY,
    base_id uuid NOT NULL REFERENCES knowledge_base(id),
    account_id uuid NOT NULL,
    admin_id uuid NOT NULL,
    enabled boolean NOT NULL,
    reason text NOT NULL CHECK (length(reason) BETWEEN 1 AND 300),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
CREATE INDEX knowledge_grant_audit_base_idx ON knowledge_grant_audit(base_id, created_at DESC);
