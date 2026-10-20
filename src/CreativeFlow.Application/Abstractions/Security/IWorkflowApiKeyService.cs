namespace CreativeFlow.Application.Abstractions.Security;

/// <param name="Plaintext">仅在创建或即时使用时出现的密钥明文。</param>
/// <param name="Prefix">用于命名空间隔离、查找或验证的固定前缀。</param>
/// <param name="Hint">仅用于显示的安全提示或掩码。</param>
/// <param name="Hash">用于比较或校验的单向摘要。</param>
public sealed record WorkflowApiKeyMaterial(
    string Plaintext,
    string Prefix,
    string Hint,
    byte[] Hash);

public interface IWorkflowApiKeyService
{
    /// <summary>生成新的工作流 API Key 及安全元数据。</summary>
    WorkflowApiKeyMaterial Generate();

    /// <summary>读取格式合法的 API Key 公开查找前缀。</summary>
    string? ReadPrefix(string? plaintext);

    /// <summary>计算用于凭据保存或验证的单向摘要。</summary>
    byte[] Hash(string plaintext);

    /// <summary>根据既有密码或 Key 格式验证输入是否匹配保存的凭据。</summary>
    bool Verify(string plaintext, byte[] expectedHash);
}
