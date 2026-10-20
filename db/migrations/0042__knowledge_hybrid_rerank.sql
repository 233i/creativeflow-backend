-- Independent reranker purpose; media and chat protocols keep their existing values.
ALTER TABLE ai_provider DROP CONSTRAINT IF EXISTS ai_provider_type_check;
ALTER TABLE ai_provider ADD CONSTRAINT ai_provider_type_check CHECK (type IN (
    'openai-chat','openai-responses','anthropic','gemini','openai-images','openai-video',
    'minimax-video','minimax-h3','seedance-video','xai-video','openai-embeddings','rerank'));
ALTER TABLE ai_model DROP CONSTRAINT IF EXISTS ai_model_type_check;
ALTER TABLE ai_model ADD CONSTRAINT ai_model_type_check CHECK (type IS NULL OR type IN (
    'openai-chat','openai-responses','anthropic','gemini','openai-images','openai-video',
    'minimax-video','minimax-h3','seedance-video','xai-video','openai-embeddings','rerank'));
ALTER TABLE ai_model DROP CONSTRAINT IF EXISTS ai_model_category_check;
ALTER TABLE ai_model ADD CONSTRAINT ai_model_category_check CHECK (category IN ('chat','image','video','embedding','speech','rerank'));

CREATE TABLE ai_rerank_configuration (
    model_id uuid NOT NULL REFERENCES ai_model(model_id) ON DELETE CASCADE,
    version uuid NOT NULL,
    settings_json jsonb NOT NULL CHECK (jsonb_typeof(settings_json) = 'object'),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (model_id, version)
);
CREATE INDEX ix_ai_rerank_configuration_latest ON ai_rerank_configuration(model_id, created_at DESC, version DESC);

-- Do not silently rewrite an index referenced by a published workflow or a running task.
ALTER TABLE knowledge_version ADD COLUMN index_schema text NOT NULL DEFAULT 'dense-v1'
    CHECK (index_schema IN ('dense-v1','hybrid-v1'));
