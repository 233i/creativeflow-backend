namespace CreativeFlow.Infrastructure.Persistence.Migrations;

/// <param name="Checksum"><paramref name="Sql"/> 的 SHA-256，每次启动时都会比较。</param>
/// <param name="Version">实体、发布版本或协议的版本号。</param>
/// <param name="Name">实体或配置项的名称。</param>
/// <param name="Sql">该版本迁移嵌入的原始 SQL 正文，用于执行和校验和验证。</param>
internal sealed record MigrationScript(int Version, string Name, string Sql, byte[] Checksum);
