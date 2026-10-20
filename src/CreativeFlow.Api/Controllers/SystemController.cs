using Microsoft.AspNetCore.Mvc;
using CreativeFlow.Api.Serialization;
using CreativeFlow.Infrastructure.Health;

namespace CreativeFlow.Api.Controllers;

/// <summary>装配System的 HTTP 入口及其用例/端口依赖。</summary>
[ApiController]
[Tags("system")]
public sealed class SystemController(
    DependencyHealthProbe probe,
    IHostEnvironment environment) : ControllerBase
{
    /// <summary>存活检查只验证进程正在运行，不访问外部依赖。</summary>
    [HttpGet("/health/live", Name = "HealthLive")]
    public IActionResult Live() => ApiResults.Ok("alive");

    /// <summary>就绪检查验证当前实例能否承载流量。</summary>
    [HttpGet("/health/ready", Name = "HealthReady")]
    public async Task<IActionResult> Ready(CancellationToken ct)
    {
        var results = await probe.ProbeAllAsync(ct);
        var entries = results
            .Select(result => new HealthEntry(
                result.Name,
                result.Healthy,
                result.Error,
                Math.Round(result.ElapsedMs, 1)))
            .ToArray();

        var healthy = entries.All(entry => entry.Healthy);
        var report = new HealthReport(healthy ? "healthy" : "unhealthy", entries);

        return healthy
            ? ApiResults.Ok(report)
            : ApiResults.Json(
                report,
                ApiJsonContext.Default.HealthReport,
                statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    /// <summary>返回应用名称、环境与现有版本字段。</summary>
    [HttpGet("/api/v1/system/info", Name = "SystemInfo")]
    public IActionResult Info() => ApiResults.Ok(new SystemInfoResponse(
        "creativeflow-api",
        environment.EnvironmentName,
        "1.0.0"));
}
