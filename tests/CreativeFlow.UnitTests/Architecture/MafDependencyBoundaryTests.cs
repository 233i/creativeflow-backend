using System.Xml.Linq;
using CreativeFlow.Application.Features.Agent;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Infrastructure.Ai.Maf;

namespace CreativeFlow.UnitTests.Architecture;

/// <summary>迁移期间阻止隔离探针进入生产依赖，以及框架类型渗入业务内层。</summary>
public sealed class MafDependencyBoundaryTests
{
    /// <summary>生产程序集只包含 MAF 调度实现，Agent 的运行端口没有可选兜底参数。</summary>
    [Fact]
    public void MafIsTheOnlyProductionEngineAndAgentRequiresIt()
    {
        var types = typeof(AgentTurnRunner).Assembly.GetTypes()
            .Concat(typeof(MafAgentRuntime).Assembly.GetTypes()).Where(type => !type.IsAbstract).ToArray();
        Assert.Equal(typeof(MafAgentRuntime), Assert.Single(types, type => typeof(IAgentRuntime).IsAssignableFrom(type)));
        Assert.Equal(typeof(MafWorkflowRuntime), Assert.Single(types, type => typeof(IWorkflowRuntime).IsAssignableFrom(type)));
        var parameter = Assert.Single(Assert.Single(typeof(AgentTurnRunner).GetConstructors()).GetParameters(),
            item => item.ParameterType == typeof(IAgentRuntime));
        Assert.False(parameter.HasDefaultValue);
        Assert.DoesNotContain(types, type => type.Name is "RunRuntimeResolver" or "RuntimeRoutingPolicy" or "RuntimeMigrationOptions" or "RuntimeBindings");
    }

    /// <summary>试验工具只能被兼容性测试引用，不能成为任何生产项目的依赖。</summary>
    [Fact]
    public void ProductionProjectsNeverReferenceMigrationProbe()
    {
        var source = Path.Combine(DependencyBoundaryTests.RepositoryRoot(), "src");
        foreach (var project in Directory.EnumerateFiles(source, "*.csproj", SearchOption.AllDirectories))
        {
            var references = XDocument.Load(project).Descendants("ProjectReference");
            Assert.DoesNotContain(references, reference =>
                reference.Attribute("Include")!.Value.Contains("MafProbe", StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Application 的公开端口和编译产物保持框架无关，未来 MAF 适配只能落在外层。</summary>
    [Fact]
    public void ApplicationDoesNotDependOnMafOrFrameworkChatTypes()
    {
        var prefixes = new[] { "Microsoft.Agents.", "Microsoft.Extensions.AI" };
        var project = Path.Combine(DependencyBoundaryTests.RepositoryRoot(), "src", "CreativeFlow.Application", "CreativeFlow.Application.csproj");
        Assert.DoesNotContain(XDocument.Load(project).Descendants("PackageReference"), reference =>
            prefixes.Any(prefix => reference.Attribute("Include")!.Value.StartsWith(prefix, StringComparison.Ordinal)));
        Assert.DoesNotContain(typeof(AgentTurnRunner).Assembly.GetReferencedAssemblies(), reference =>
            prefixes.Any(prefix => reference.Name!.StartsWith(prefix, StringComparison.Ordinal)));
    }
}
