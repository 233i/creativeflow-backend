-- A generated image may arrive with a short-lived public vendor URL. Keeping it briefly lets the
-- immediately following video node avoid another hop; the signed CreativeFlow media URL remains
-- the durable fallback for uploads, derived frames and expired vendor links.

ALTER TABLE canvas_asset
    ADD COLUMN IF NOT EXISTS source_url text NULL,
    ADD COLUMN IF NOT EXISTS source_url_expires_at timestamptz NULL;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'ck_canvas_asset_source_url_pair'
          AND conrelid = 'canvas_asset'::regclass
    ) THEN
        ALTER TABLE canvas_asset
            ADD CONSTRAINT ck_canvas_asset_source_url_pair
            CHECK ((source_url IS NULL) = (source_url_expires_at IS NULL));
    END IF;
END
$$;
