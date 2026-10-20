using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using CreativeFlow.Application.Abstractions.Security;

namespace CreativeFlow.Infrastructure.Persistence.Migrations;

/// <summary>
/// 按顺序应用嵌入的 SQL 迁移。
/// </summary>
/// <remarks>
/// EF Core 迁移依赖运行时模型构建，在 Native AOT 下不可用，因此架构变更采用嵌入程序集的
/// 普通版本化 SQL；单个原生二进制文件会携带其所需架构。
/// </remarks>
public sealed class MigrationRunner(NpgsqlDataSource dataSource, ILogger<MigrationRunner> logger, IApiKeyProtector? protector = null)
{
    /// <summary>
    /// 用于标识此应用架构锁的任意固定键。
    /// </summary>
    private const long AdvisoryLockKey = 0x501_5C4E_3A21L;

    /// <summary>迁移程序集资源的固定命名前缀。</summary>
    private const string ResourcePrefix = "CreativeFlow.Migrations.";

    /// <summary>按嵌入版本顺序和既有校验和规则应用待执行 SQL 迁移。</summary>
    public async Task<int> RunAsync(CancellationToken ct, bool discardDatabaseFiles = false)
    {
        var scripts = LoadEmbeddedScripts();
        if (scripts.Count == 0)
        {
            logger.LogWarning("No embedded migration scripts found.");
            return 0;
        }

        await using var connection = await dataSource.OpenConnectionAsync(ct);

        // 串行化并发实例。若无此锁，同时启动的副本会在 DDL 上竞争并死锁。
        // 该锁为会话作用域，并会在下方显式释放。
        await connection.ExecuteAsync("SELECT pg_advisory_lock(@Key)", new LockArgs { Key = AdvisoryLockKey });

        try
        {
            await EnsureVersionTableAsync(connection, scripts[0], ct);

            var applied = (await connection.QueryAsync<AppliedMigration>(
                    "SELECT version, name, checksum FROM schema_version ORDER BY version"))
                .ToDictionary(m => m.Version);

            VerifyChecksums(scripts, applied);

            var pending = scripts.Where(s => !applied.ContainsKey(s.Version)).ToList();
            foreach (var script in pending)
            {
                await ApplyAsync(connection, script, discardDatabaseFiles, ct);
                logger.LogInformation("Applied migration {Version} ({Name}).", script.Version, script.Name);
            }

            return pending.Count;
        }
        finally
        {
            await connection.ExecuteAsync("SELECT pg_advisory_unlock(@Key)", new LockArgs { Key = AdvisoryLockKey });
        }
    }

    /// <summary>
    /// 在记录管理路径之外运行迁移 0001（它会创建 schema_version 本身），然后记录结果。
    /// 每个脚本均按幂等方式编写，因此重复执行无害。
    /// </summary>
    private static async Task EnsureVersionTableAsync(
        NpgsqlConnection connection, MigrationScript first, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = first.Sql;
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// 重新计算每个已应用脚本的哈希；若有变化则停止应用。对已发布迁移进行原地编辑会使各环境
    /// 在无提示的情况下产生差异，其修复成本远高于一次启动失败。
    /// </summary>
    private void VerifyChecksums(
        IReadOnlyList<MigrationScript> scripts, IReadOnlyDictionary<int, AppliedMigration> applied)
    {
        foreach (var script in scripts)
        {
            if (!applied.TryGetValue(script.Version, out var record))
            {
                continue;
            }

            if (!record.Checksum.AsSpan().SequenceEqual(script.Checksum))
            {
                throw new InvalidOperationException(
                    $"Migration {script.Version} ({script.Name}) has been modified after it was applied. " +
                    "Revert the edit and add a new migration instead.");
            }
        }

        logger.LogDebug("Verified {Count} applied migration checksums.", applied.Count);
    }

    /// <summary>
    /// 在同一事务中应用并记录脚本，确保部分应用的迁移绝不会被标记为完成。
    /// </summary>
    private async Task ApplyAsync(
        NpgsqlConnection connection, MigrationScript script, bool discardDatabaseFiles, CancellationToken ct)
    {
        await using var transaction = await connection.BeginTransactionAsync(ct);
        if (script.Version == 37) await HttpCredentialConsolidation.ApplyAsync(connection, transaction, protector, ct);
        if (script.Version == 40 && discardDatabaseFiles)
            await connection.ExecuteAsync("SELECT set_config('creativeflow.discard_database_files','on',true)", transaction: transaction);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = script.Sql;
            await command.ExecuteNonQueryAsync(ct);
        }

        await connection.ExecuteAsync(
            """
            INSERT INTO schema_version (version, name, checksum)
            VALUES (@Version, @Name, @Checksum)
            ON CONFLICT (version) DO NOTHING
            """,
            new InsertMigrationArgs
            {
                Version = script.Version,
                Name = script.Name,
                Checksum = script.Checksum,
            },
            transaction);

        await transaction.CommitAsync(ct);
    }

    /// <summary>按固定资源前缀读取迁移文件、解析版本并计算校验和。</summary>
    private static List<MigrationScript> LoadEmbeddedScripts()
    {
        // 脚本版本和校验和取自嵌入资源；加载顺序稳定，重复或无效版本不能混入待执行迁移。
        var assembly = typeof(MigrationRunner).Assembly;
        var scripts = new List<MigrationScript>();

        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.StartsWith(ResourcePrefix, StringComparison.Ordinal) ||
                !resourceName.EndsWith(".sql", StringComparison.Ordinal))
            {
                continue;
            }

            var fileName = resourceName[ResourcePrefix.Length..];
            var separator = fileName.IndexOf("__", StringComparison.Ordinal);
            if (separator <= 0 || !int.TryParse(fileName[..separator], out var version))
            {
                throw new InvalidOperationException(
                    $"Migration resource '{resourceName}' does not follow the NNNN__name.sql convention.");
            }

            var sql = ReadResource(assembly, resourceName);
            scripts.Add(new MigrationScript(
                version,
                fileName[(separator + 2)..].Replace(".sql", string.Empty, StringComparison.Ordinal),
                sql,
                SHA256.HashData(Encoding.UTF8.GetBytes(sql))));
        }

        scripts.Sort((a, b) => a.Version.CompareTo(b.Version));
        return scripts;
    }

    /// <summary>读取嵌入的 SQL 资源文本，资源缺失时明确失败。</summary>
    private static string ReadResource(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
                           ?? throw new InvalidOperationException($"Missing embedded resource '{resourceName}'.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}

// Dapper.AOT 要求静态类型参数对象：object、dynamic、DynamicParameters 和泛型 T
// 都会关闭生成器（DAP015/DAP016）并回退到反射。
internal sealed class LockArgs
{
    /// <summary>当前查找、签名或映射所使用的键。</summary>
    public long Key { get; init; }
}

internal sealed class InsertMigrationArgs
{
    /// <summary>实体、发布版本或协议的版本号。</summary>
    public int Version { get; init; }
    /// <summary>实体或配置项的名称。</summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>已发布 SQL 迁移内容的校验和。</summary>
    public byte[] Checksum { get; init; } = [];
}

internal sealed class AppliedMigration
{
    /// <summary>实体、发布版本或协议的版本号。</summary>
    public int Version { get; init; }
    /// <summary>实体或配置项的名称。</summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>已发布 SQL 迁移内容的校验和。</summary>
    public byte[] Checksum { get; init; } = [];
}
