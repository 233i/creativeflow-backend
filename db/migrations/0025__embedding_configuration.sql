-- Add an explicit embeddings protocol; preserve all existing protocol values and rows.
ALTER TABLE ai_provider DROP CONSTRAINT IF EXISTS ai_provider_type_check;
ALTER TABLE ai_provider ADD CONSTRAINT ai_provider_type_check CHECK (type IN (
    'openai-chat', 'openai-responses', 'anthropic', 'gemini',
    'openai-images', 'openai-video', 'minimax-video', 'minimax-h3', 'seedance-video', 'xai-video',
    'openai-embeddings'));

ALTER TABLE ai_model DROP CONSTRAINT IF EXISTS ai_model_type_check;
ALTER TABLE ai_model ADD CONSTRAINT ai_model_type_check CHECK (type IS NULL OR type IN (
    'openai-chat', 'openai-responses', 'anthropic', 'gemini',
    'openai-images', 'openai-video', 'minimax-video', 'minimax-h3', 'seedance-video', 'xai-video',
    'openai-embeddings'));

-- Append-only non-secret settings. Existing model DTOs and default slots stay unchanged.
CREATE TABLE ai_embedding_configuration (
    model_id uuid NOT NULL REFERENCES ai_model(model_id) ON DELETE CASCADE,
    version uuid NOT NULL,
    settings_json jsonb NOT NULL CHECK (jsonb_typeof(settings_json) = 'object'),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (model_id, version)
);
CREATE INDEX ix_ai_embedding_configuration_latest
    ON ai_embedding_configuration (model_id, created_at DESC, version DESC);
