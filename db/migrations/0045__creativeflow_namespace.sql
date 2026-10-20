-- CreativeFlow namespace cutover.
--
-- Historical migrations 0001-0044 intentionally keep their original Sol text. This migration
-- moves live PostgreSQL objects once, without copying or rewriting business rows. It is safe for
-- databases that already completed migration 0040: the legacy blob tables may no longer exist.
-- The application migration runner holds the global advisory lock and records this script only
-- after the transaction commits.

SET LOCAL search_path = public;

DO $$
DECLARE
    item record;
BEGIN
    FOR item IN
        SELECT * FROM (VALUES
            ('sol_account', 'creativeflow_account'),
            ('sol_external_identity', 'creativeflow_external_identity'),
            ('sol_account_device', 'creativeflow_account_device'),
            ('sol_auth_session', 'creativeflow_auth_session'),
            ('sol_oauth_transaction', 'creativeflow_oauth_transaction'),
            ('sol_password_credential', 'creativeflow_password_credential'),
            ('sol_admin', 'creativeflow_admin'),
            ('sol_admin_session', 'creativeflow_admin_session'),
            ('sol_asset_blob', 'creativeflow_asset_blob'),
            ('sol_skill_file', 'creativeflow_skill_file')
        ) AS names(old_name, new_name)
    LOOP
        IF to_regclass(item.old_name) IS NOT NULL AND to_regclass(item.new_name) IS NOT NULL THEN
            RAISE EXCEPTION
                'Namespace migration found both % and %; refusing to guess which object is authoritative',
                item.old_name, item.new_name;
        END IF;

        IF to_regclass(item.old_name) IS NOT NULL THEN
            EXECUTE format('ALTER TABLE %I RENAME TO %I', item.old_name, item.new_name);
        END IF;
    END LOOP;
END $$;

-- Renaming a table does not guarantee that every generated constraint, index or sequence name
-- follows it. Names are metadata only, but keeping them aligned avoids future migration ambiguity.
DO $$
DECLARE
    item record;
    new_name text;
BEGIN
    FOR item IN
        SELECT conrelid::regclass AS table_name, conname
        FROM pg_constraint
        WHERE connamespace = 'public'::regnamespace
          AND left(conname, 4) = 'sol_'
    LOOP
        new_name := 'creativeflow_' || substr(item.conname, 5);
        IF EXISTS (
            SELECT 1 FROM pg_constraint
            WHERE connamespace = 'public'::regnamespace AND conname = new_name
        ) THEN
            RAISE EXCEPTION 'Constraint rename collision: % and %', item.conname, new_name;
        END IF;
        EXECUTE format('ALTER TABLE %s RENAME CONSTRAINT %I TO %I', item.table_name, item.conname, new_name);
    END LOOP;

    FOR item IN
        SELECT c.relname
        FROM pg_class c
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = 'public'
          AND c.relkind IN ('i', 'S')
          AND left(c.relname, 4) = 'sol_'
    LOOP
        new_name := 'creativeflow_' || substr(item.relname, 5);
        IF EXISTS (
            SELECT 1 FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public' AND c.relname = new_name
        ) THEN
            RAISE EXCEPTION 'Relation rename collision: % and %', item.relname, new_name;
        END IF;
        IF EXISTS (
            SELECT 1 FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public' AND c.relname = item.relname AND c.relkind = 'i'
        ) THEN
            EXECUTE format('ALTER INDEX %I RENAME TO %I', item.relname, new_name);
        ELSE
            EXECUTE format('ALTER SEQUENCE %I RENAME TO %I', item.relname, new_name);
        END IF;
    END LOOP;
END $$;

-- Keep the original signatures and privileges. pg_get_functiondef is used so this migration does
-- not duplicate a function body that may have received a bug fix in 0043/0044. Replacing sol_ in
-- the captured definition updates both the function name and its internal calls; the setting key
-- is handled separately because it has no underscore.
DO $$
DECLARE
    item record;
    definition text;
    target_exists boolean;
BEGIN
    FOR item IN
        SELECT * FROM (VALUES
            ('sol_accessible_device_ids(uuid)', 'creativeflow_accessible_device_ids'),
            ('sol_prevent_retired_workflow_replay()', 'creativeflow_prevent_retired_workflow_replay'),
            ('sol_lock_live_workflow(uuid)', 'creativeflow_lock_live_workflow'),
            ('sol_guard_live_workflow_run()', 'creativeflow_guard_live_workflow_run'),
            ('sol_guard_live_workflow_deployment()', 'creativeflow_guard_live_workflow_deployment'),
            ('sol_guard_live_template_debug()', 'creativeflow_guard_live_template_debug'),
            ('sol_asset_ids(jsonb)', 'creativeflow_asset_ids'),
            ('sol_asset_reference_lock()', 'creativeflow_asset_reference_lock'),
            ('sol_replace_asset_references(text,uuid,text,jsonb,integer)', 'creativeflow_replace_asset_references'),
            ('sol_sync_asset_references()', 'creativeflow_sync_asset_references'),
            ('sol_check_file_reference()', 'creativeflow_check_file_reference'),
            ('sol_file_is_referenced(text)', 'creativeflow_file_is_referenced'),
            ('sol_append_workflow_event(uuid,text,jsonb)', 'creativeflow_append_workflow_event'),
            ('sol_finish_workflow_events(uuid)', 'creativeflow_finish_workflow_events'),
            ('sol_journal_workflow_run()', 'creativeflow_journal_workflow_run'),
            ('sol_journal_workflow_node()', 'creativeflow_journal_workflow_node'),
            ('sol_journal_workflow_text()', 'creativeflow_journal_workflow_text'),
            ('sol_finalize_workflow_journal()', 'creativeflow_finalize_workflow_journal')
        ) AS names(old_signature, new_name)
    LOOP
        target_exists := to_regprocedure(item.new_name || substring(item.old_signature from position('(' in item.old_signature))) IS NOT NULL;
        IF to_regprocedure(item.old_signature) IS NOT NULL AND target_exists THEN
            RAISE EXCEPTION
                'Function migration found both % and %; refusing to guess which object is authoritative',
                item.old_signature, item.new_name;
        END IF;

        IF to_regprocedure(item.old_signature) IS NULL THEN
            CONTINUE;
        END IF;

        SELECT pg_get_functiondef(to_regprocedure(item.old_signature)) INTO definition;
        EXECUTE format('ALTER FUNCTION %s RENAME TO %I', item.old_signature, item.new_name);
        definition := replace(definition, 'sol_', 'creativeflow_');
        definition := replace(definition, 'sol.account_id', 'creativeflow.account_id');
        EXECUTE definition;
    END LOOP;
END $$;

-- Fail before recording the migration if any legacy executable reference remains. This catches a
-- missed function or a mixed manual state while the transaction can still roll back cleanly.
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM pg_proc p
        JOIN pg_namespace n ON n.oid = p.pronamespace
        WHERE n.nspname = 'public' AND left(p.proname, 4) = 'sol_'
    ) THEN
        RAISE EXCEPTION 'Legacy sol_* function remains after CreativeFlow namespace migration';
    END IF;

    IF EXISTS (
        SELECT 1 FROM pg_class c
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p', 'v', 'm') AND left(c.relname, 4) = 'sol_'
    ) THEN
        RAISE EXCEPTION 'Legacy sol_* relation remains after CreativeFlow namespace migration';
    END IF;
END $$;
