using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CreativeFlow.Application.Abstractions.Ai;

namespace CreativeFlow.Infrastructure.Ai.Embeddings;

/// <summary>严格解析标准向量响应，通过 index 恢复顺序，拒绝部分、重复及非有限值。</summary>
internal static class OpenAiEmbeddingResponse
{
    /// <summary>验证完整批次后才返回向量；无法验证的响应不产生任何有效条目。</summary>
    public static EmbeddingResult Parse(byte[] body, EmbeddingRequest request, int statusCode)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() != request.Inputs.Count)
                return Invalid("Embedding count does not match the submitted input count.", statusCode);
            var vectors = new EmbeddingVector[request.Inputs.Count];
            int? dimensions = null;
            foreach (var item in data.EnumerateArray())
            {
                // 不依赖返回数组顺序：重复或越界 index 会破坏文档片段关联，因此整批拒绝。
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("index", out var indexValue)
                    || indexValue.ValueKind != JsonValueKind.Number || !indexValue.TryGetInt32(out var index)
                    || index < 0 || index >= vectors.Length || vectors[index] is not null
                    || !item.TryGetProperty("embedding", out var values) || values.ValueKind != JsonValueKind.Array)
                    return Invalid("Embedding response contains missing, duplicate or invalid indexes or vectors.", statusCode);
                var length = values.GetArrayLength();
                dimensions ??= length;
                if (length is < 1 or > 16384 || dimensions != length
                    || (request.Settings.ExpectedDimensions is { } expected && expected != length)
                    || (request.Settings.RequestedDimensions is { } requested && requested != length))
                    return Invalid("Embedding dimensions are inconsistent or do not match the configured dimensions.", statusCode);
                var vector = new float[length];
                var position = 0;
                var hasNonZero = false;
                foreach (var value in values.EnumerateArray())
                {
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetSingle(out var number) || !float.IsFinite(number))
                        return Invalid("Embedding vector contains an invalid or non-finite value.", statusCode);
                    vector[position++] = number;
                    hasNonZero |= number != 0;
                }
                if (!hasNonZero) return Invalid("Embedding vector must not be all zeros.", statusCode);
                vectors[index] = new(request.Inputs[index].Id, vector);
            }
            long? tokens = null;
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object
                && usage.TryGetProperty("total_tokens", out var totalTokens))
            {
                if (totalTokens.ValueKind != JsonValueKind.Number || !totalTokens.TryGetInt64(out var count) || count < 0)
                    return Invalid("Embedding usage is not a non-negative integer.", statusCode);
                tokens = count;
            }
            return new(true, vectors, dimensions, Fingerprint(request, dimensions!.Value), tokens, statusCode, null, null);
        }
        catch (JsonException) { return Invalid("Embedding service returned malformed JSON.", statusCode); }
    }

    /// <summary>用配置与实际维度生成稳定摘要，不包含密钥/文本，也不宣称验证了模型权重。</summary>
    private static string Fingerprint(EmbeddingRequest request, int dimensions)
    {
        // JSON 数组提供无歧义的字段边界，避免直接拼接造成不同配置得到同一输入串。
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var value in new[] { "openai-embeddings", request.Provider.BaseUrl.TrimEnd('/'),
                         request.Settings.EndpointPath, request.Model.ModelKey, request.Settings.ModelRevision,
                         request.Settings.DocumentPrefix, request.Settings.QueryPrefix }) writer.WriteStringValue(value);
            writer.WriteNumberValue(dimensions);
            writer.WriteEndArray();
        }
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()));
    }

    /// <summary>构造无部分向量、无上游原文的协议错误。</summary>
    private static EmbeddingResult Invalid(string error, int statusCode) =>
        EmbeddingResult.Failure("invalid_response", error, statusCode);
}
