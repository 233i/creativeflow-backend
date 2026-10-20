-- Administrator-managed workflow templates. Templates are independent from account-owned
-- workflows; instantiating one creates a normal workflow copy for the requesting device.

CREATE TABLE IF NOT EXISTS workflow_template (
    template_id    uuid PRIMARY KEY,
    name           text NOT NULL,
    description    text NOT NULL DEFAULT '',
    category       text NOT NULL DEFAULT '其他',
    tags           text[] NOT NULL DEFAULT ARRAY[]::text[],
    schema_version integer NOT NULL DEFAULT 1,
    graph          jsonb NOT NULL DEFAULT '{"nodes":[],"edges":[]}'::jsonb,
    status         text NOT NULL DEFAULT 'draft',
    version        integer NOT NULL DEFAULT 1,
    is_featured    boolean NOT NULL DEFAULT false,
    sort_order     integer NOT NULL DEFAULT 0,
    use_count      bigint NOT NULL DEFAULT 0,
    created_at     timestamptz NOT NULL,
    updated_at     timestamptz NOT NULL,
    published_at   timestamptz NULL,

    CONSTRAINT ck_workflow_template_name_length
        CHECK (char_length(name) BETWEEN 1 AND 200),
    CONSTRAINT ck_workflow_template_description_length
        CHECK (char_length(description) <= 2000),
    CONSTRAINT ck_workflow_template_category_length
        CHECK (char_length(category) BETWEEN 1 AND 80),
    CONSTRAINT ck_workflow_template_schema_version
        CHECK (schema_version BETWEEN 1 AND 100),
    CONSTRAINT ck_workflow_template_status
        CHECK (status IN ('draft', 'published', 'archived')),
    CONSTRAINT ck_workflow_template_version CHECK (version >= 1),
    CONSTRAINT ck_workflow_template_sort_order
        CHECK (sort_order BETWEEN -100000 AND 100000),
    CONSTRAINT ck_workflow_template_use_count CHECK (use_count >= 0)
);

CREATE INDEX IF NOT EXISTS ix_workflow_template_public
    ON workflow_template (is_featured DESC, sort_order ASC, published_at DESC)
    WHERE status = 'published';

CREATE INDEX IF NOT EXISTS ix_workflow_template_category
    ON workflow_template (category, sort_order ASC)
    WHERE status = 'published';
