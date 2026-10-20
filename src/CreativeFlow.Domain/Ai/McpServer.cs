using CreativeFlow.Domain.Identity;

namespace CreativeFlow.Domain.Ai;

/// <param name="Value">底层 UUID 值，保持现有标识编码与持久化格式。</param>
public readonly record struct McpServerId(Guid Value)
{
    /// <summary>生成新的 UUIDv7 标识，保留当前持久化排序特性。</summary>
    public static McpServerId New() => new(Guid.CreateVersion7());

    /// <summary>解析非空 UUID 标识；无效或全零输入返回失败并清空输出。</summary>
    public static bool TryParse(string? text, out McpServerId id)
    {
        if (Guid.TryParse(text, out var guid) && guid != Guid.Empty)
        {
            id = new McpServerId(guid);
            return true;
        }

        id = default;
        return false;
    }

    /// <summary>以既有标准 UUID 字符串格式输出该标识。</summary>
    public override string ToString() => Value.ToString();
}

/// <summary>
/// 一条 MCP 服务器注册信息。
/// </summary>
/// <remarks>
/// 不同传输方式使用两组不同的有效字段：<see cref="McpTransport.Stdio"/> 使用
/// <see cref="Command"/>/<see cref="Args"/>/<see cref="Env"/>/<see cref="Cwd"/>，HTTP 传输则使用
/// <see cref="Url"/>/<see cref="Headers"/>。验证逻辑会强制遵守这一划分。
/// <para>
/// <see cref="Args"/>、<see cref="Env"/> 和 <see cref="Headers"/> 以 JSON 字符串而非集合存储：
/// 它们以 jsonb 跨越 Dapper 边界，而 Dapper.AOT 只支持静态类型的标量参数。
/// </para>
/// </remarks>
/// <param name="Id">当前实体或契约项目的唯一标识。</param>
/// <param name="DeviceId">资源或操作关联的设备标识，访问范围按现有账号与设备规则判定。</param>
/// <param name="Name">实体或配置项的名称。</param>
/// <param name="Description">面向用户或模型展示的说明文本。</param>
/// <param name="Enabled">当前配置或资源是否启用。</param>
/// <param name="Transport">MCP 服务使用的传输方式。</param>
/// <param name="Command">已配置的进程或脚本入口命令。</param>
/// <param name="ArgsJson">以 JSON 保存的进程参数列表。</param>
/// <param name="EnvJson">以 JSON 保存的环境变量配置；敏感值通过既有密钥机制保护。</param>
/// <param name="Cwd">进程执行时使用的工作目录。</param>
/// <param name="Url">资源的交付或访问地址。</param>
/// <param name="HeadersJson">以 JSON 保存的请求头配置，秘密值不直接回传。</param>
/// <param name="AutoFallback">是否允许按既有策略选择回退来源。</param>
/// <param name="CreatedAt">记录创建时间，沿用当前 UTC 与序列化约定。</param>
/// <param name="UpdatedAt">记录最近一次修改的 UTC 时间。</param>
public sealed record McpServer(
    McpServerId Id,
    DeviceId DeviceId,
    string Name,
    string? Description,
    bool Enabled,
    McpTransport Transport,
    string? Command,
    string ArgsJson,
    string EnvJson,
    string? Cwd,
    string? Url,
    string HeadersJson,
    bool AutoFallback,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
