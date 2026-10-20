using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace CreativeFlow.Api.Extensions;

public static class SerilogSetup
{
    /// <summary>
    /// 在代码中配置 Serilog。
    /// </summary>
    /// <remarks>
    /// 不使用 <c>ReadFrom.Configuration(...)</c>，让 sink、格式与 enrichers 在代码审查中显式可见，
    /// 并避免不同部署环境因程序集扫描范围不同而产生日志行为差异。日志级别仍取自配置。
    /// </remarks>
    public static void ConfigureSerilog(this WebApplicationBuilder builder)
    {
        var defaultLevel = ParseLevel(
            builder.Configuration["Logging:LogLevel:Default"], LogEventLevel.Information);
        var aspNetLevel = ParseLevel(
            builder.Configuration["Logging:LogLevel:Microsoft.AspNetCore"], LogEventLevel.Warning);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(defaultLevel)
            .MinimumLevel.Override("Microsoft.AspNetCore", aspNetLevel)
            .MinimumLevel.Override("Microsoft.Extensions.Http", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("service", "creativeflow-api")
            .WriteTo.Console(new CompactJsonFormatter())
            .CreateLogger();

        builder.Logging.ClearProviders();
        builder.Services.AddSerilog();
    }

    /// <summary>将日志级别配置转换为既有默认规则下的枚举值。</summary>
    private static LogEventLevel ParseLevel(string? value, LogEventLevel fallback) => value switch
    {
        "Trace" => LogEventLevel.Verbose,
        "Debug" => LogEventLevel.Debug,
        "Information" => LogEventLevel.Information,
        "Warning" => LogEventLevel.Warning,
        "Error" => LogEventLevel.Error,
        "Critical" => LogEventLevel.Fatal,
        _ => fallback,
    };
}
