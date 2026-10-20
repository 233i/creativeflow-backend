-- Cached media derivatives produced from stored canvas assets.
--
-- A derived frame remains a normal image asset so it can be previewed, grouped and reused by
-- every existing image-input path. Deleting the source only severs the cache relation: the image
-- may already be referenced by a canvas or workflow and must remain usable in its own right.

ALTER TABLE canvas_asset
    ADD COLUMN IF NOT EXISTS source_asset_id uuid NULL
        REFERENCES canvas_asset(asset_id) ON DELETE SET NULL,
    ADD COLUMN IF NOT EXISTS derivation text NULL;

CREATE UNIQUE INDEX IF NOT EXISTS uq_canvas_asset_derivative
    ON canvas_asset (source_asset_id, derivation)
    WHERE source_asset_id IS NOT NULL AND derivation IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_canvas_asset_source
    ON canvas_asset (source_asset_id)
    WHERE source_asset_id IS NOT NULL;
