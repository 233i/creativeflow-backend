namespace CreativeFlow.Application.Abstractions.Security;

/// <summary>
/// 单向密码哈希边界。实现必须在存储值中嵌入格式和计算强度，
/// 以便在成功登录后升级凭据。
/// </summary>
public interface IPasswordCredentialHasher
{
    /// <summary>生成可版本化验证的密码凭据哈希。</summary>
    string Hash(string password);

    /// <summary>验证密码并返回是否需要更新哈希格式的信息。</summary>
    PasswordHashVerification Verify(string? passwordHash, string providedPassword);
}

public enum PasswordHashVerification
{
    /// <summary>已经失败并保存失败信息。</summary>
    Failed,
    /// <summary>已经成功完成。</summary>
    Succeeded,
    /// <summary>密码验证成功，但保存的哈希需要更新到当前格式。</summary>
    SucceededRehashNeeded,
}
