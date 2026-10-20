-- Explicit forks retain their source without turning a copy into shared publication state.
-- No foreign key: deleting a source must not erase its provenance or cascade to the new workflow.
ALTER TABLE workflow ADD COLUMN copied_from_source text NULL,
    ADD COLUMN copied_from_id uuid NULL;
ALTER TABLE workflow ADD CONSTRAINT workflow_copy_origin_valid CHECK (
    (copied_from_source IS NULL AND copied_from_id IS NULL)
    OR (copied_from_source IS NOT NULL AND copied_from_source IN ('workflow', 'template') AND copied_from_id IS NOT NULL)
);
