using Npgsql;
using CreativeFlow.Infrastructure.Persistence.Migrations;
using CreativeFlow.UnitTests.Support;

namespace CreativeFlow.UnitTests.Ai;

public sealed class FileStorageCutoverTests
{
    public static bool NoDatabase => ImageJobDatabase.NoDatabase;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(Skip = "需要隔离测试库", SkipWhen = nameof(NoDatabase))]
    public async Task NonEmptyDatabaseRequiresExplicitDestructiveOptInAndPreservesHistory()
    {
        await using var db = await ImageJobDatabase.CreateAsync(migrationsThrough: 39);
        var asset = Guid.NewGuid(); var skill = Guid.NewGuid(); var workflow = Guid.NewGuid(); var run = Guid.NewGuid();
        await db.SqlAsync($$"""
            INSERT INTO sol_asset_blob(storage_path,media_type,payload) VALUES('cloud/old','image/png','\x010203');
            INSERT INTO canvas_asset(asset_id,device_id,kind,media_type,storage_path,byte_size) VALUES('{{asset}}','{{db.Device}}','image','image/png','cloud/old',3);
            INSERT INTO skill(skill_id,device_id,slug,name,storage_path) VALUES('{{skill}}','{{db.Device}}','old','Old skill','old/skill');
            INSERT INTO sol_skill_file(storage_path,relative_path,payload) VALUES('old/skill','SKILL.md','\x010203');
            INSERT INTO workflow(workflow_id,device_id,name,created_at,updated_at) VALUES('{{workflow}}','{{db.Device}}','Text-only workflow',now(),now());
            INSERT INTO workflow_run(run_id,workflow_id,device_id,graph_snapshot,status,created_at,updated_at) VALUES('{{run}}','{{workflow}}','{{db.Device}}','{}','queued',now(),now());
            """);
        var error = await Assert.ThrowsAsync<PostgresException>(() => Cutover(db, false));
        Assert.Contains("--discard-database-files", error.Message);
        Assert.Equal(1, await db.CountAsync("SELECT count(*) FROM sol_asset_blob"));
        Assert.Equal(1, await db.CountAsync("SELECT count(*) FROM canvas_asset WHERE purged_at IS NULL"));
        Assert.Equal(1, await db.CountAsync("SELECT count(*) FROM skill"));
        await Cutover(db, true);
        Assert.Equal(1, await db.CountAsync("SELECT count(*) FROM canvas_asset WHERE purged_at IS NOT NULL AND deleted_at IS NOT NULL AND NOT is_public"));
        Assert.Equal(0, await db.CountAsync("SELECT count(*) FROM skill"));
        Assert.Equal(1, await db.CountAsync("SELECT count(*) FROM workflow_run WHERE status='queued'"));
        Assert.Equal(0, await db.CountAsync("SELECT count(*) FROM information_schema.tables WHERE table_schema=current_schema() AND table_name IN ('sol_asset_blob','sol_skill_file')"));
        Assert.Equal(0, await db.CountAsync("SELECT count(*) FROM information_schema.columns WHERE table_schema=current_schema() AND table_name='image_job_output' AND column_name='payload'"));
        Assert.Equal(2, await db.CountAsync("SELECT count(*) FROM sol_account"));
        Assert.Equal(2, await db.CountAsync("SELECT count(*) FROM device WHERE fp_exact IS NOT NULL"));
        Assert.Null(await db.Store.OpenReadAsync("cloud/old", Ct));
    }

    private static async Task Cutover(ImageJobDatabase db, bool discard)
    {
        await using var connection = await db.Source.OpenConnectionAsync(Ct);
        await using var tx = await connection.BeginTransactionAsync(Ct);
        await using var command = connection.CreateCommand(); command.Transaction = tx;
        if (discard)
        {
            command.CommandText = "SET LOCAL creativeflow.discard_database_files='on'";
            await command.ExecuteNonQueryAsync(Ct);
        }
        await using var stream = typeof(MigrationRunner).Assembly.GetManifestResourceStream("CreativeFlow.Migrations.0040__filesystem_storage.sql")!;
        using var reader = new StreamReader(stream);
        command.CommandText = await reader.ReadToEndAsync(Ct);
        await command.ExecuteNonQueryAsync(Ct); await tx.CommitAsync(Ct);
    }
}
