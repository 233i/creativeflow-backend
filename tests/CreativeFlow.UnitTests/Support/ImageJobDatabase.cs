using Npgsql;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Infrastructure.Persistence;
using CreativeFlow.Infrastructure.Persistence.Migrations;
using CreativeFlow.Infrastructure.Ai;
using CreativeFlow.Infrastructure.Options;

namespace CreativeFlow.UnitTests.Support;

/// <summary>仅使用显式指定的测试库和随机隔离 schema，不读取或修改开发/线上数据。</summary>
internal sealed class ImageJobDatabase : IAsyncDisposable
{
    /// <summary>本实例独占的随机测试 schema，释放时只删除此范围。</summary>
    private readonly string schema = "image_jobs_test_" + Guid.NewGuid().ToString("N");
    /// <summary>连接固定测试 schema 的数据源。</summary>
    public NpgsqlDataSource Source { get; private set; } = null!;
    /// <summary>使用真实 SQL 的任务仓储。</summary>
    public ImageJobRepository Jobs { get; private set; } = null!;
    public string FileRoot { get; } = Path.Combine(Path.GetTempPath(), "creativeflow-test-files-" + Guid.NewGuid().ToString("N"));
    public FileSystemAssetStore Store { get; private set; } = null!;
    /// <summary>主要测试账号。</summary>
    public Guid Account { get; } = Guid.NewGuid();
    /// <summary>主要账号的设备。</summary>
    public Guid Device { get; } = Guid.NewGuid();
    /// <summary>访问隔离与跨账号配额测试账号。</summary>
    public Guid OtherAccount { get; } = Guid.NewGuid();
    /// <summary>第二个账号的设备。</summary>
    public Guid OtherDevice { get; } = Guid.NewGuid();
    /// <summary>共享配额测试使用的虚拟服务商。</summary>
    public Guid Provider { get; } = Guid.NewGuid();
    /// <summary>主要账号的测试画布。</summary>
    public Guid Canvas { get; } = Guid.NewGuid();
    /// <summary>是否未显式提供测试库，禁止回落到开发或生产连接。</summary>
    public static bool NoDatabase => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CREATIVEFLOW_TEST_POSTGRES"));

    /// <summary>创建随机 schema，应用全部迁移并播种隔离测试身份。</summary>
    public static async Task<ImageJobDatabase> CreateAsync(int? migrationsThrough = null)
    {
        var database = new ImageJobDatabase();
        var config = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("CREATIVEFLOW_TEST_POSTGRES")) { SearchPath = database.schema };
        database.Source = NpgsqlDataSourceFactory.Create(config.ConnectionString);
        try
        {
            await database.SqlAsync($"CREATE SCHEMA {database.schema}");
            var assembly = typeof(MigrationRunner).Assembly;
            foreach (var name in assembly.GetManifestResourceNames().Where(name => name.StartsWith("CreativeFlow.Migrations.", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
            {
                if (migrationsThrough is { } last && int.Parse(name["CreativeFlow.Migrations.".Length..].Split("__", StringSplitOptions.None)[0], System.Globalization.CultureInfo.InvariantCulture) > last) continue;
                await using var stream = assembly.GetManifestResourceStream(name)!;
                using var reader = new StreamReader(stream);
                var sql = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
                // 发行迁移 0045 显式限定 public；隔离测试只把该命名空间映射到本夹具的随机 schema。
                // 不修改迁移资源或校验和，也不允许测试触碰共享 public 表。
                if (name.EndsWith("0045__creativeflow_namespace.sql", StringComparison.Ordinal))
                {
                    sql = sql.Replace("search_path = public", $"search_path = {database.schema}", StringComparison.Ordinal)
                        .Replace("'public'", $"'{database.schema}'", StringComparison.Ordinal);
                }
                await database.SqlAsync(sql);
            }
            var identitySeed = $$"""
                INSERT INTO creativeflow_account(account_id,display_name) VALUES ('{{database.Account}}','Queue A'), ('{{database.OtherAccount}}','Queue B');
                INSERT INTO device(device_id,fp_exact,fp_coarse,ip_prefix,signals,sig_version) VALUES
                    ('{{database.Device}}','\x01','\x01','127.0.0.1','{}',1), ('{{database.OtherDevice}}','\x02','\x02','127.0.0.1','{}',1);
                INSERT INTO creativeflow_account_device(account_id,device_id) VALUES ('{{database.Account}}','{{database.Device}}'), ('{{database.OtherAccount}}','{{database.OtherDevice}}');
                INSERT INTO canvas(canvas_id,device_id,name) VALUES ('{{database.Canvas}}','{{database.Device}}','Queue test');
                """;
            await database.SqlAsync(migrationsThrough is < 45 ? identitySeed.Replace("creativeflow_", "sol_", StringComparison.Ordinal) : identitySeed);
            database.Jobs = new(database.Source);
            var options = new AiOptions { AssetRoot = database.FileRoot };
            database.Store = new(database.Source, new AssetFileSystem(options), options, Microsoft.Extensions.Logging.Abstractions.NullLogger<FileSystemAssetStore>.Instance);
            return database;
        }
        catch { await database.DisposeAsync(); throw; }
    }

    /// <summary>构造有稳定参数及可指定摘要的提交输入。</summary>
    public ImageJobSubmissionInput Input(string hash = "same-request") => new(Account, Device, Guid.NewGuid(), Provider,
        Canvas, "node-1", $$"""{"providerId":"{{Provider}}","modelKey":"image-model","prompt":"tree","count":1}""", hash);

    /// <summary>仅在本实例测试 schema 中执行准备或断言 SQL。</summary>
    public async Task SqlAsync(string sql)
    {
        await using var command = Source.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>历史迁移断言后补齐后续结构，保证当前仓储运行在完整版本而非旧身份表名上。</summary>
    public async Task ApplyRemainingAsync(int after)
    {
        var assembly = typeof(MigrationRunner).Assembly;
        // 只重映射测试命名空间，冻结资源本身与生产迁移校验和保持不变。
        foreach (var name in assembly.GetManifestResourceNames().Where(name => name.StartsWith("CreativeFlow.Migrations.", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            if (int.Parse(name["CreativeFlow.Migrations.".Length..].Split("__", StringSplitOptions.None)[0], System.Globalization.CultureInfo.InvariantCulture) <= after) continue;
            using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
            var sql = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
            if (name.EndsWith("0045__creativeflow_namespace.sql", StringComparison.Ordinal))
                sql = sql.Replace("search_path = public", $"search_path = {schema}", StringComparison.Ordinal)
                    .Replace("'public'", $"'{schema}'", StringComparison.Ordinal);
            await SqlAsync(sql);
        }
    }

    /// <summary>读取测试范围内的计数断言。</summary>
    public async Task<long> CountAsync(string sql)
    {
        await using var command = Source.CreateCommand(sql);
        return Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>删除本实例创建的 schema，并释放连接池。</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await using var command = Source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
        finally
        {
            await Source.DisposeAsync();
            if (Directory.Exists(FileRoot)) Directory.Delete(FileRoot, recursive: true);
        }
    }
}
