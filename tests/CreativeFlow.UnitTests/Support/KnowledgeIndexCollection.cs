namespace CreativeFlow.UnitTests.Support;

/// <summary>PostgreSQL advisory lock 属于数据库而不是 schema；索引集成测试串行运行，仍使用各自随机 schema 和真实锁。</summary>
[CollectionDefinition("Knowledge index database lock")]
public sealed class KnowledgeIndexCollection;
