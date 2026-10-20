-- Semantic chunk metadata. Existing versions keep their old boundaries and remain readable;
-- new imports bind the parser version in the version row and store source offsets explicitly.
ALTER TABLE knowledge_version
    ADD COLUMN parser_version text NOT NULL DEFAULT 'utf8-lines-v2';

ALTER TABLE knowledge_chunk
    ADD COLUMN start_offset integer NOT NULL DEFAULT 0,
    ADD COLUMN end_offset integer NOT NULL DEFAULT 0,
    ADD COLUMN section_path text NOT NULL DEFAULT '',
    ADD COLUMN block_type text NOT NULL DEFAULT 'paragraph',
    ADD COLUMN continuation boolean NOT NULL DEFAULT false;

CREATE INDEX knowledge_chunk_document_ordinal ON knowledge_chunk(version_id, document_id, ordinal);
