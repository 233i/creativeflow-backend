using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace CreativeFlow.Api.Extensions;

public static class OpenTelemetrySetup
{
    /// <summary>
    /// 接入跟踪和指标。仅在配置端点时注册 OTLP 导出器，
    /// 因此本地开发无需运行收集器。
    /// </summary>
    /// <remarks>
    /// 刻意不引入 Npgsql 和 StackExchange.Redis 检测包，以控制传递依赖与默认遥测开销。
    /// 需要数据库或缓存跨度时，在仓储内部添加语义明确的 <c>ActivitySource</c>。
    /// </remarks>
    public static void ConfigureOpenTelemetry(this WebApplicationBuilder builder)
    {
        var otlpEndpoint = builder.Configuration["OpenTelemetry:OtlpEndpoint"];

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("creativeflow-api"))
            .WithTracing(tracing =>
            {
                tracing.AddAspNetCoreInstrumentation();
                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                {
                    tracing.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
                }
            })
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation();
                metrics.AddMeter("Microsoft.AspNetCore.Hosting");
                metrics.AddMeter("Microsoft.AspNetCore.Server.Kestrel");
                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                {
                    metrics.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
                }
            });
    }
}
