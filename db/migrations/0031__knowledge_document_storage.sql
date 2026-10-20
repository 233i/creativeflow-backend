-- Preserve pilot versions while moving document bodies, chunks and vector checkpoints out of
-- whole-version JSONB. Published graphs keep their version/digest/chunk identities unchanged.
CREATE TABLE knowledge_document (
    id uuid PRIMARY KEY,
    base_id uuid NOT NULL REFERENCES knowledge_base(id),
    digest text NOT NULL,
    name text NOT NULL,
    body text NOT NULL,
    byte_count integer NOT NULL CHECK (byte_count > 0),
    UNIQUE(base_id, digest),
    UNIQUE(base_id, id)
);
CREATE TABLE knowledge_version_document (
    version_id uuid NOT NULL REFERENCES knowledge_version(id) ON DELETE CASCADE,
    document_id uuid NOT NULL REFERENCES knowledge_document(id),
    ordinal integer NOT NULL,
    PRIMARY KEY(version_id, document_id),
    UNIQUE(version_id, ordinal)
);
CREATE TABLE knowledge_chunk (
    version_id uuid NOT NULL REFERENCES knowledge_version(id) ON DELETE CASCADE,
    id uuid NOT NULL,
    document_id uuid NOT NULL REFERENCES knowledge_document(id),
    ordinal integer NOT NULL,
    start_line integer NOT NULL,
    end_line integer NOT NULL,
    body text NOT NULL,
    vector real[] NULL,
    written boolean NOT NULL DEFAULT false,
    PRIMARY KEY(version_id, id),
    UNIQUE(version_id, ordinal)
);
CREATE INDEX knowledge_chunk_pending ON knowledge_chunk(version_id, ordinal) WHERE vector IS NULL;
CREATE INDEX knowledge_chunk_writing ON knowledge_chunk(version_id, ordinal) WHERE NOT written;
CREATE INDEX knowledge_chunk_reuse ON knowledge_chunk(id) WHERE vector IS NOT NULL;
CREATE INDEX knowledge_document_membership ON knowledge_version_document(document_id);

INSERT INTO knowledge_document(id, base_id, digest, name, body, byte_count)
SELECT gen_random_uuid(), base_id, digest, name, body, octet_length(body)
FROM (
    SELECT DISTINCT v.base_id, encode(sha256(convert_to(jsonb_build_array(d->>'name', d->>'text')::text, 'UTF8')), 'hex') AS digest,
        d->>'name' AS name, d->>'text' AS body
    FROM knowledge_version v CROSS JOIN LATERAL jsonb_array_elements(v.documents_json) d
) documents;
INSERT INTO knowledge_version_document(version_id, document_id, ordinal)
SELECT v.id, d.id, item.ordinality::int
FROM knowledge_version v CROSS JOIN LATERAL jsonb_array_elements(v.documents_json) WITH ORDINALITY item(value, ordinality)
JOIN knowledge_document d ON d.base_id = v.base_id
    AND d.digest = encode(sha256(convert_to(jsonb_build_array(item.value->>'name', item.value->>'text')::text, 'UTF8')), 'hex');
INSERT INTO knowledge_chunk(version_id, id, document_id, ordinal, start_line, end_line, body, vector, written)
SELECT v.id, (item.value->>'id')::uuid, d.id, item.ordinality::int,
    (item.value->>'startLine')::int, (item.value->>'endLine')::int, item.value->>'text',
    CASE WHEN v.vectors_json ? (item.value->>'id')
        THEN ARRAY(SELECT value::real FROM jsonb_array_elements_text(v.vectors_json->(item.value->>'id'))) END,
    v.status = 'ready'
FROM knowledge_version v CROSS JOIN LATERAL jsonb_array_elements(v.chunks_json) WITH ORDINALITY item(value, ordinality)
JOIN knowledge_version_document vd ON vd.version_id = v.id
JOIN knowledge_document d ON d.id = vd.document_id AND d.name = item.value->>'document';

ALTER TABLE knowledge_version
    ADD COLUMN document_count integer NOT NULL DEFAULT 0,
    ADD COLUMN chunk_count integer NOT NULL DEFAULT 0,
    ADD COLUMN embedded_count integer NOT NULL DEFAULT 0,
    ADD COLUMN written_count integer NOT NULL DEFAULT 0,
    ADD COLUMN reused_count integer NOT NULL DEFAULT 0,
    ADD COLUMN reserved_bytes bigint NOT NULL DEFAULT 0,
    ADD COLUMN source_version_id uuid NULL,
    ADD COLUMN chunk_size integer NULL,
    ADD COLUMN overlap integer NULL;
ALTER TABLE knowledge_version ADD COLUMN number integer NOT NULL DEFAULT 0;
UPDATE knowledge_version v SET number = ordered.n FROM
    (SELECT id, row_number() OVER(PARTITION BY base_id ORDER BY created_at, id)::int AS n FROM knowledge_version) ordered WHERE ordered.id = v.id;
CREATE UNIQUE INDEX knowledge_version_number ON knowledge_version(base_id, number);
UPDATE knowledge_version v SET
    document_count = jsonb_array_length(v.documents_json),
    chunk_count = jsonb_array_length(v.chunks_json),
    embedded_count = (SELECT count(*) FROM knowledge_chunk c WHERE c.version_id = v.id AND c.vector IS NOT NULL),
    written_count = CASE WHEN v.status = 'ready' THEN jsonb_array_length(v.chunks_json) ELSE 0 END,
    reserved_bytes = (SELECT COALESCE(sum(d.byte_count), 0) FROM knowledge_version_document vd JOIN knowledge_document d ON d.id = vd.document_id WHERE vd.version_id = v.id)
        + (SELECT COALESCE(sum(octet_length(c.body) + COALESCE((v.model_snapshot->>'dimensions')::int, 8192) * 4 + 256), 0) FROM knowledge_chunk c WHERE c.version_id = v.id);
DO $$ BEGIN
    IF EXISTS (SELECT 1 FROM knowledge_version v WHERE
        v.document_count <> (SELECT count(*) FROM knowledge_version_document WHERE version_id = v.id)
        OR v.chunk_count <> (SELECT count(*) FROM knowledge_chunk WHERE version_id = v.id)
        OR v.embedded_count <> (SELECT count(*) FROM jsonb_object_keys(v.vectors_json))) THEN
        RAISE EXCEPTION 'Knowledge backfill counts differ; original JSONB data has not been removed';
    END IF;
END $$;
ALTER TABLE knowledge_version DROP COLUMN documents_json, DROP COLUMN chunks_json, DROP COLUMN vectors_json;
ALTER TABLE knowledge_version DROP CONSTRAINT knowledge_version_status_check;
ALTER TABLE knowledge_version ADD CONSTRAINT knowledge_version_status_check
    CHECK (status IN ('queued','embedding','writing','ready','failed','unknown','cleaning','cleanup_failed','deleted'));
ALTER TABLE knowledge_version DROP CONSTRAINT knowledge_version_base_id_digest_key;
CREATE UNIQUE INDEX knowledge_live_digest ON knowledge_version(base_id, digest) WHERE status <> 'deleted';

-- Pins serialize against cleanup (the same advisory transaction lock). Even a publisher that
-- captured a ready version just before cleanup cannot enqueue a dangling reference afterward.
CREATE TABLE knowledge_version_pin (
    version_id uuid NOT NULL REFERENCES knowledge_version(id),
    owner_kind text NOT NULL,
    owner_id uuid NOT NULL,
    PRIMARY KEY(version_id, owner_kind, owner_id)
);
CREATE INDEX knowledge_pin_owner ON knowledge_version_pin(owner_kind, owner_id);
CREATE FUNCTION sync_knowledge_version_pins() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE record_json jsonb; graph_json jsonb; owner uuid; version_uuid uuid; item jsonb; strict_reference boolean;
BEGIN
    record_json := CASE WHEN TG_OP = 'DELETE' THEN to_jsonb(OLD) ELSE to_jsonb(NEW) END;
    owner := (record_json->>TG_ARGV[0])::uuid;
    graph_json := record_json->TG_ARGV[1];
    IF NOT COALESCE(graph_json @> '{"nodes":[{"type":"knowledgeRetrieve"}]}'::jsonb, false)
        AND NOT EXISTS (SELECT 1 FROM knowledge_version_pin WHERE owner_kind = TG_TABLE_NAME AND owner_id = owner) THEN
        IF TG_OP = 'DELETE' THEN RETURN OLD; ELSE RETURN NEW; END IF;
    END IF;
    PERFORM pg_advisory_xact_lock(721903263);
    DELETE FROM knowledge_version_pin WHERE owner_kind = TG_TABLE_NAME AND owner_id = owner;
    IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
    strict_reference := TG_TABLE_NAME IN ('workflow_run', 'workflow_deployment');
    FOR item IN SELECT value FROM jsonb_array_elements(CASE WHEN jsonb_typeof(graph_json->'nodes') = 'array' THEN graph_json->'nodes' ELSE '[]'::jsonb END)
    LOOP
        IF item->>'type' IS DISTINCT FROM 'knowledgeRetrieve' OR COALESCE(item->'data'->>'versionId', '') !~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$' THEN CONTINUE; END IF;
        version_uuid := (item->'data'->>'versionId')::uuid;
        IF strict_reference AND NOT EXISTS (SELECT 1 FROM knowledge_version WHERE id = version_uuid AND status = 'ready') THEN
            RAISE EXCEPTION 'Knowledge version is no longer available' USING ERRCODE = '23514', CONSTRAINT = 'knowledge_version_unavailable';
        END IF;
        INSERT INTO knowledge_version_pin(version_id, owner_kind, owner_id)
        SELECT version_uuid, TG_TABLE_NAME, owner FROM knowledge_version
        WHERE id = version_uuid AND status NOT IN ('cleaning','cleanup_failed','deleted')
        ON CONFLICT DO NOTHING;
    END LOOP;
    RETURN NEW;
END $$;
CREATE TRIGGER knowledge_workflow_pin AFTER INSERT OR UPDATE OF graph OR DELETE ON workflow
    FOR EACH ROW EXECUTE FUNCTION sync_knowledge_version_pins('workflow_id', 'graph');
CREATE TRIGGER knowledge_template_pin AFTER INSERT OR UPDATE OF graph OR DELETE ON workflow_template
    FOR EACH ROW EXECUTE FUNCTION sync_knowledge_version_pins('template_id', 'graph');
CREATE TRIGGER knowledge_deployment_pin AFTER INSERT OR UPDATE OF graph_snapshot OR DELETE ON workflow_deployment
    FOR EACH ROW EXECUTE FUNCTION sync_knowledge_version_pins('deployment_id', 'graph_snapshot');
CREATE TRIGGER knowledge_run_pin AFTER INSERT OR UPDATE OF graph_snapshot OR DELETE ON workflow_run
    FOR EACH ROW EXECUTE FUNCTION sync_knowledge_version_pins('run_id', 'graph_snapshot');
INSERT INTO knowledge_version_pin(version_id, owner_kind, owner_id)
SELECT DISTINCT v.id, owners.kind, owners.id FROM (
    SELECT 'workflow' AS kind, workflow_id AS id, graph FROM workflow
    UNION ALL SELECT 'workflow_template', template_id, graph FROM workflow_template
    UNION ALL SELECT 'workflow_deployment', deployment_id, graph_snapshot FROM workflow_deployment
    UNION ALL SELECT 'workflow_run', run_id, graph_snapshot FROM workflow_run
) owners CROSS JOIN LATERAL jsonb_array_elements(CASE WHEN jsonb_typeof(owners.graph->'nodes') = 'array' THEN owners.graph->'nodes' ELSE '[]'::jsonb END) node
JOIN knowledge_version v ON node->>'type' = 'knowledgeRetrieve' AND node->'data'->>'versionId' = v.id::text
ON CONFLICT DO NOTHING;

CREATE TABLE knowledge_cleanup_audit (
    id uuid PRIMARY KEY,
    base_id uuid NOT NULL REFERENCES knowledge_base(id),
    version_id uuid NOT NULL REFERENCES knowledge_version(id),
    admin_id uuid NOT NULL REFERENCES sol_admin(admin_id),
    reason text NOT NULL CHECK (char_length(reason) BETWEEN 1 AND 300),
    requested_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    completed_at timestamptz NULL
);
