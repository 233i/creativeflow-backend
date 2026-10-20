namespace CreativeFlow.Api.Http.Contracts;

/// <param name="Error">失败详情或错误分类，具体格式沿用所属契约。</param>
/// <param name="Details">按当前错误协议返回的详细说明。</param>
public sealed record ErrorResponse(string Error, string[] Details);

/// <param name="Name">实体或配置项的名称。</param>
/// <param name="Healthy">该服务或整体依赖是否通过健康检查。</param>
/// <param name="Error">失败详情或错误分类，具体格式沿用所属契约。</param>
/// <param name="ElapsedMs">以毫秒计的耗时。</param>
public sealed record HealthEntry(string Name, bool Healthy, string? Error, double ElapsedMs);

/// <param name="Status">当前对象的业务状态，存储和接口名称保持既有映射。</param>
/// <param name="Dependencies">依赖服务或上游节点的集合。</param>
public sealed record HealthReport(string Status, HealthEntry[] Dependencies);

/// <param name="Service">健康检查或系统信息中的服务名称。</param>
/// <param name="Environment">进程运行环境或环境变量配置。</param>
/// <param name="Version">实体、发布版本或协议的版本号。</param>
public sealed record SystemInfoResponse(string Service, string Environment, string Version);
