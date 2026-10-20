-- Keep the OpenAI-compatible MiniMax route separate from the official H3 V2 contract.
ALTER TABLE ai_provider DROP CONSTRAINT IF EXISTS ai_provider_type_check;
ALTER TABLE ai_provider ADD CONSTRAINT ai_provider_type_check CHECK (type IN (
    'openai-chat', 'openai-responses', 'anthropic', 'gemini',
    'openai-images', 'openai-video', 'minimax-video', 'minimax-h3', 'seedance-video', 'xai-video'));

ALTER TABLE ai_model DROP CONSTRAINT IF EXISTS ai_model_type_check;
ALTER TABLE ai_model ADD CONSTRAINT ai_model_type_check CHECK (type IS NULL OR type IN (
    'openai-chat', 'openai-responses', 'anthropic', 'gemini',
    'openai-images', 'openai-video', 'minimax-video', 'minimax-h3', 'seedance-video', 'xai-video'));
