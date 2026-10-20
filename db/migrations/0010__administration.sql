-- Commercial administration boundary.
--
-- A NULL ai_provider.device_id identifies the single application-wide provider catalog. Legacy
-- per-device rows remain intact for rollback safety, but application reads now use only the
-- global catalog and only an authenticated administrator may mutate it.

ALTER TABLE sol_account
    ADD COLUMN IF NOT EXISTS is_enabled boolean NOT NULL DEFAULT true;

ALTER TABLE ai_provider
    ALTER COLUMN device_id DROP NOT NULL;

-- Preserve the most recently edited copy of every existing provider name as the initial global
-- catalog. Other per-device copies remain untouched and can be recovered manually if needed.
WITH ranked_providers AS (
    SELECT provider_id,
           row_number() OVER (
               PARTITION BY lower(name)
               ORDER BY updated_at DESC, created_at DESC, provider_id DESC
           ) AS rank
    FROM ai_provider
)
UPDATE ai_provider AS provider
SET device_id = NULL
FROM ranked_providers AS ranked
WHERE provider.provider_id = ranked.provider_id
  AND ranked.rank = 1;

CREATE UNIQUE INDEX IF NOT EXISTS uq_ai_provider_global_name
    ON ai_provider (lower(name))
    WHERE device_id IS NULL;

CREATE INDEX IF NOT EXISTS ix_ai_provider_global_order
    ON ai_provider (sort_order, created_at)
    WHERE device_id IS NULL;

CREATE TABLE IF NOT EXISTS sol_admin (
    admin_id             uuid PRIMARY KEY,
    username             text NOT NULL,
    password_hash        text NOT NULL,
    must_change_password boolean NOT NULL DEFAULT true,
    created_at           timestamptz NOT NULL,
    updated_at           timestamptz NOT NULL,

    CONSTRAINT uq_sol_admin_username UNIQUE (username),
    CONSTRAINT ck_sol_admin_username_length CHECK (char_length(username) BETWEEN 3 AND 64),
    CONSTRAINT ck_sol_admin_password_hash_length
        CHECK (char_length(password_hash) BETWEEN 20 AND 2048)
);

CREATE TABLE IF NOT EXISTS sol_admin_session (
    session_id   uuid PRIMARY KEY,
    admin_id     uuid NOT NULL REFERENCES sol_admin(admin_id) ON DELETE CASCADE,
    token_hash   bytea NOT NULL UNIQUE,
    created_at   timestamptz NOT NULL,
    expires_at   timestamptz NOT NULL,
    last_seen_at timestamptz NOT NULL,
    user_agent   text NULL,
    ip_address   inet NULL
);

CREATE INDEX IF NOT EXISTS ix_sol_admin_session_admin
    ON sol_admin_session (admin_id, expires_at);

CREATE TABLE IF NOT EXISTS ai_model_slot (
    slot_kind   text PRIMARY KEY CHECK (slot_kind IN ('chat', 'fast', 'image', 'video')),
    provider_id uuid NULL REFERENCES ai_provider(provider_id) ON DELETE SET NULL,
    model_id    uuid NULL REFERENCES ai_model(model_id) ON DELETE SET NULL,
    updated_at  timestamptz NOT NULL DEFAULT now()
);

INSERT INTO ai_model_slot (slot_kind)
VALUES ('chat'), ('fast'), ('image'), ('video')
ON CONFLICT (slot_kind) DO NOTHING;
