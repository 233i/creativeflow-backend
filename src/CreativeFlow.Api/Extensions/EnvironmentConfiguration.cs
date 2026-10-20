using Microsoft.Extensions.Configuration.Json;

namespace CreativeFlow.Api.Extensions;

/// <summary>在标准配置层级中插入当前环境的私有文件，不重新注册或降级部署者的覆盖配置。</summary>
internal static class EnvironmentConfiguration
{
    /// <summary>按公共 JSON、环境 JSON、私有 JSON、开发 Secrets、环境变量、命令行的顺序加载配置。</summary>
    internal static void AddEnvironmentOverrides(this WebApplicationBuilder builder)
    {
        var environmentFile = $"appsettings.{builder.Environment.EnvironmentName}.json";
        var sources = builder.Configuration.Sources;
        var index = sources.ToList().FindLastIndex(source => source is JsonConfigurationSource json
            && string.Equals(json.Path, environmentFile, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            throw new InvalidOperationException("The default environment JSON configuration source is missing.");

        // 不能将私有文件追加到末尾，否则会覆盖容器环境变量、CLI 及开发 User Secrets。
        sources.Insert(index + 1, new JsonConfigurationSource
        {
            FileProvider = builder.Environment.ContentRootFileProvider,
            Path = $"appsettings.{builder.Environment.EnvironmentName}.local.json",
            Optional = true,
            ReloadOnChange = false,
        });
    }
}
