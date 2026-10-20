-- Application-wide image generation policy controlled by the administrator.
--
-- The singleton row keeps the upstream response format out of canvas documents and user
-- requests. Image bytes are still copied into Sol's asset store regardless of whether the
-- provider returns a temporary URL or inline base64 data.

CREATE TABLE IF NOT EXISTS ai_generation_settings (
    settings_id          smallint PRIMARY KEY DEFAULT 1,
    image_response_format text NOT NULL DEFAULT 'url',
    updated_at           timestamptz NOT NULL DEFAULT now(),

    CONSTRAINT ck_ai_generation_settings_singleton CHECK (settings_id = 1),
    CONSTRAINT ck_ai_generation_settings_image_response_format
        CHECK (image_response_format IN ('url', 'base64'))
);

INSERT INTO ai_generation_settings (settings_id)
VALUES (1)
ON CONFLICT (settings_id) DO NOTHING;
