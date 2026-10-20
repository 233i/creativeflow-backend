using System.Security.Cryptography;
using System.Text.Json;
using CreativeFlow.Infrastructure.Persistence.Migrations;

namespace CreativeFlow.UnitTests.Architecture;

/// <summary>锁定独立发行版的迁移及其嵌入字节，防止后续整理破坏发行版校验和。</summary>
public sealed class MigrationCompatibilityTests
{
    /// <summary>独立发行版的迁移文件与程序集资源必须保持基线字节；新迁移另行追加。</summary>
    [Fact]
    public void ReleaseMigrationsAndEmbeddedResourcesMatchBaseline()
    {
        var root = DependencyBoundaryTests.RepositoryRoot();
        var expected = JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Combine(root, "tests/CreativeFlow.UnitTests/Contracts/migration-checksums.json")))!;
        var assembly = typeof(MigrationRunner).Assembly;
        foreach (var (name, checksum) in expected)
        {
            var bytes = File.ReadAllBytes(Path.Combine(root, "db/migrations", name));
            Assert.Equal(checksum, Convert.ToHexStringLower(SHA256.HashData(bytes)));
            using var resource = assembly.GetManifestResourceStream("CreativeFlow.Migrations." + name);
            Assert.NotNull(resource);
            Assert.Equal(checksum, Convert.ToHexStringLower(SHA256.HashData(resource)));
        }
    }
}
