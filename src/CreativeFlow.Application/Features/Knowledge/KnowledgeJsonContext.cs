using System.Text.Json.Serialization;
using CreativeFlow.Application.Contracts.Ai;

namespace CreativeFlow.Application.Features.Knowledge;

/// <summary>知识文档与检查点的静态序列化元数据，不开启全局反射序列化。</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(KnowledgeDocumentInput[]))]
[JsonSerializable(typeof(KnowledgeChunk[]))]
[JsonSerializable(typeof(Dictionary<string, float[]>))]
[JsonSerializable(typeof(KnowledgeQuotas))]
[JsonSerializable(typeof(KnowledgeRetrievalTrace))]
[JsonSerializable(typeof(KnowledgeRetrievalSettings))]
[JsonSerializable(typeof(KnowledgeBaseResponse))]
[JsonSerializable(typeof(KnowledgeExportManifest))]
public partial class KnowledgeJsonContext : JsonSerializerContext;
