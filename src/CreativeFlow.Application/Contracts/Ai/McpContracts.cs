namespace CreativeFlow.Application.Contracts.Ai;

/// <param name="Id">当前实体或契约项目的唯一标识。</param>
/// <param name="Name">实体或配置项的名称。</param>
/// <param name="Description">面向用户或模型展示的说明文本。</param>
/// <param name="Enabled">当前配置或资源是否启用。</param>
/// <param name="Transport">MCP 服务使用的传输方式。</param>
/// <param name="Command">已配置的进程或脚本入口命令。</param>
/// <param name="Args">进程启动参数列表。</param>
/// <param name="Env">传给子进程的环境变量项。</param>
/// <param name="Cwd">进程执行时使用的工作目录。</param>
/// <param name="Url">资源的交付或访问地址。</param>
/// <param name="Headers">调用上游时附加的请求头配置。</param>
/// <param name="AutoFallback">是否允许按既有策略选择回退来源。</param>
/// <param name="CreatedAt">记录创建时间，沿用当前 UTC 与序列化约定。</param>
/// <param name="UpdatedAt">记录最近一次修改的 UTC 时间。</param>
public sealed record McpServerResponse(
    string Id,
    string Name,
    string? Description,
    bool Enabled,
    string Transport,
    string? Command,
    string[] Args,
    McpSecretEntry[] Env,
    string? Cwd,
    string? Url,
    McpSecretEntry[] Headers,
    bool AutoFallback,
    string CreatedAt,
    string UpdatedAt);

/// <summary>
/// 一组环境变量或请求头配置的键值对。
/// </summary>
/// <remarks>
/// 使用键值对数组而非字典：<c>Dictionary&lt;string, string&gt;</c> 在源生成模式下可正常序列化，
/// 但数组能让设置界面的顺序保持稳定。该界面会将这些数据渲染为可编辑列表，
/// 若每次保存后行顺序都变化，会让用户困惑。
/// </remarks>
/// <param name="Key">当前查找、签名或映射所使用的键。</param>
/// <param name="Value">当前值对象或参数封装的底层值。</param>
public sealed record McpEnvEntry(string Key, string Value);

/// <summary>返回浏览器的秘密元数据；秘密值本身绝不会离开服务器。</summary>
/// <param name="Key">当前查找、签名或映射所使用的键。</param>
/// <param name="IsConfigured">对应能力或模型角色是否已配置。</param>
/// <param name="Hint">仅用于显示的安全提示或掩码。</param>
public sealed record McpSecretEntry(string Key, bool IsConfigured, string Hint);

/// <param name="Servers">当前可用的 MCP 服务配置集合。</param>
public sealed record McpServerListResponse(McpServerResponse[] Servers);

/// <param name="Name">实体或配置项的名称。</param>
/// <param name="Description">面向用户或模型展示的说明文本。</param>
/// <param name="Transport">MCP 服务使用的传输方式。</param>
/// <param name="Command">已配置的进程或脚本入口命令。</param>
/// <param name="Args">进程启动参数列表。</param>
/// <param name="Env">传给子进程的环境变量项。</param>
/// <param name="Cwd">进程执行时使用的工作目录。</param>
/// <param name="Url">资源的交付或访问地址。</param>
/// <param name="Headers">调用上游时附加的请求头配置。</param>
/// <param name="AutoFallback">是否允许按既有策略选择回退来源。</param>
/// <param name="Enabled">当前配置或资源是否启用。</param>
public sealed record CreateMcpServerRequest(
    string Name,
    string? Description,
    string Transport,
    string? Command,
    string[]? Args,
    McpEnvEntry[]? Env,
    string? Cwd,
    string? Url,
    McpEnvEntry[]? Headers,
    bool? AutoFallback,
    bool? Enabled);

/// <param name="Name">实体或配置项的名称。</param>
/// <param name="Description">面向用户或模型展示的说明文本。</param>
/// <param name="Transport">MCP 服务使用的传输方式。</param>
/// <param name="Command">已配置的进程或脚本入口命令。</param>
/// <param name="Args">进程启动参数列表。</param>
/// <param name="Env">传给子进程的环境变量项。</param>
/// <param name="Cwd">进程执行时使用的工作目录。</param>
/// <param name="Url">资源的交付或访问地址。</param>
/// <param name="Headers">调用上游时附加的请求头配置。</param>
/// <param name="AutoFallback">是否允许按既有策略选择回退来源。</param>
/// <param name="Enabled">当前配置或资源是否启用。</param>
public sealed record UpdateMcpServerRequest(
    string? Name,
    string? Description,
    string? Transport,
    string? Command,
    string[]? Args,
    McpEnvEntry[]? Env,
    string? Cwd,
    string? Url,
    McpEnvEntry[]? Headers,
    bool? AutoFallback,
    bool? Enabled);

/// <summary>批量导入；名称已存在的服务器会被跳过。</summary>
/// <param name="Servers">当前可用的 MCP 服务配置集合。</param>
public sealed record ImportMcpServersRequest(CreateMcpServerRequest[] Servers);

/// <param name="Added">本次新增的记录数量。</param>
public sealed record ImportMcpServersResponse(int Added);

/// <param name="Ok">当前调用是否成功。</param>
/// <param name="Error">失败详情或错误分类，具体格式沿用所属契约。</param>
/// <param name="ToolCount">本次发现或提供给模型的工具数量。</param>
public sealed record McpServerCheckResponse(bool Ok, string? Error, int ToolCount);

/// <param name="Name">实体或配置项的名称。</param>
/// <param name="Description">面向用户或模型展示的说明文本。</param>
/// <param name="InputSchemaJson">对外输入契约的 JSON schema。</param>
public sealed record McpToolResponse(string Name, string Description, string InputSchemaJson);

/// <param name="Tools">模型可用的工具或 MCP 工具目录。</param>
public sealed record McpToolListResponse(McpToolResponse[] Tools);
