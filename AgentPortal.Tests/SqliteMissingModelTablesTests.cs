using System;
using System.Threading.Tasks;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPortal.Tests;

public sealed class SqliteMissingModelTablesTests
{
    [Fact]
    public async Task ExistingDatabaseRestoresPublicationTablesWithoutChangingRowsOrHistory()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).Options);
        await MasterAppSqliteSchemaBootstrapper.InitializeAsync(db, NullLogger.Instance);
        async Task<string> Scalar(string sql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToString(await command.ExecuteScalarAsync())!;
        }
        var history = await Scalar("SELECT group_concat(MigrationId || ':' || ProductVersion) FROM (SELECT * FROM __EFMigrationsHistory ORDER BY MigrationId)");
        var tableSql = await Scalar("SELECT sql FROM sqlite_master WHERE type='table' AND name='WebsiteContentState'");
        var indexes = await Scalar("SELECT group_concat(sql) FROM (SELECT sql FROM sqlite_master WHERE type='index' AND tbl_name='WebsiteContentState' AND sql IS NOT NULL ORDER BY name)");
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE LocalPreservationProbe (Id INTEGER PRIMARY KEY, Value TEXT NOT NULL); INSERT INTO LocalPreservationProbe VALUES (1, 'retained');");
        await db.Database.ExecuteSqlRawAsync("DROP TABLE WebsiteStudioComments; DROP TABLE WebsiteContentVersion; DROP TABLE WebsiteContentState;");
        await MasterAppSqliteSchemaBootstrapper.InitializeAsync(db, NullLogger.Instance);
        Assert.Equal(tableSql, await Scalar("SELECT sql FROM sqlite_master WHERE type='table' AND name='WebsiteContentState'"));
        Assert.Equal(indexes, await Scalar("SELECT group_concat(sql) FROM (SELECT sql FROM sqlite_master WHERE type='index' AND tbl_name='WebsiteContentState' AND sql IS NOT NULL ORDER BY name)"));
        await MasterAppSqliteSchemaBootstrapper.InitializeAsync(db, NullLogger.Instance);
        Assert.Equal("retained", await Scalar("SELECT Value FROM LocalPreservationProbe WHERE Id=1"));
        Assert.Equal(history, await Scalar("SELECT group_concat(MigrationId || ':' || ProductVersion) FROM (SELECT * FROM __EFMigrationsHistory ORDER BY MigrationId)"));
        Assert.Equal("0", await Scalar("SELECT count(*) FROM WebsiteContentState"));
        Assert.Equal("0", await Scalar("SELECT count(*) FROM WebsiteContentVersion"));
        Assert.Equal("0", await Scalar("SELECT count(*) FROM pragma_foreign_key_check"));
    }

    [Fact]
    public async Task FailedIndexCreationRollsBackAllMissingTables()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).Options);
        await MasterAppSqliteSchemaBootstrapper.InitializeAsync(db, NullLogger.Instance);
        await db.Database.ExecuteSqlRawAsync("DROP TABLE WebsiteStudioComments; DROP TABLE WebsiteContentVersion; DROP TABLE WebsiteContentState;");
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE LocalIndexCollision (Id INTEGER); CREATE INDEX IX_WebsiteContentState_OwnerKey_SiteKey ON LocalIndexCollision(Id);");
        await Assert.ThrowsAsync<SqliteException>(() => MasterAppSqliteSchemaBootstrapper.InitializeAsync(db, NullLogger.Instance));
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name IN ('WebsiteContentState', 'WebsiteContentVersion', 'WebsiteStudioComments')";
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task SqlServerBootstrapDoesNotConnectOrChangeSchema()
    {
        await using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseSqlServer("Server=127.0.0.1,1;Database=unreachable;Integrated Security=true;Connect Timeout=1").Options);
        await MasterAppSqliteSchemaBootstrapper.InitializeAsync(db, NullLogger.Instance);
        Assert.Equal(System.Data.ConnectionState.Closed, db.Database.GetDbConnection().State);
    }
}
