-- 外部查询凭据与不可变配置版本独立于工作流；既有 v1 API Key 和媒体表不改写。
CREATE TABLE IF NOT EXISTS workflow_http_credential (
    id uuid PRIMARY KEY,
    name text NOT NULL CHECK (char_length(name) BETWEEN 1 AND 100),
    description text NOT NULL DEFAULT '' CHECK (char_length(description) <= 1000),
    enabled boolean NOT NULL DEFAULT true,
    current_revision uuid NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
CREATE TABLE IF NOT EXISTS workflow_http_credential_revision (
    id uuid PRIMARY KEY,
    credential_id uuid NOT NULL REFERENCES workflow_http_credential(id),
    definition_json jsonb NOT NULL CHECK (jsonb_typeof(definition_json) = 'object'),
    digest text NOT NULL CHECK (char_length(digest) = 64),
    secret_cipher bytea NULL,
    secret_nonce bytea NULL,
    secret_tag bytea NULL,
    secret_hint text NULL,
    created_by uuid NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    UNIQUE (credential_id, id),
    CONSTRAINT ck_workflow_http_credential_secret CHECK (
      (secret_cipher IS NULL AND secret_nonce IS NULL AND secret_tag IS NULL AND secret_hint IS NULL)
      OR (secret_cipher IS NOT NULL AND octet_length(secret_nonce) = 12 AND octet_length(secret_tag) = 16 AND secret_hint IS NOT NULL))
);
ALTER TABLE workflow_http_credential ADD CONSTRAINT fk_workflow_http_credential_current_revision
    FOREIGN KEY (id, current_revision) REFERENCES workflow_http_credential_revision(credential_id, id);
CREATE INDEX IF NOT EXISTS ix_workflow_http_credential_revisions ON workflow_http_credential_revision(credential_id, created_at DESC);

-- 外部 Key 授权只表示此凭据的调用权，不隐含知识库权限或工作流发布权限。
CREATE TABLE IF NOT EXISTS workflow_http_credential_grant (
    credential_id uuid NOT NULL REFERENCES workflow_http_credential(id),
    subject_kind text NOT NULL CHECK (subject_kind IN ('account', 'apiKey')),
    subject_id uuid NOT NULL,
    enabled boolean NOT NULL,
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY(credential_id, subject_kind, subject_id)
);
CREATE TABLE IF NOT EXISTS workflow_http_credential_grant_audit (
    id uuid PRIMARY KEY,
    credential_id uuid NOT NULL REFERENCES workflow_http_credential(id),
    subject_kind text NOT NULL CHECK (subject_kind IN ('account', 'apiKey')),
    subject_id uuid NOT NULL,
    admin_id uuid NOT NULL,
    enabled boolean NOT NULL,
    reason text NOT NULL CHECK (char_length(reason) BETWEEN 1 AND 300),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
CREATE INDEX IF NOT EXISTS ix_workflow_http_credential_grant_audit ON workflow_http_credential_grant_audit(credential_id, created_at DESC);
