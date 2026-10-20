using System.Reflection;
using System.Xml.Linq;
using CreativeFlow.Api.Hubs;
using CreativeFlow.Api.Realtime;
using CreativeFlow.Application.Features.Generation;
using CreativeFlow.Domain.Identity;

namespace CreativeFlow.UnitTests.Architecture;

/// <summary>同时检查项目声明和编译产物，阻止 HTTP 与数据库依赖进入业务内层。</summary>
public sealed class DependencyBoundaryTests
{
    /// <summary>领域项目保持零项目/包引用，其他内层项目仅向内引用。</summary>
    [Theory]
    [InlineData("CreativeFlow.Domain", "")]
    [InlineData("CreativeFlow.Application", "CreativeFlow.Domain")]
    [InlineData("CreativeFlow.Infrastructure", "CreativeFlow.Application")]
    public void ProjectReferencesPointInward(string project, string expected)
    {
        var path = Path.Combine(RepositoryRoot(), "src", project, project + ".csproj");
        var document = XDocument.Load(path);
        var actual = document.Descendants("ProjectReference")
            .Select(reference => Path.GetFileNameWithoutExtension(reference.Attribute("Include")!.Value))
            .Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expected.Length == 0 ? [] : expected.Split(','), actual);
        if (project == "CreativeFlow.Domain") Assert.Empty(document.Descendants("PackageReference"));
    }

    /// <summary>编译后的 Application 不得实际引用交付、持久化或后台宿主程序集。</summary>
    [Fact]
    public void ApplicationAssemblyDoesNotDependOnDeliveryOrPersistence()
    {
        var forbidden = new[]
        {
            "CreativeFlow.Api", "CreativeFlow.Infrastructure", "Dapper", "Npgsql",
            "Microsoft.AspNetCore.", "Microsoft.Extensions.Hosting",
        };
        Assert.DoesNotContain(typeof(GenerateImagesUseCase).Assembly.GetReferencedAssemblies(),
            reference => forbidden.Any(prefix => reference.Name!.StartsWith(prefix, StringComparison.Ordinal)));
        Assert.DoesNotContain(typeof(DeviceId).Assembly.GetReferencedAssemblies(),
            reference => reference.Name!.StartsWith("CreativeFlow.", StringComparison.Ordinal));
    }

    /// <summary>依赖 Hub 的通知器始终属于 API 交付适配器。</summary>
    [Fact]
    public void SignalRAdapterStaysBesideItsHub() =>
        Assert.Equal(typeof(CreativeFlowHub).Assembly, typeof(SignalRNotifier).Assembly);

    /// <summary>从测试输出目录向上查找仓库，兼容本机及 CI 的不同检出路径。</summary>
    internal static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CreativeFlow.slnx"))) return directory.FullName;
        }
        throw new DirectoryNotFoundException("The tests must run from a CreativeFlow checkout.");
    }
}
