using System.Text.Json;
using CreativeFlow.CodeQuality;

// 检查源文件语法，避免正则表达式将注释中的示例或生成代码误算为业务方法。
var inventoryOnly = args.Contains("--inventory", StringComparer.Ordinal);
var root = Path.GetFullPath(args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal))
    ?? Directory.GetCurrentDirectory());
if (!File.Exists(Path.Combine(root, "CreativeFlow.slnx")))
{
    Console.Error.WriteLine("Provide the CreativeFlow repository root.");
    return 2;
}

var members = SourceInventory.Read(root).ToArray();
if (inventoryOnly)
{
    Console.WriteLine(JsonSerializer.Serialize(members, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}

var failures = members.Where(member => !member.Documented || member.NeedsLogicComment).ToArray();
foreach (var member in failures)
{
    var reason = !member.Documented ? "缺少方法/字段文档" : "复杂方法缺少解释关键规则的内部注释";
    Console.Error.WriteLine($"{member.File}:{member.Line}: {reason}：{member.Owner}.{member.Name}");
}
Console.WriteLine($"Checked {members.Length} declarations; documentation violations: {failures.Length}.");
return failures.Length == 0 ? 0 : 1;
