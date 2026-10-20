-- Additive execution metadata only. Existing rows without a binding remain Legacy.
-- Business tables, public JSON and historical migration bytes remain unchanged.
CREATE TABLE agent_session_runtime (
    session_id uuid PRIMARY KEY REFERENCES agent_session(session_id) ON DELETE CASCADE,
    engine text NOT NULL CHECK (engine IN ('legacy', 'maf')),
    compatibility_version integer NOT NULL CHECK (compatibility_version > 0)
);

CREATE TABLE agent_run_runtime (
    run_id uuid PRIMARY KEY REFERENCES agent_run(run_id) ON DELETE CASCADE,
    engine text NOT NULL CHECK (engine IN ('legacy', 'maf')),
    compatibility_version integer NOT NULL CHECK (compatibility_version > 0)
);

CREATE TABLE workflow_run_runtime (
    run_id uuid PRIMARY KEY REFERENCES workflow_run(run_id) ON DELETE CASCADE,
    engine text NOT NULL CHECK (engine IN ('legacy', 'maf')),
    compatibility_version integer NOT NULL CHECK (compatibility_version > 0)
);
