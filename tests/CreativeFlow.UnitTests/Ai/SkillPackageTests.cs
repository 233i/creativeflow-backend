using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Options;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;
using CreativeFlow.Infrastructure.Options;
using CreativeFlow.Infrastructure.Skills;

namespace CreativeFlow.UnitTests.Ai;

public sealed class SkillPackageTests
{
    /// <summary>当前验证器或测试使用的限制集合。</summary>
    private static readonly SkillsOptions Limits = new()
    {
        Root = Path.Combine(Path.GetTempPath(), "creativeflow-skill-tests"),
        MaxUploadBytes = 2 * 1024 * 1024,
        MaxExtractedBytes = 4 * 1024 * 1024,
        MaxEntries = 100,
    };

    /// <summary>回归场景：Bare manifest is scanned and frontmatter removed from instructions。</summary>
    [Fact]
    public async Task Bare_manifest_is_scanned_and_frontmatter_removed_from_instructions()
    {
        var scanner = Scanner();
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes("""
            ---
            name: Prompt Critic
            description: Reviews prompts before generation
            version: 1.0
            ---
            Read the canvas, then identify vague language.
            """));

        var result = await scanner.ScanAsync(input, "SKILL.md", CancellationToken.None);

        Assert.Equal("Prompt Critic", result.Name);
        Assert.Equal("prompt-critic", result.Slug);
        Assert.Equal("Reviews prompts before generation", result.Description);
        Assert.Equal("Read the canvas, then identify vague language.", result.Instructions);
        Assert.False(result.HasScripts);
        Assert.Equal(SkillRiskLevel.Safe, result.MaxRisk);
    }

    /// <summary>回归场景：Shared top level directory is stripped。</summary>
    [Fact]
    public async Task Shared_top_level_directory_is_stripped()
    {
        var zip = Zip(("example/SKILL.md", Manifest("Nested")), ("example/reference.txt", "hello"));

        var result = await Scanner().ScanAsync(zip, "skill.zip", CancellationToken.None);

        Assert.Contains(result.Files, file => file.Path == "SKILL.md");
        Assert.Contains(result.Files, file => file.Path == "reference.txt");
    }

    /// <summary>回归场景：Unsafe archive paths are rejected。</summary>
    [Theory]
    [InlineData("../SKILL.md")]
    [InlineData("/SKILL.md")]
    [InlineData("C:/SKILL.md")]
    [InlineData("folder\\SKILL.md")]
    public async Task Unsafe_archive_paths_are_rejected(string path)
    {
        var zip = Zip((path, Manifest("Unsafe")));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Scanner().ScanAsync(zip, "skill.zip", CancellationToken.None));
    }

    /// <summary>回归场景：Excessively long archive paths are rejected。</summary>
    [Fact]
    public async Task Excessively_long_archive_paths_are_rejected()
    {
        var zip = Zip((new string('a', 256) + "/SKILL.md", Manifest("Long path")));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Scanner().ScanAsync(zip, "skill.zip", CancellationToken.None));
    }

    /// <summary>回归场景：Missing manifest is rejected。</summary>
    [Fact]
    public async Task Missing_manifest_is_rejected()
    {
        var zip = Zip(("README.md", "not a skill"));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Scanner().ScanAsync(zip, "skill.zip", CancellationToken.None));
    }

    /// <summary>回归场景：Nested archives are rejected。</summary>
    [Fact]
    public async Task Nested_archives_are_rejected()
    {
        var zip = Zip(("SKILL.md", Manifest("Nested archive")), ("payload.zip", "not really a zip"));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Scanner().ScanAsync(zip, "skill.zip", CancellationToken.None));
    }

    /// <summary>回归场景：Duplicate normalized paths are rejected。</summary>
    [Fact]
    public async Task Duplicate_normalized_paths_are_rejected()
    {
        var zip = Zip(("SKILL.md", Manifest("Duplicate")), ("guide.txt", "one"), ("GUIDE.txt", "two"));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Scanner().ScanAsync(zip, "skill.zip", CancellationToken.None));
    }

    /// <summary>回归场景：Symbolic links are rejected。</summary>
    [Fact]
    public async Task Symbolic_links_are_rejected()
    {
        await using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifest = archive.CreateEntry("SKILL.md");
            await using (var writer = new StreamWriter(manifest.Open(), Encoding.UTF8, leaveOpen: false))
            {
                await writer.WriteAsync(Manifest("Unsafe"));
            }
            var link = archive.CreateEntry("payload.py");
            link.ExternalAttributes = 0xA000 << 16;
            await using var linkWriter = new StreamWriter(link.Open(), Encoding.UTF8, leaveOpen: false);
            await linkWriter.WriteAsync("target.py");
        }
        output.Position = 0;

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Scanner().ScanAsync(output, "skill.zip", CancellationToken.None));
    }

    /// <summary>回归场景：Script risks are reported with lines。</summary>
    [Fact]
    public async Task Script_risks_are_reported_with_lines()
    {
        var zip = Zip(
            ("SKILL.md", Manifest("Downloader")),
            ("scripts/run.sh", "#!/bin/sh\ncurl https://example.test/data\n"));

        var result = await Scanner().ScanAsync(zip, "skill.zip", CancellationToken.None);

        Assert.True(result.HasScripts);
        Assert.Equal(SkillRiskLevel.Danger, result.MaxRisk);
        Assert.Contains(result.Risks, finding => finding.Code == "script_file");
        var network = Assert.Single(result.Risks, finding => finding.Code == "network_access");
        Assert.Equal(2, network.Line);
    }

    /// <summary>回归场景：Store rejects symlinked device directories。</summary>
    [Fact]
    public async Task Store_rejects_symlinked_device_directories()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), $"creativeflow-skill-{Guid.NewGuid():N}");
        var outside = Path.Combine(Path.GetTempPath(), $"creativeflow-skill-outside-{Guid.NewGuid():N}");
        var deviceId = new DeviceId(Guid.NewGuid());
        var store = new FileSystemSkillStore(Microsoft.Extensions.Options.Options.Create(
            new SkillsOptions { Root = root }));
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(
            Path.Combine(root, deviceId.Value.ToString("N")), outside);

        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => store.InstallAsync(
                deviceId,
                SkillId.New(),
                [new SkillPackageFile("SKILL.md", Encoding.UTF8.GetBytes(Manifest("Unsafe")))],
                CancellationToken.None));
            Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
        }
        finally
        {
            var link = Path.Combine(root, deviceId.Value.ToString("N"));
            if (Directory.Exists(link)) Directory.Delete(link);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            if (Directory.Exists(outside)) Directory.Delete(outside, recursive: true);
        }
    }

    /// <summary>回归场景：Store enforces containment and reads utf8。</summary>
    [Fact]
    public async Task Store_enforces_containment_and_reads_utf8()
    {
        // 用隔离临时目录验证可读取合法 UTF-8 内容，同时拒绝逃逸路径；测试结束必须清理包目录。
        var root = Path.Combine(Path.GetTempPath(), $"creativeflow-skill-{Guid.NewGuid():N}");
        var options = Microsoft.Extensions.Options.Options.Create(new SkillsOptions
        {
            Root = root,
            MaxUploadBytes = Limits.MaxUploadBytes,
            MaxExtractedBytes = Limits.MaxExtractedBytes,
            MaxEntries = Limits.MaxEntries,
        });
        var store = new FileSystemSkillStore(options);
        var deviceId = new DeviceId(Guid.NewGuid());
        var skillId = SkillId.New();

        try
        {
            var path = await store.InstallAsync(
                deviceId,
                skillId,
                [new SkillPackageFile("SKILL.md", Encoding.UTF8.GetBytes(Manifest("Stored")))],
                CancellationToken.None);
            var text = await store.ReadTextAsync(path, "SKILL.md", 64 * 1024, CancellationToken.None);
            Assert.Contains("name: Stored", text);
            var package = await store.ReadPackageAsync(path, 10, 64 * 1024, CancellationToken.None);
            var storedManifest = Assert.Single(package);
            Assert.Equal("SKILL.md", storedManifest.Path);
            Assert.Contains("name: Stored", Encoding.UTF8.GetString(storedManifest.Bytes));

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.ReadTextAsync(path, "../outside", 100, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.DeleteAsync("../outside", CancellationToken.None));

            await store.DeleteAsync(path, CancellationToken.None);
            Assert.False(Directory.Exists(Path.Combine(root, path)));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>创建使用测试包大小与风险限制的 Skill 扫描器。</summary>
    private static ZipSkillScanner Scanner() =>
        new(Microsoft.Extensions.Options.Options.Create(Limits));

    /// <summary>回归场景：Zip。</summary>
    private static MemoryStream Zip(params (string Path, string Content)[] files)
    {
        var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files)
            {
                var entry = archive.CreateEntry(file.Path, CompressionLevel.Optimal);
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8, leaveOpen: false);
                writer.Write(file.Content);
            }
        }
        output.Position = 0;
        return output;
    }

    /// <summary>生成 Skill 包测试使用的清单正文。</summary>
    private static string Manifest(string name) => $"""
        ---
        name: {name}
        description: Test skill
        ---
        Follow these instructions.
        """;
}
