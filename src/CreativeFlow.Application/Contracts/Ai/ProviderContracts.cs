namespace CreativeFlow.Application.Contracts.Ai;

/// <summary>
/// 返回给浏览器的供应商数据。
/// </summary>
/// <remarks>
/// 这里刻意不提供 <c>ApiKey</c> 字段。已存储密钥绝不会被序列化——调用方只会得到
/// <see cref="HasApiKey"/> 和 <see cref="ApiKeyHint"/>，这已足够渲染设置界面。
/// 若在此加入明文字段，一次响应就会把所有密钥泄露给浏览器。
/// </remarks>
/// <param name="Id">当前实体或契约项目的唯一标识。</param>
/// <param name="BuiltinId">内置渠道预设的稳定标识，非内置项可为空。</param>
/// <param name="Name">实体或配置项的名称。</param>
/// <param name="Description">面向用户或模型展示的说明文本。</param>
/// <param name="Icon">显示用图标标识或地址。</param>
/// <param name="Type">该数据项的协议、值或事件类型，沿用所属契约的取值。</param>
/// <param name="BaseUrl">调用方配置并规范化后的上游基础地址。</param>
/// <param name="Enabled">当前配置或资源是否启用。</param>
/// <param name="HasApiKey">是否已配置可用的密钥记录。</param>
/// <param name="ApiKeyHint">可公开展示的密钥掩码。</param>
/// <param name="SortOrder">界面显示及列表查询使用的排序值。</param>
/// <param name="PresetVersion">当前内置渠道预设的版本号。</param>
/// <param name="CreatedAt">记录创建时间，沿用当前 UTC 与序列化约定。</param>
/// <param name="UpdatedAt">记录最近一次修改的 UTC 时间。</param>
/// <param name="Models">当前渠道配置的模型集合。</param>
public sealed record ProviderResponse(
    string Id,
    string? BuiltinId,
    string Name,
    string? Description,
    string? Icon,
    string Type,
    string BaseUrl,
    bool Enabled,
    bool HasApiKey,
    string? ApiKeyHint,
    int SortOrder,
    int? PresetVersion,
    string CreatedAt,
    string UpdatedAt,
    ModelResponse[] Models);

/// <param name="Type">协议覆盖；空值表示模型跟随其供应商的协议。</param>
/// <param name="Id">当前实体或契约项目的唯一标识。</param>
/// <param name="ModelKey">上游识别的模型名称，不等同于本地模型配置标识。</param>
/// <param name="Name">实体或配置项的名称。</param>
/// <param name="Enabled">当前配置或资源是否启用。</param>
/// <param name="Category">所属的模型、模板或内容分类。</param>
/// <param name="Icon">显示用图标标识或地址。</param>
/// <param name="ContextLength">模型声明的上下文 token 容量。</param>
/// <param name="MaxOutputTokens">模型调用允许的最大输出 token 数量。</param>
/// <param name="SupportsVision">模型是否声明支持图片输入。</param>
/// <param name="SupportsFunctionCall">模型是否声明支持工具/函数调用。</param>
/// <param name="SupportsThinking">模型是否声明支持思考内容。</param>
/// <param name="SortOrder">界面显示及列表查询使用的排序值。</param>
public sealed record ModelResponse(
    string Id,
    string ModelKey,
    string Name,
    bool Enabled,
    string? Type,
    string Category,
    string? Icon,
    int? ContextLength,
    int? MaxOutputTokens,
    bool SupportsVision,
    bool SupportsFunctionCall,
    bool SupportsThinking,
    int SortOrder);

/// <param name="Providers">当前可用的渠道或身份提供方集合。</param>
public sealed record ProviderListResponse(ProviderResponse[] Providers);

/// <param name="BuiltinId">内置渠道预设的稳定标识，非内置项可为空。</param>
/// <param name="Name">实体或配置项的名称。</param>
/// <param name="Description">面向用户或模型展示的说明文本。</param>
/// <param name="Icon">显示用图标标识或地址。</param>
/// <param name="Type">该数据项的协议、值或事件类型，沿用所属契约的取值。</param>
/// <param name="BaseUrl">调用方配置并规范化后的上游基础地址。</param>
/// <param name="ApiKey">渠道调用密钥；ApiKeySecret 类型为受保护的持久化表示，字符串类型仅用于请求或执行。</param>
/// <param name="Enabled">当前配置或资源是否启用。</param>
/// <param name="PresetVersion">当前内置渠道预设的版本号。</param>
/// <param name="Models">当前渠道配置的模型集合。</param>
public sealed record CreateProviderRequest(
    string? BuiltinId,
    string Name,
    string? Description,
    string? Icon,
    string Type,
    string BaseUrl,
    string? ApiKey,
    bool? Enabled,
    int? PresetVersion,
    CreateModelRequest[]? Models);

/// <summary>
/// 供应商的部分更新。所有字段均可选；空值表示“保持不变”。
/// </summary>
/// <param name="ApiKey">
/// 空值保留已存储密钥——这是常见情况，因为大多数编辑只修改基础 URL 或名称。
/// 空字符串会清除密钥，其他值则替换密钥。
/// </param>
/// <param name="Name">实体或配置项的名称。</param>
/// <param name="Description">面向用户或模型展示的说明文本。</param>
/// <param name="Icon">显示用图标标识或地址。</param>
/// <param name="Type">该数据项的协议、值或事件类型，沿用所属契约的取值。</param>
/// <param name="BaseUrl">调用方配置并规范化后的上游基础地址。</param>
/// <param name="Enabled">当前配置或资源是否启用。</param>
public sealed record UpdateProviderRequest(
    string? Name,
    string? Description,
    string? Icon,
    string? Type,
    string? BaseUrl,
    string? ApiKey,
    bool? Enabled);

/// <param name="ModelKey">上游识别的模型名称，不等同于本地模型配置标识。</param>
/// <param name="Name">实体或配置项的名称。</param>
/// <param name="Type">该数据项的协议、值或事件类型，沿用所属契约的取值。</param>
/// <param name="Category">所属的模型、模板或内容分类。</param>
/// <param name="Icon">显示用图标标识或地址。</param>
/// <param name="ContextLength">模型声明的上下文 token 容量。</param>
/// <param name="MaxOutputTokens">模型调用允许的最大输出 token 数量。</param>
/// <param name="SupportsVision">模型是否声明支持图片输入。</param>
/// <param name="SupportsFunctionCall">模型是否声明支持工具/函数调用。</param>
/// <param name="SupportsThinking">模型是否声明支持思考内容。</param>
/// <param name="Enabled">当前配置或资源是否启用。</param>
public sealed record CreateModelRequest(
    string ModelKey,
    string? Name,
    string? Type,
    string? Category,
    string? Icon,
    int? ContextLength,
    int? MaxOutputTokens,
    bool? SupportsVision,
    bool? SupportsFunctionCall,
    bool? SupportsThinking,
    bool? Enabled);

/// <param name="Name">实体或配置项的名称。</param>
/// <param name="Type">该数据项的协议、值或事件类型，沿用所属契约的取值。</param>
/// <param name="Category">所属的模型、模板或内容分类。</param>
/// <param name="Icon">显示用图标标识或地址。</param>
/// <param name="ContextLength">模型声明的上下文 token 容量。</param>
/// <param name="MaxOutputTokens">模型调用允许的最大输出 token 数量。</param>
/// <param name="SupportsVision">模型是否声明支持图片输入。</param>
/// <param name="SupportsFunctionCall">模型是否声明支持工具/函数调用。</param>
/// <param name="SupportsThinking">模型是否声明支持思考内容。</param>
/// <param name="Enabled">当前配置或资源是否启用。</param>
public sealed record UpdateModelRequest(
    string? Name,
    string? Type,
    string? Category,
    string? Icon,
    int? ContextLength,
    int? MaxOutputTokens,
    bool? SupportsVision,
    bool? SupportsFunctionCall,
    bool? SupportsThinking,
    bool? Enabled);

/// <summary>针对供应商上游端点的连接测试结果。</summary>
/// <param name="Ok">当前调用是否成功。</param>
/// <param name="StatusCode">上游或 HTTP 调用的状态码。</param>
/// <param name="Error">失败详情或错误分类，具体格式沿用所属契约。</param>
/// <param name="ModelCount">当前渠道或导入结果包含的模型数量。</param>
public sealed record ProviderCheckResponse(bool Ok, int? StatusCode, string? Error, int? ModelCount);

/// <summary>通过查询供应商自身目录端点发现的模型。</summary>
/// <param name="ModelKey">上游识别的模型名称，不等同于本地模型配置标识。</param>
/// <param name="Name">实体或配置项的名称。</param>
/// <param name="Category">所属的模型、模板或内容分类。</param>
/// <param name="Icon">显示用图标标识或地址。</param>
public sealed record DiscoveredModel(string ModelKey, string Name, string Category, string? Icon);

/// <param name="Models">当前渠道配置的模型集合。</param>
public sealed record DiscoveredModelsResponse(DiscoveredModel[] Models);

/// <summary>添加一组已发现模型，并跳过供应商已有的模型。</summary>
/// <param name="Models">当前渠道配置的模型集合。</param>
public sealed record ImportModelsRequest(CreateModelRequest[] Models);

/// <param name="Added">本次新增的记录数量。</param>
public sealed record ImportModelsResponse(int Added);

// --- URL 快速配置导入 ---

/// <param name="Providers">当前可用的渠道或身份提供方集合。</param>
public sealed record ImportConfigRequest(ImportProviderEntry[] Providers);

/// <param name="BuiltinId">内置渠道预设的稳定标识，非内置项可为空。</param>
/// <param name="Name">实体或配置项的名称。</param>
/// <param name="Description">面向用户或模型展示的说明文本。</param>
/// <param name="Icon">显示用图标标识或地址。</param>
/// <param name="PresetVersion">当前内置渠道预设的版本号。</param>
/// <param name="Type">该数据项的协议、值或事件类型，沿用所属契约的取值。</param>
/// <param name="ApiKey">渠道调用密钥；ApiKeySecret 类型为受保护的持久化表示，字符串类型仅用于请求或执行。</param>
/// <param name="BaseUrl">调用方配置并规范化后的上游基础地址。</param>
/// <param name="Models">当前渠道配置的模型集合。</param>
public sealed record ImportProviderEntry(
    string? BuiltinId,
    string? Name,
    string? Description,
    string? Icon,
    int? PresetVersion,
    string? Type,
    string? ApiKey,
    string? BaseUrl,
    CreateModelRequest[]? Models);

/// <param name="Created">本次新创建的项目。</param>
/// <param name="Updated">本次更新成功的记录数量。</param>
public sealed record ImportConfigResponse(int Created, int Updated);

// --- 应用级模型角色配置 ---

/// <param name="Kind">当前资源或数据项的种类。</param>
/// <param name="ProviderId">关联的 AI 渠道标识。</param>
/// <param name="ModelId">本地模型配置标识。</param>
/// <param name="UpdatedAt">记录最近一次修改的 UTC 时间。</param>
public sealed record ModelSlotResponse(
    string Kind,
    string? ProviderId,
    string? ModelId,
    string UpdatedAt);

/// <param name="Slots">管理员配置的默认模型角色集合。</param>
public sealed record ModelSlotListResponse(ModelSlotResponse[] Slots);

/// <param name="ProviderId">关联的 AI 渠道标识。</param>
/// <param name="ModelId">本地模型配置标识。</param>
public sealed record UpdateModelSlotRequest(string? ProviderId, string? ModelId);
