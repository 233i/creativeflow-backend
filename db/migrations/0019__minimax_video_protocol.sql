-- Add an explicit MiniMax video protocol so models can select its request format independently
-- from the provider's inherited protocol.

ALTER TABLE ai_provider DROP CONSTRAINT IF EXISTS ai_provider_type_check;
ALTER TABLE ai_provider ADD CONSTRAINT ai_provider_type_check CHECK (type IN (
    'openai-chat', 'openai-responses', 'anthropic', 'gemini',
    'openai-images', 'openai-video', 'minimax-video', 'seedance-video', 'xai-video'));

ALTER TABLE ai_model DROP CONSTRAINT IF EXISTS ai_model_type_check;
ALTER TABLE ai_model ADD CONSTRAINT ai_model_type_check CHECK (type IS NULL OR type IN (
    'openai-chat', 'openai-responses', 'anthropic', 'gemini',
    'openai-images', 'openai-video', 'minimax-video', 'seedance-video', 'xai-video'));
