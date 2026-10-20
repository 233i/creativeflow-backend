using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using CreativeFlow.Api.Extensions;
using CreativeFlow.UnitTests.Architecture;

namespace CreativeFlow.UnitTests.Api;

/// <summary>仅装配配置提供程序，验证环境隔离、覆盖优先级和发布约束，不启动 API 或访问任何依赖。</summary>
public sealed class EnvironmentConfigurationTests
{
    /// <summary>私有配置只覆盖同环境 JSON，其他环境文件与普通 .env 不会被后端自动加载。</summary>
    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    [InlineData("MigrationBaseline")]
    public void PrivateFileOverridesOnlyMatchingEnvironment(string environment)
    {
        using var files = new ConfigurationFiles();
        files.Json("appsettings.json", "base", "base-only");
        files.Json($"appsettings.{environment}.json", "environment");
        files.Json($"appsettings.{environment}.local.json", "private");
        files.Json("appsettings.OtherEnvironment.local.json", "other");
        File.WriteAllText(Path.Combine(files.Root, ".env"), $"{files.Key}__Choice=dotenv");
        var builder = files.Builder(environment);
        try
        {
            builder.AddEnvironmentOverrides();
            Assert.Equal("private", builder.Configuration[$"{files.Key}:Choice"]);
            Assert.Equal("base-only", builder.Configuration[$"{files.Key}:BaseOnly"]);
            Assert.Equal(environment, builder.Environment.EnvironmentName);
        }
        finally { ((IDisposable)builder.Configuration).Dispose(); }
    }

    /// <summary>没有私有文件时仍使用公共和环境配置，容器不必携带私有 JSON。</summary>
    [Fact]
    public void MissingPrivateFileIsOptional()
    {
        using var files = new ConfigurationFiles();
        files.Json("appsettings.json", "base");
        var builder = files.Builder("Production");
        try
        {
            builder.AddEnvironmentOverrides();
            Assert.Equal("base", builder.Configuration[$"{files.Key}:Choice"]);
        }
        finally { ((IDisposable)builder.Configuration).Dispose(); }
    }

    /// <summary>真实环境变量高于私有文件，命令行再高于环境变量；迁移开关保留在原始参数中。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeploymentOverridesTakePrecedence(bool commandLine)
    {
        using var files = new ConfigurationFiles();
        files.Json("appsettings.Production.json", "environment");
        files.Json("appsettings.Production.local.json", "private");
        var environmentKey = $"{files.Key}__Choice";
        Environment.SetEnvironmentVariable(environmentKey, "environment-variable");
        try
        {
            var builder = files.Builder("Production", commandLine
                ? [$"--{files.Key}:Choice=command-line", "--migrate-only"] : []);
            try
            {
                builder.AddEnvironmentOverrides();
                Assert.Equal(commandLine ? "command-line" : "environment-variable",
                    builder.Configuration[$"{files.Key}:Choice"]);
            }
            finally { ((IDisposable)builder.Configuration).Dispose(); }
        }
        finally { Environment.SetEnvironmentVariable(environmentKey, null); }
    }

    /// <summary>私有文件插在环境 JSON 后，不覆盖更高层的 Secrets 提供程序或重新排序既有来源。</summary>
    [Fact]
    public void PrivateFileKeepsHigherPrioritySecretSources()
    {
        using var files = new ConfigurationFiles();
        files.Json("appsettings.Development.json", "environment");
        files.Json("appsettings.Development.local.json", "private");
        files.Json("synthetic-secrets.json", "secret");
        var builder = files.Builder("Development");
        try
        {
            var sources = builder.Configuration.Sources;
            var index = sources.ToList().FindIndex(source => source is JsonConfigurationSource json
                && json.Path == "appsettings.Development.json");
            sources.Insert(index + 1, new JsonConfigurationSource
            {
                FileProvider = builder.Environment.ContentRootFileProvider,
                Path = "synthetic-secrets.json",
                Optional = false,
            });
            var before = sources.ToArray();
            builder.AddEnvironmentOverrides();
            Assert.Equal("secret", builder.Configuration[$"{files.Key}:Choice"]);
            Assert.Equal(before, sources.Where(source => source is not JsonConfigurationSource json
                || json.Path != "appsettings.Development.local.json"));
        }
        finally { ((IDisposable)builder.Configuration).Dispose(); }
    }

    /// <summary>正式模板不绑定具体部署，要求独立文件卷和外部注入密钥，Qdrant 保留回环安全边界。</summary>
    [Fact]
    public void ProductionProfileUsesProductionResourcesAndRequiresSecrets()
    {
        using var config = Profile("Production");
        Assert.Equal("https://your-domain.example", config["Authentication:PublicOrigin"]);
        Assert.Equal("http://127.0.0.1:6333", config["Knowledge:Qdrant:Endpoint"]);
        Assert.Equal("/var/lib/creativeflow/assets", config["Ai:AssetRoot"]);
        Assert.Equal("/var/lib/creativeflow/skills", config["Skills:Root"]);
        Assert.Equal("True", config["Ai:RequireStorageMarker"]);
        Assert.Equal("True", config["Skills:RequireStorageMarker"]);
        Assert.Equal("False", config["Postgres:RunMigrationsOnStartup"]);
        Assert.Equal("True", config["Workflow:Enabled"]);
        Assert.Equal("True", config["Knowledge:Enabled"]);
        foreach (var key in new[] { "Postgres:ConnectionString", "Redis:Configuration", "DeviceIdentity:Pepper",
                     "Ai:EncryptionKey", "Admin:DefaultPassword", "Knowledge:Qdrant:ApiKey" })
            Assert.True(string.IsNullOrEmpty(config[key]), key);
        Assert.Empty(config.GetSection("Embedding:AllowedEndpoints").GetChildren());
        Assert.Equal("False", config["Skills:SandboxEnabled"]);
    }

    /// <summary>开发模板使用本机基础设施占位地址，凭据由私有层提供，不自动执行数据库迁移。</summary>
    [Fact]
    public void DevelopmentProfileUsesPortableLocalDefaults()
    {
        using var config = Profile("Development");
        Assert.Equal("localhost", config["RabbitMq:HostName"]);
        Assert.Equal("5672", config["RabbitMq:Port"]);
        Assert.Equal("localhost:8090", config["Skills:OpenSandboxDomain"]);
        Assert.Equal("http://localhost:5298", config["Authentication:PublicOrigin"]);
        Assert.Equal("./storage/assets", config["Ai:AssetRoot"]);
        Assert.Equal("False", config["Ai:RequireStorageMarker"]);
        Assert.Equal("False", config["Postgres:RunMigrationsOnStartup"]);
        Assert.True(string.IsNullOrEmpty(config["Admin:DefaultPassword"]));
        Assert.True(string.IsNullOrEmpty(config["Postgres:ConnectionString"]));
        Assert.True(string.IsNullOrEmpty(config["Redis:Configuration"]));
        Assert.Equal("REPLACE_WITH_LOCAL_DEVICE_PEPPER", config["DeviceIdentity:Pepper"]);
        Assert.Equal("REPLACE_WITH_LOCAL_AI_ENCRYPTION_KEY", config["Ai:EncryptionKey"]);
    }

    /// <summary>SDK 的 JSON Content 规则显式排除机器私有配置，防止 dotnet publish 泄露开发凭据。</summary>
    [Fact]
    public void PrivateConfigurationCannotBeCopiedByProjectDefaults()
    {
        var document = XDocument.Load(Path.Combine(DependencyBoundaryTests.RepositoryRoot(), "src/CreativeFlow.Api/CreativeFlow.Api.csproj"));
        var rule = Assert.Single(document.Descendants("Content"), item => item.Attribute("Update")?.Value == "appsettings.*.local.json");
        Assert.Equal("Never", rule.Attribute("CopyToOutputDirectory")?.Value);
        Assert.Equal("Never", rule.Attribute("CopyToPublishDirectory")?.Value);
        Assert.Equal("CreativeFlow.Api", Assert.Single(document.Descendants("UserSecretsId")).Value);
    }

    /// <summary>本机启动 profile 的两个环境变量一致，不因宿主残留 DOTNET_ENVIRONMENT 意外启动正式配置。</summary>
    [Fact]
    public void LaunchProfilePinsBothEnvironmentNamesToDevelopment()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(DependencyBoundaryTests.RepositoryRoot(),
            "src/CreativeFlow.Api/Properties/launchSettings.json")));
        var environment = json.RootElement.GetProperty("profiles").GetProperty("http").GetProperty("environmentVariables");
        Assert.Equal("Development", environment.GetProperty("ASPNETCORE_ENVIRONMENT").GetString());
        Assert.Equal("Development", environment.GetProperty("DOTNET_ENVIRONMENT").GetString());
    }

    /// <summary>仅读取受版本管理的公共与环境 JSON，绝不加载用户实际私有配置或服务器环境变量。</summary>
    private static ConfigurationManager Profile(string environment)
    {
        var configuration = new ConfigurationManager();
        configuration.SetBasePath(Path.Combine(DependencyBoundaryTests.RepositoryRoot(), "src/CreativeFlow.Api"));
        configuration.AddJsonFile("appsettings.json").AddJsonFile($"appsettings.{environment}.json");
        return configuration;
    }

    /// <summary>为每个测试提供独立目录与配置键；释放时仅移除本实例创建的合成文件。</summary>
    private sealed class ConfigurationFiles : IDisposable
    {
        /// <summary>本次测试专属的 ContentRoot，不读取开发目录的私有配置。</summary>
        internal string Root { get; } = Directory.CreateTempSubdirectory("creativeflow-config-test-").FullName;
        /// <summary>环境变量名称使用随机前缀，允许多个用例并行且不覆盖宿主设置。</summary>
        internal string Key { get; } = "CF_CONFIG_TEST_" + Guid.NewGuid().ToString("N");

        /// <summary>写入只含合成标记的 JSON，测试覆盖和继承行为。</summary>
        internal void Json(string file, string value, string? baseOnly = null)
        {
            var inherited = baseOnly is null ? "" : $",\"BaseOnly\":\"{baseOnly}\"";
            File.WriteAllText(Path.Combine(Root, file), "{\"" + Key + "\":{\"Choice\":\"" + value + "\"" + inherited + "}}");
        }

        /// <summary>创建尚未注册业务服务的标准 ASP.NET 宿主配置，不启动任何 Worker。</summary>
        internal WebApplicationBuilder Builder(string environment, string[]? args = null) =>
            WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = environment,
                ContentRootPath = Root,
                Args = args ?? [],
            });

        /// <summary>清除本测试的随机临时目录，不接触已有配置文件。</summary>
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
