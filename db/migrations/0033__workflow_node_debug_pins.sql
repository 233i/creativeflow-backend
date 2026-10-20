-- 独立调试只固定本次实际使用的资源，不能让未执行的草稿节点阻止调试，
-- 也不能遗漏从历史输入复用、已从草稿移走的知识版本。
CREATE OR REPLACE FUNCTION sync_knowledge_version_pins() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE record_json jsonb; graph_json jsonb; owner uuid; version_uuid uuid; item jsonb; strict_reference boolean;
BEGIN
    record_json := CASE WHEN TG_OP = 'DELETE' THEN to_jsonb(OLD) ELSE to_jsonb(NEW) END;
    owner := (record_json->>TG_ARGV[0])::uuid;
    graph_json := record_json->TG_ARGV[1];
    IF TG_TABLE_NAME = 'workflow_run' AND record_json->'input_json'->'nodeDebug' IS NOT NULL THEN
        SELECT jsonb_build_object('nodes', COALESCE(jsonb_agg(jsonb_build_object(
            'type', 'knowledgeRetrieve', 'data', jsonb_build_object('versionId', value->>'versionId'))), '[]'::jsonb))
        INTO graph_json FROM jsonb_each(COALESCE(record_json->'input_json'->'knowledge'->'references', '{}'::jsonb));
    END IF;
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
