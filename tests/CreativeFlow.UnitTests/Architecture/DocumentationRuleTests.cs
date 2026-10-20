using CreativeFlow.CodeQuality;

namespace CreativeFlow.UnitTests.Architecture;

/// <summary>验证注释检查能正确区分 C# 声明、记录字段、文本示例与方法内部说明。</summary>
public sealed class DocumentationRuleTests
{
    /// <summary>类型摘要不能代替私有成员说明，记录参数必须按名称逐项匹配。</summary>
    [Fact]
    public void TypeDocumentationDoesNotHideMissingMembersOrRecordFields()
    {
        var members = Scan("""
            /// <summary>类型说明。</summary>
            public class Example
            {
                private int value;
                public int Value { get; set; }
                private void Run() { }
            }
            /// <param name="Name">名称。</param>
            /// <param name="CountSuffix">无关参数，不能覆盖 Count。</param>
            public record Item(string Name, int Count);
            """);
        Assert.All(members.Where(member => member.Owner == "Example"),
            member => Assert.False(member.Documented));
        Assert.True(Assert.Single(members, member => member.Name == "Name").Documented);
        Assert.False(Assert.Single(members, member => member.Name == "Count").Documented);
        Assert.Equal(5, members.Length);
    }

    /// <summary>接口实现必须有自己的实际摘要，局部函数可以使用紧邻的普通说明。</summary>
    [Fact]
    public void ExplicitImplementationsAndLocalFunctionExplanationsAreRecognized()
    {
        var members = Scan("""
            public interface IRunner
            {
                /// <summary>执行当前工作。</summary>
                void Run();
            }
            public class Runner : IRunner
            {
                /// <summary>调用当前实现的局部步骤，完成本轮工作。</summary>
                public void Run()
                {
                    // 将本轮输入交给局部步骤。
                    void Step() { }
                    Step();
                }
            }
            """);
        Assert.Equal(3, members.Length);
        Assert.All(members, member => Assert.True(member.Documented));
        Assert.Contains(members, member => member.Kind == "local_function");
    }

    /// <summary>继承标记、只有引用的摘要及空白标签都不能冒充声明的实际职责说明。</summary>
    [Theory]
    [InlineData("/// <inheritdoc />")]
    [InlineData("/// <inheritdoc cref=\"IRunner.Run\" />")]
    [InlineData("/// <inheritdoc></inheritdoc>")]
    [InlineData("/// <summary />")]
    [InlineData("/// <summary>   </summary>")]
    [InlineData("/// <summary>\n/// <inheritdoc />\n/// </summary>")]
    [InlineData("/// <summary><see cref=\"IRunner.Run\" /></summary>")]
    public void InheritedOrEmptyDocumentationDoesNotDescribeImplementations(string documentation)
    {
        var member = Assert.Single(Scan("public class Runner\n{\n" + documentation
            + "\npublic void Run() { }\n}"));
        Assert.False(member.Documented);
    }

    /// <summary>摘要中的真实正文可以混合代码或符号引用，不能因包含 XML 子标签而误判缺失。</summary>
    [Theory]
    [InlineData("/// <summary>记录当前步骤并返回结果。</summary>")]
    [InlineData("/// <summary>记录 <c>Run</c> 的执行顺序。</summary>")]
    [InlineData("/// <summary>调用 <see cref=\"IRunner.Run\" /> 完成本轮步骤。</summary>")]
    public void ExplicitSummaryTextCanContainXmlMarkup(string documentation)
    {
        var member = Assert.Single(Scan("public class Runner\n{\n" + documentation
            + "\npublic void Run() { }\n}"));
        Assert.True(member.Documented);
    }

    /// <summary>外层摘要及字符串里的斜杠不能冒充复杂方法的内部规则说明。</summary>
    [Fact]
    public void CommentTextInsideStringsDoesNotSatisfyTheInternalExplanationRule()
    {
        var statements = string.Concat(Enumerable.Repeat("        _ = \"// example\";\n", 32));
        var source = "public class Example\n{\n"
            + "    /// <summary>处理输入。</summary>\n"
            + "    public void Run()\n    {\n        if (true)\n        {\n"
            + statements + "        }\n    }\n}\n";
        var missing = Assert.Single(Scan(source));
        Assert.True(missing.Documented);
        Assert.True(missing.NeedsLogicComment);

        var explained = Assert.Single(Scan(source.Replace("if (true)",
            "// 保持输入逐项处理的顺序。\n        if (true)", StringComparison.Ordinal)));
        Assert.False(explained.NeedsLogicComment);
    }

    /// <summary>编译输出与明确标记的生成文件不会被算作需要维护的手写声明。</summary>
    [Fact]
    public void BuildOutputsAndExplicitGeneratedFilesAreExcluded()
    {
        var members = Scan("""
            /// <param name="Value">输入值。</param>
            public record Item(int Value);
            """, includeGeneratedFiles: true);
        Assert.True(Assert.Single(members).Documented);
    }

    /// <summary>在隔离目录中检查合成源码，避免测试依赖当前仓库的注释数量或文件布局。</summary>
    private static SourceMember[] Scan(string source, bool includeGeneratedFiles = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "creativeflow-documentation-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "src"));
            File.WriteAllText(Path.Combine(root, "src", "Example.cs"), source);
            if (includeGeneratedFiles)
            {
                Directory.CreateDirectory(Path.Combine(root, "src", "obj"));
                Directory.CreateDirectory(Path.Combine(root, "src", "bin"));
                const string generated = "public class Output { public void MissingDocs() {} }";
                File.WriteAllText(Path.Combine(root, "src", "obj", "Output.cs"), generated);
                File.WriteAllText(Path.Combine(root, "src", "bin", "Output.cs"), generated);
                File.WriteAllText(Path.Combine(root, "src", "Generated.cs"), "// <auto-generated/>\n" + generated);
            }
            return SourceInventory.Read(root).ToArray();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
