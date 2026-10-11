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
        var contract = CandidateContract.Current(db);
        var requestedCount = Environment.GetEnvironmentVariable("LEGEND_REHEARSAL_PENDING_COUNT");
        if (requestedCount == "payment-and-successors")
            requestedCount = contract.Migrations.SkipWhile(m => m.Id != "20261011064911_AddBusinessScopedMerchantPayments").Count().ToString();
        if (!int.TryParse(requestedCount, out var count) || count is < 1 or > 1000)
            throw new ProbeObservationFailure("INPUT_UNAVAILABLE");
        var pending = contract.Migrations.TakeLast(count).ToArray();
        // Reusable operation shape, with an explicit representative row fixture.
        // Other tables require a reviewed fixture; empty tables are not proof
        // that existing rows and constraints survive a migration.
        const string paymentMigration = "20261011064911_AddBusinessScopedMerchantPayments";
        const string retentionMigration = "20261011120000_AddWebsiteSnapshotRetirement";
        var payments = pending.Any(m => m.Id == paymentMigration);
        var retention = pending.Any(m => m.Id == retentionMigration);
        if (pending.Length != count || pending.Any(m => !m.Supported ||
            (m.Id == retentionMigration && (m.Columns.Length != 1 || m.Tables.Length != 0 ||
                m.Indexes.Length != 0 || m.Guards.Length != 0 || m.Columns[0] is not {
                    Schema: "dbo", Table: "WebsiteContentVersion", Name: "SnapshotRetiredUtc",
                    Type: "datetime2", Nullable: true } || m.Columns[0].Default.ValueKind != JsonValueKind.Null)) ||
            (m.Id != paymentMigration && m.Id != retentionMigration && (m.Tables.Length != 0 || m.Indexes.Length != 0 || m.Guards.Length != 0 ||
            m.Columns.Any(c => c.Schema != "dbo" || c.Table != "MobileProfileSettings")))))
            throw new ProbeObservationFailure("MIGRATION_COMPATIBILITY_UNPROVEN");
        if (operation == "initialize")
        {
            if (!await db.Database.EnsureCreatedAsync(timeout.Token))
                throw new ProbeObservationFailure("INPUT_UNAVAILABLE");
            db.MobileProfileSettings.Add(new Domain.Entities.MobileProfileSettings {
                ProfileId = Guid.NewGuid(), ParticipantType = "Agent", Bio = "synthetic migration fixture" });
            if (retention)
            {
                var state = new Domain.Entities.WebsiteContentState { OwnerKey = "synthetic-owner", SiteKey = "legend", Revision = 2 };
                db.Add(state);
                for (var revision = 1; revision <= 2; revision++)
                    db.Add(new Domain.Entities.WebsiteContentVersion {
                        StateId = state.Id, Revision = revision, ActorUserId = "synthetic-owner",
                        DocumentJson = "{\"synthetic\":true}", CompiledPagesJson = "{\"/\":\"original\"}",
                        ImportReportJson = "{\"preserved\":true}" });
            }
            if (payments)
            {
                // Same checkout identity in different tenants is valid; neither
                // historical order may be re-keyed or changed by the migration.
                foreach (var key in new[] { "rehearsal-a", "rehearsal-b" })
                {
                    var business = new Domain.Entities.CommerceBusiness { Key = key, DisplayName = key };
                    db.CommerceBusinesses.Add(business);
                    db.CommerceOrders.Add(new Domain.Entities.CommerceOrder {
                        CommerceBusiness = business, CommerceBusinessId = business.Id,
                        OrderNumber = key, CheckoutAttemptId = "synthetic-shared-attempt",
                        SquarePaymentId = "synthetic-legacy-payment", TotalCents = 1234 });
                }
            }
            await db.SaveChangesAsync(timeout.Token);
            var assembly = db.GetService<IMigrationsAssembly>();
            // These tables are migration-owned SQL, absent from the EF model.
            var engineering = assembly.CreateMigration(
                assembly.Migrations["20261001070000_AddLegendEngineeringControlPlane"], db.Database.ProviderName!);
            foreach (var command in db.GetService<IMigrationsSqlGenerator>().Generate(engineering.UpOperations))
                await db.Database.ExecuteSqlRawAsync(command.CommandText, timeout.Token);
            // Construct the supported synthetic prior schema explicitly. Never
            // run arbitrary migration Down code or imply database rollback.
            var reverse = pending.Reverse().SelectMany(m =>
                m.Indexes.Where(i => !m.Tables.Any(t => t.Schema == i.Schema && t.Name == i.Table))
                    .Select(i => (MigrationOperation)new DropIndexOperation { Schema = i.Schema, Table = i.Table, Name = i.Name })
                .Concat(m.Tables.Reverse().Select(t => (MigrationOperation)new DropTableOperation { Schema = t.Schema, Name = t.Name }))
                .Concat(m.Columns.Reverse().Select(c => (MigrationOperation)new DropColumnOperation {
                    Schema = c.Schema, Table = c.Table, Name = c.Name }))).ToArray();
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
        else if (operation == "precondition")
        {
            if (!payments) throw new ProbeObservationFailure("INPUT_UNAVAILABLE");
            var applied = (await db.Database.GetAppliedMigrationsAsync(timeout.Token)).ToArray();
            await CandidateContract.Verify(db, contract, applied, timeout.Token);
            await using var transaction = await db.Database.BeginTransactionAsync(timeout.Token);
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE dbo.CommerceOrders SET CommerceBusinessId = (SELECT TOP (1) Id FROM dbo.CommerceBusinesses ORDER BY [Key])",
                timeout.Token);
            try
            {
                await CandidateContract.Verify(db, contract, applied, timeout.Token);
                throw new InvalidOperationException("Duplicate checkout identity was accepted");
            }
            catch (ProbeObservationFailure error) when (error.Message == "MERCHANT_CHECKOUT_DUPLICATE_IDENTITY_REQUIRES_REVIEW") { }
            await transaction.RollbackAsync(timeout.Token);
        }
        else if (operation == "migrate")
        {
            // Only this disposable, synthetic database can reach this branch.
            await db.Database.MigrateAsync(timeout.Token);
        }
        else if (operation == "adversarial")
        {
            if (!payments) throw new ProbeObservationFailure("INPUT_UNAVAILABLE");
            var applied = db.Database.GetMigrations().ToArray();
            foreach (var mutation in new[] {
                "ALTER INDEX [IX_CommercePaymentReceipts_Provider_Environment_AccountId_EventId] ON [dbo].[CommercePaymentReceipts] DISABLE",
                "ALTER TABLE [dbo].[CommercePaymentEffects] NOCHECK CONSTRAINT [FK_CommercePaymentEffects_CommercePaymentAttempts_AttemptId]",
                "ALTER TABLE [dbo].[CommerceOrders] ALTER COLUMN [PaymentReference] nvarchar(199) NULL"
            })
            {
                await using var transaction = await db.Database.BeginTransactionAsync(timeout.Token);
                await db.Database.ExecuteSqlRawAsync(mutation, timeout.Token);
                try
                {
                    await CandidateContract.Verify(db, contract, applied, timeout.Token);
                    throw new InvalidOperationException("Schema drift was accepted");
                }
                catch (ProbeObservationFailure error) when (error.Message == "PHYSICAL_SCHEMA_DRIFT") { }
                await transaction.RollbackAsync(timeout.Token);
            }
            await CandidateContract.Verify(db, contract, applied, timeout.Token);
        }
        else if (operation == "verify")
        {
            var row = await db.MobileProfileSettings.SingleAsync(timeout.Token);
            if (row.Bio != "synthetic migration fixture")
                throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
            if (retention)
            {
                var versions = await db.Set<Domain.Entities.WebsiteContentVersion>().AsNoTracking().ToArrayAsync(timeout.Token);
                if (versions.Length != 2 || versions.Select(v => v.StateId).Distinct().Count() != 1 ||
                    versions.Select(v => v.Revision).Order().SequenceEqual(new long[] { 1, 2 }) is false ||
                    versions.Any(v => v.SnapshotRetiredUtc is not null || v.ActorUserId != "synthetic-owner" ||
                        v.DocumentJson != "{\"synthetic\":true}" || v.CompiledPagesJson != "{\"/\":\"original\"}" ||
                        v.ImportReportJson != "{\"preserved\":true}"))
                    throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
            }
            await db.Database.OpenConnectionAsync(timeout.Token);
            if (payments)
            {
                var orders = await db.CommerceOrders.AsNoTracking().OrderBy(o => o.OrderNumber).ToArrayAsync(timeout.Token);
                if (orders.Length != 2 || orders.Select(o => o.CommerceBusinessId).Distinct().Count() != 2 ||
                    orders.Any(o => o.CheckoutAttemptId != "synthetic-shared-attempt" || o.TotalCents != 1234 ||
                        o.SquarePaymentId != "synthetic-legacy-payment" || o.PaymentProvider is not null ||
                        o.PaymentReference is not null || o.PaymentEnvironment is not null))
                    throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
                await CandidateContract.Verify(db, contract, db.Database.GetMigrations().ToArray(), timeout.Token);
            }
            foreach (var column in pending.SelectMany(m => m.Columns).Where(c => c.Table == "MobileProfileSettings"))
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
