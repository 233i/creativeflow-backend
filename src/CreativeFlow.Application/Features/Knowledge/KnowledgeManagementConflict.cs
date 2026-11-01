namespace CreativeFlow.Application.Features.Knowledge;

/// <summary>管理确认已经过期，客户端应重新读取影响范围，不自动重发写操作。</summary>
public sealed class KnowledgeManagementConflict(string message) : Exception(message);
