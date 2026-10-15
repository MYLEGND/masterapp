using Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using System.Text.Json;

// Synthetic compatibility fixture. This entry point is permitted only in the
// credential-free rehearsal job, against its disposable loopback SQL database.
// It is deliberately bounded: an unrepresented pending migration needs a new
// reviewed fixture before readiness can succeed.
internal static class RehearsalFixture
{
    internal static async Task Run(string operation)
    {
        var connection = Environment.GetEnvironmentVariable("LEGEND_ISOLATED_SQL");
        var sql = new SqlConnectionStringBuilder(connection);
        if (!System.Text.RegularExpressions.Regex.IsMatch(sql.DataSource, @"^127\.0\.0\.1,[1-9][0-9]{3,4}$") ||
            !System.Text.RegularExpressions.Regex.IsMatch(sql.InitialCatalog, @"^LegendRehearsal_[a-f0-9]{32}$") ||
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LEGEND_RELEASE_DB_CONNECTION")))
            throw new ProbeObservationFailure("INPUT_UNAVAILABLE");
        var options = new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseSqlServer(connection, provider => provider.CommandTimeout(60)).Options;
        await using var db = new MasterAppDbContext(options);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        if (!int.TryParse(Environment.GetEnvironmentVariable("LEGEND_REHEARSAL_PENDING_COUNT"), out var count) || count is < 1 or > 1000)
            throw new ProbeObservationFailure("INPUT_UNAVAILABLE");
        var contract = CandidateContract.Current(db);
        var pending = contract.Migrations.TakeLast(count).ToArray();
        // Reusable operation shape, with an explicit representative row fixture.
        // Other tables require a reviewed fixture; empty tables are not proof
        // that existing rows and constraints survive a migration.
        if (pending.Length != count || pending.Any(m => !m.Supported ||
            m.Columns.Any(c => c.Schema != "dbo" || c.Table != "MobileProfileSettings")))
            throw new ProbeObservationFailure("MIGRATION_COMPATIBILITY_UNPROVEN");
        if (operation == "initialize")
        {
            if (!await db.Database.EnsureCreatedAsync(timeout.Token))
                throw new ProbeObservationFailure("INPUT_UNAVAILABLE");
            db.MobileProfileSettings.Add(new Domain.Entities.MobileProfileSettings {
                ProfileId = Guid.NewGuid(), ParticipantType = "Agent", Bio = "synthetic migration fixture" });
            await db.SaveChangesAsync(timeout.Token);
            var assembly = db.GetService<IMigrationsAssembly>();
            // These tables are migration-owned SQL, absent from the EF model.
            var engineering = assembly.CreateMigration(
                assembly.Migrations["20261001070000_AddLegendEngineeringControlPlane"], db.Database.ProviderName!);
            foreach (var command in db.GetService<IMigrationsSqlGenerator>().Generate(engineering.UpOperations))
                await db.Database.ExecuteSqlRawAsync(command.CommandText, timeout.Token);
            // Construct the supported synthetic prior schema explicitly. Never
            // run arbitrary migration Down code or imply database rollback.
            var reverse = pending.Reverse().SelectMany(m => m.Columns.Reverse()).Select(c =>
                (MigrationOperation)new DropColumnOperation { Schema = c.Schema, Table = c.Table, Name = c.Name }).ToArray();
            foreach (var command in db.GetService<IMigrationsSqlGenerator>().Generate(reverse))
                await db.Database.ExecuteSqlRawAsync(command.CommandText, timeout.Token);
            var history = db.GetService<IHistoryRepository>();
            await db.Database.ExecuteSqlRawAsync(history.GetCreateScript(), timeout.Token);
            // Synthetic baseline, never copied production history or rows.
            foreach (var id in db.Database.GetMigrations().Where(id => !pending.Any(m => m.Id == id)).Concat(new[] {
                "20260213015339_FinanceToolStates_ByClientProfile", "20260321020000_AddAgentAssistants",
                "20260329093000_ExecutionMvp", "20260330094500_RepairAgentProfilesSqlite" }))
                await db.Database.ExecuteSqlRawAsync(history.GetInsertScript(new HistoryRow(id, "10.0.0")), timeout.Token);
        }
        else if (operation == "verify")
        {
            var row = await db.MobileProfileSettings.SingleAsync(timeout.Token);
            if (row.Bio != "synthetic migration fixture")
                throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
            await db.Database.OpenConnectionAsync(timeout.Token);
            foreach (var column in pending.SelectMany(m => m.Columns))
            {
                await using var command = db.Database.GetDbConnection().CreateCommand();
                using var quote = new SqlCommandBuilder();
                command.CommandText = "SELECT " + quote.QuoteIdentifier(column.Name) + " FROM dbo.MobileProfileSettings";
                command.CommandTimeout = 30;
                var actual = await command.ExecuteScalarAsync(timeout.Token);
                var expected = column.Default.ValueKind switch {
                    JsonValueKind.Null => null, JsonValueKind.String => column.Default.GetString(),
                    JsonValueKind.True => "True", JsonValueKind.False => "False",
                    JsonValueKind.Number => column.Default.GetRawText(), _ => throw new InvalidOperationException() };
                if ((actual is null or DBNull ? null : Convert.ToString(actual, System.Globalization.CultureInfo.InvariantCulture)) != expected)
                    throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
            }
        }
        else throw new ProbeObservationFailure("INPUT_UNAVAILABLE");
        Console.WriteLine("LEGEND_ISOLATED_FIXTURE:PROVEN");
    }
}
