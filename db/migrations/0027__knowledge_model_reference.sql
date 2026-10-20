-- Knowledge snapshots retain model IDs even after the original model is removed.
-- Do not change existing model/provider deletion semantics by introducing a new blocking FK.
-- Runtime model resolution already fails closed if that model is missing or disabled.
ALTER TABLE knowledge_base DROP CONSTRAINT knowledge_base_model_id_fkey;
