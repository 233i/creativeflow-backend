-- Seedream uses JSON image input on /images/generations, including plan URLs.
ALTER TABLE ai_provider DROP CONSTRAINT IF EXISTS ai_provider_type_check;
ALTER TABLE ai_provider ADD CONSTRAINT ai_provider_type_check CHECK (type IN (
    'openai-chat','openai-responses','anthropic','gemini','openai-images','seedream-images','openai-video',
    'minimax-video','minimax-h3','seedance-video','xai-video','openai-embeddings','rerank'));
ALTER TABLE ai_model DROP CONSTRAINT IF EXISTS ai_model_type_check;
ALTER TABLE ai_model ADD CONSTRAINT ai_model_type_check CHECK (type IS NULL OR type IN (
    'openai-chat','openai-responses','anthropic','gemini','openai-images','seedream-images','openai-video',
    'minimax-video','minimax-h3','seedance-video','xai-video','openai-embeddings','rerank'));

-- Repair only inferred OpenAI routing for identifiable native Ark Seedream models.
-- Explicit overrides, relay hosts, credentials, URLs, jobs and published snapshots stay intact.
UPDATE ai_model m SET type = 'seedream-images'
FROM ai_provider p
WHERE m.provider_id = p.provider_id AND m.category = 'image' AND m.type IS NULL
    AND m.model_key ILIKE 'doubao-seedream-%'
    AND p.type IN ('openai-chat','openai-responses','openai-images')
    AND p.base_url ~* '^https://ark(\.[a-z0-9-]+)+\.volces\.com(/|$)';
