using Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;

// Synthetic compatibility fixture. This entry point is permitted only in the
// credential-free rehearsal job, against its disposable loopback SQL database.
// It is deliberately bounded: an unrepresented pending migration needs a new
// reviewed fixture before readiness can succeed.
internal static partial class RehearsalFixture
{
    internal static bool IsDiagnosticsFixture(CandidateContract.Entry entry) =>
        entry.Id == CandidateContract.DiagnosticsMigration && entry.Supported &&
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(entry, CandidateContract.Json))) == CandidateContract.DiagnosticsEntrySha256;

    private static Domain.Entities.RuntimeDiagnosticIncident[] DiagnosticRows() => new[] { 1, 2 }.Select(i => new Domain.Entities.RuntimeDiagnosticIncident
    {
        Id = Guid.Parse(i == 1 ? "10000000-0000-0000-0000-000000000001" : "10000000-0000-0000-0000-000000000002"),
        DeduplicationKey = new string(i == 1 ? 'a' : 'b', 64), AppIdentifier = i == 1 ? "AgentPortal" : "ProtectWebsite",
        Platform = "server", Route = "/synthetic", ErrorName = "SyntheticFailure", Summary = "sanitized fixture",
        Category = "unhandled_exception", Disposition = "Observed", ReviewVersion = i, Occurrences = i + 2,
        FirstSeenUtc = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Unspecified),
        LastSeenUtc = new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Unspecified),
        ExpiresUtc = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Unspecified)
    }).ToArray();

    private static async Task VerifyDiagnosticsData(MasterAppDbContext db, CancellationToken ct)
    {
        var expected = DiagnosticRows();
        var ids = expected.Select(x => x.Id).ToArray();
        var actual = await db.Set<Domain.Entities.RuntimeDiagnosticIncident>().AsNoTracking().Where(x => ids.Contains(x.Id)).OrderBy(x => x.Id).ToArrayAsync(ct);
        if (JsonSerializer.Serialize(actual, CandidateContract.Json) != JsonSerializer.Serialize(expected, CandidateContract.Json))
            throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
        // New nullable column starts NULL; the former application's explicit fields
        // remain readable and writable without supplying a new value.
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM dbo.RuntimeDiagnosticIncidents WHERE PerformanceEvidenceJson IS NOT NULL";
        command.CommandTimeout = 30;
        if (Convert.ToInt32(await command.ExecuteScalarAsync(ct)) != 0) throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("UPDATE dbo.RuntimeDiagnosticIncidents SET Summary = 'old application write' WHERE Id = '10000000-0000-0000-0000-000000000001'", ct);
        if (await db.Set<Domain.Entities.RuntimeDiagnosticIncident>().Where(x => x.Id == expected[0].Id).Select(x => x.Summary).SingleAsync(ct) != "old application write")
            throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
        await transaction.RollbackAsync(ct);
    }

    internal const string ManagementMigration = "20261026210738_AddReceptionistManagement";
    internal static bool IsManagementFixture(CandidateContract.Entry entry) =>
        entry.Id == ManagementMigration && entry.Supported &&
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(entry, CandidateContract.Json))) == "4505723c4487d382bd8bcde553d5e5d835dec499564c2b308469a4e2861c3d3d";
    internal const string ReceptionistMigration = "20261020160000_AddReceptionistCoordination";
    internal const string CanonicalPaymentMigration = "20261026000134_AddCanonicalSubscriptionPaymentCompletion";
    internal static bool IsTrainingFixture(CandidateContract.Entry entry) =>
        entry.Id == CandidateContract.TrainingMigration && entry.Supported &&
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(entry, CandidateContract.Json))) ==
            "5a543b02a2a1ae2f2bb35d5e2a8fb51f41e9601b1ef4de2707ec6db51029be10";
    internal static bool IsCanonicalPaymentFixture(CandidateContract.Entry entry) =>
        entry.Id == CanonicalPaymentMigration && entry.Supported &&
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(entry, CandidateContract.Json))) ==
            "6fe5eaa7f0070c282a5219075d4efe101dca16a7dacae605c87b58f0f9027975";
    // Pins the complete declarative shape, including every CHECK, collation,
    // key and default. A same-id edited migration requires a reviewed fixture.
    internal static bool IsReceptionistFixture(CandidateContract.Entry entry) =>
        entry.Id == ReceptionistMigration && entry.Supported &&
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(entry, CandidateContract.Json))) ==
            "d2da70a25230e14bf520e4129ac95a9fc558e9316385cedfcb172a5d82564fd6";

    internal static async Task Run(string operation)
    {
        try { await RunCore(operation); }
        catch (Exception error)
        {
            // Preserve the original exception and failure gate. Emit only a fixed
            // stage and classification; never SQL text, credentials or raw messages.
            var stage = operation is "initialize" or "precondition" or "migrate" or "verify" or "adversarial"
                ? operation : "dispatch";
            var reason = error.GetBaseException() switch
            {
                SqlException sql => "SQL:" + sql.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ProbeObservationFailure probe when probe.Message is "INPUT_UNAVAILABLE" or
                    "MIGRATION_COMPATIBILITY_UNPROVEN" or "PHYSICAL_SCHEMA_DRIFT" or
                    "MERCHANT_CHECKOUT_DUPLICATE_IDENTITY_REQUIRES_REVIEW" => probe.Message,
                OperationCanceledException => "TIMEOUT",
                InvalidOperationException => "INVALID_OPERATION",
                _ => "UNCLASSIFIED"
            };
            var types = new List<string>();
            for (Exception? current = error; current is not null && types.Count < 4; current = current.InnerException)
            {
                var name = current.GetType().FullName;
                types.Add(name is not null && System.Text.RegularExpressions.Regex.IsMatch(
                    name, @"\A[A-Za-z_][A-Za-z0-9_.+]{0,159}\z") ? name : "UnknownExceptionType");
            }
            var location = "UNKNOWN";
            foreach (var frame in new System.Diagnostics.StackTrace(error, true).GetFrames())
            {
                var file = Path.GetFileName(frame.GetFileName());
                var line = frame.GetFileLineNumber();
                if (file is not ("RehearsalFixture.cs" or "CandidateContract.cs" or "CandidateContract.Structures.cs") ||
                    line is < 1 or > 99999) continue;
                location = file + ":" + line.ToString(System.Globalization.CultureInfo.InvariantCulture);
                break;
            }
            Console.Error.WriteLine("LEGEND_ISOLATED_FIXTURE:UNPROVEN:" + stage + ":" + reason +
                ":TYPES:" + string.Join(">", types) + ":LOCATION:" + location);
            throw;
        }
    }

    private static async Task RunCore(string operation)
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
        if (requestedCount == "canonical-payment-only")
        {
            if (contract.Migrations.LastOrDefault()?.Id != CanonicalPaymentMigration)
                throw new ProbeObservationFailure("MIGRATION_COMPATIBILITY_UNPROVEN");
            requestedCount = "1";
        }
        if (requestedCount is "canonical-payment-and-training" or "training-only")
        {
            if (contract.Migrations.LastOrDefault()?.Id != CandidateContract.TrainingMigration ||
                contract.Migrations[^2].Id != CanonicalPaymentMigration)
                throw new ProbeObservationFailure("MIGRATION_COMPATIBILITY_UNPROVEN");
            requestedCount = requestedCount == "training-only" ? "1" : "2";
        }
        if (requestedCount is "management-only" or "training-and-management" or "canonical-payment-training-management")
        {
            var expected = new[] { CanonicalPaymentMigration, CandidateContract.TrainingMigration, ManagementMigration };
            if (!contract.Migrations.TakeLast(3).Select(m => m.Id).SequenceEqual(expected))
                throw new ProbeObservationFailure("MIGRATION_COMPATIBILITY_UNPROVEN");
            requestedCount = requestedCount == "management-only" ? "1" : requestedCount == "training-and-management" ? "2" : "3";
        }
        if (requestedCount is "browser-only" or "management-and-browser" or "training-management-browser" or "canonical-payment-training-management-browser")
        {
            var expected = new[] { CanonicalPaymentMigration, CandidateContract.TrainingMigration, ManagementMigration, CandidateContract.BrowserMigration };
            if (!contract.Migrations.TakeLast(4).Select(m => m.Id).SequenceEqual(expected))
                throw new ProbeObservationFailure("MIGRATION_COMPATIBILITY_UNPROVEN");
            requestedCount = requestedCount switch { "browser-only" => "1", "management-and-browser" => "2",
                "training-management-browser" => "3", _ => "4" };
        }
        if (requestedCount is "diagnostics-only" or "browser-diagnostics" or "management-browser-diagnostics" or "training-management-browser-diagnostics" or "canonical-payment-training-management-browser-diagnostics")
        {
            var expected = new[] { CanonicalPaymentMigration, CandidateContract.TrainingMigration, ManagementMigration,
                CandidateContract.BrowserMigration, CandidateContract.DiagnosticsMigration };
            if (!contract.Migrations.TakeLast(5).Select(m => m.Id).SequenceEqual(expected))
                throw new ProbeObservationFailure("MIGRATION_COMPATIBILITY_UNPROVEN");
            requestedCount = requestedCount switch { "diagnostics-only" => "1", "browser-diagnostics" => "2",
                "management-browser-diagnostics" => "3", "training-management-browser-diagnostics" => "4", _ => "5" };
        }
        if (requestedCount is "crm-only" or "diagnostics-crm" or "browser-diagnostics-crm" or "management-browser-diagnostics-crm" or "training-management-browser-diagnostics-crm" or "canonical-payment-training-management-browser-diagnostics-crm")
        {
            var expected = new[] { CanonicalPaymentMigration, CandidateContract.TrainingMigration, ManagementMigration,
                CandidateContract.BrowserMigration, CandidateContract.DiagnosticsMigration, CandidateContract.CrmMigration };
            if (!contract.Migrations.TakeLast(6).Select(m => m.Id).SequenceEqual(expected))
                throw new ProbeObservationFailure("MIGRATION_COMPATIBILITY_UNPROVEN");
            requestedCount = requestedCount switch { "crm-only" => "1", "diagnostics-crm" => "2", "browser-diagnostics-crm" => "3",
                "management-browser-diagnostics-crm" => "4", "training-management-browser-diagnostics-crm" => "5", _ => "6" };
        }
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
        var browser = pending.Any(m => m.Id == CandidateContract.BrowserMigration);
        var crm = pending.Any(m => m.Id == CandidateContract.CrmMigration);
        var diagnostics = pending.Any(m => m.Id == CandidateContract.DiagnosticsMigration);
        var management = pending.Any(m => m.Id == ManagementMigration);
        var receptionist = pending.Any(m => m.Id == ReceptionistMigration);
        var training = pending.Any(m => m.Id == CandidateContract.TrainingMigration);
        var canonicalPayments = pending.Any(m => m.Id == CanonicalPaymentMigration);
        if (pending.Length != count || pending.Any(m => !m.Supported ||
            (m.Id == retentionMigration && (m.Columns.Length != 1 || m.Tables.Length != 0 ||
                m.Indexes.Length != 0 || m.Guards.Length != 0 || m.ForeignKeys.Length != 0 || m.Columns[0] is not {
                    Schema: "dbo", Table: "WebsiteContentVersion", Name: "SnapshotRetiredUtc",
                    Type: "datetime2", Nullable: true } || m.Columns[0].Default.ValueKind != JsonValueKind.Null)) ||
            (m.Id == CandidateContract.CrmMigration && !IsCrmFixture(m)) ||
            (m.Id == CandidateContract.DiagnosticsMigration && !IsDiagnosticsFixture(m)) ||
            (m.Id == ReceptionistMigration && !IsReceptionistFixture(m)) ||
            (m.Id == CandidateContract.BrowserMigration && !IsBrowserFixture(m)) ||
            (m.Id == ManagementMigration && !IsManagementFixture(m)) ||
            (m.Id == CanonicalPaymentMigration && !IsCanonicalPaymentFixture(m)) ||
            (m.Id == CandidateContract.TrainingMigration && !IsTrainingFixture(m)) ||
            (m.Id != CandidateContract.CrmMigration && m.Id != CandidateContract.DiagnosticsMigration && m.Id != CandidateContract.BrowserMigration && m.Id != ManagementMigration && m.Id != paymentMigration && m.Id != retentionMigration && m.Id != ReceptionistMigration && m.Id != CanonicalPaymentMigration && m.Id != CandidateContract.TrainingMigration &&
            (m.Tables.Length != 0 || m.Indexes.Length != 0 || m.Guards.Length != 0 || m.ForeignKeys.Length != 0 ||
            m.Columns.Any(c => c.Schema != "dbo" || c.Table != "MobileProfileSettings")))))
            throw new ProbeObservationFailure("MIGRATION_COMPATIBILITY_UNPROVEN");
        var appliedReceptionist = contract.Migrations.SingleOrDefault(m => m.Id == ReceptionistMigration);
        if (appliedReceptionist is not null && !receptionist && !IsReceptionistFixture(appliedReceptionist))
            throw new ProbeObservationFailure("MIGRATION_COMPATIBILITY_UNPROVEN");
        if (operation == "initialize")
        {
            if (!await db.Database.EnsureCreatedAsync(timeout.Token))
                throw new ProbeObservationFailure("INPUT_UNAVAILABLE");
            db.MobileProfileSettings.Add(new Domain.Entities.MobileProfileSettings {
                ProfileId = Guid.NewGuid(), ParticipantType = "Agent", Bio = "synthetic migration fixture" });
            if (crm) await SeedCrmData(db, timeout.Token);
            if (diagnostics) db.AddRange(DiagnosticRows());
            if (receptionist || management || browser) db.AddRange(MarketingRows());
            if (canonicalPayments) db.AddRange(CanonicalPaymentRows());
            if (training) db.AddRange(TrainingLegacyRows());
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
            if (crm) await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE dbo.WebsiteLeadIntakeLinks DROP CONSTRAINT CK_WebsiteLeadIntakeLinks_HistoryAssociation; ALTER TABLE dbo.WebsiteLeadIntakeLinks ALTER COLUMN WorkstationLeadId nvarchar(64) NOT NULL", timeout.Token);
            if (browser) await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE dbo.ReceptionistCalls DROP CONSTRAINT CK_ReceptionistCall_BrowserCreation", timeout.Token);
            var reverse = pending.Reverse().SelectMany(m =>
                m.ForeignKeys.Select(f => (MigrationOperation)new DropForeignKeyOperation {
                    Schema = f.Schema, Table = f.Table, Name = f.Constraint.Name })
                .Concat(m.Indexes.Where(i => !m.Tables.Any(t => t.Schema == i.Schema && t.Name == i.Table))
                    .Select(i => (MigrationOperation)new DropIndexOperation { Schema = i.Schema, Table = i.Table, Name = i.Name }))
                .Concat(m.Tables.Reverse().Select(t => (MigrationOperation)new DropTableOperation { Schema = t.Schema, Name = t.Name }))
                .Concat(m.Columns.Reverse().Select(c => (MigrationOperation)new DropColumnOperation {
                    Schema = c.Schema, Table = c.Table, Name = c.Name }))).ToArray();
            foreach (var command in db.GetService<IMigrationsSqlGenerator>().Generate(reverse))
                await db.Database.ExecuteSqlRawAsync(command.CommandText, timeout.Token);
            if (browser && !receptionist) await db.Database.ExecuteSqlRawAsync("""
                ALTER TABLE dbo.ReceptionistCalls ALTER COLUMN ProviderSessionId nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL;
                CREATE UNIQUE INDEX IX_ReceptionistCalls_Provider_ProviderProjectKey_ProviderSessionId
                    ON dbo.ReceptionistCalls(Provider,ProviderProjectKey,ProviderSessionId);
                """, timeout.Token);
            if (training)
                await db.Database.ExecuteSqlRawAsync("CREATE UNIQUE INDEX IX_ClientSubscriptions_ClientProfileId ON dbo.ClientSubscriptions(ClientProfileId) WHERE [Status] <> 'Canceled' AND [Status] <> 'ActivationFailed'", timeout.Token);
            // EnsureCreated follows the model, which omits these migration-owned
            // defaults. Reconstruct the pinned applied migration's physical shape
            // before recording synthetic history; never repair an observed database.
            if (appliedReceptionist is not null && !receptionist)
                await db.Database.ExecuteSqlRawAsync("""
                    ALTER TABLE dbo.MarketingConnections ADD CONSTRAINT DF_Rehearsal_ReceptionistAdmissionEnabled
                        DEFAULT (0) FOR ReceptionistAdmissionEnabled;
                    ALTER TABLE dbo.MarketingConnections ADD CONSTRAINT DF_Rehearsal_ReceptionistControlRevision
                        DEFAULT ('00000000-0000-0000-0000-000000000000') FOR ReceptionistControlRevision;
                    """, timeout.Token);
            var history = db.GetService<IHistoryRepository>();
            await db.Database.ExecuteSqlRawAsync(history.GetCreateScript(), timeout.Token);
            if ((management || browser) && !receptionist)
            {
                await db.Database.OpenConnectionAsync(timeout.Token);
                await VerifyReceptionistData(db, timeout.Token, persist: true);
                await SaveManagementPredecessor(db, sql.InitialCatalog, timeout.Token);
            }
            // Synthetic baseline, never copied production history or rows.
            foreach (var id in db.Database.GetMigrations().Where(id => !pending.Any(m => m.Id == id)).Concat(new[] {
                "20260213015339_FinanceToolStates_ByClientProfile", "20260321020000_AddAgentAssistants",
                "20260329093000_ExecutionMvp", "20260330094500_RepairAgentProfilesSqlite" }))
                await db.Database.ExecuteSqlRawAsync(history.GetInsertScript(new HistoryRow(id, "10.0.0")), timeout.Token);
        }
        else if (operation == "precondition")
        {
            if (!payments && !receptionist && !canonicalPayments && !training && !management && !browser && !diagnostics && !crm) throw new ProbeObservationFailure("INPUT_UNAVAILABLE");
            await VerifyCatalogBatchProtocol(db, timeout.Token);
            var applied = (await db.Database.GetAppliedMigrationsAsync(timeout.Token)).ToArray();
            await CandidateContract.Verify(db, contract, applied, timeout.Token);
            if (crm) await CrmDrift(db, contract, applied, false, timeout.Token);
            if (diagnostics)
            {
                await using var transaction = await db.Database.BeginTransactionAsync(timeout.Token);
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE dbo.RuntimeDiagnosticIncidents ADD PerformanceEvidenceJson nvarchar(max) NULL", timeout.Token);
                try { await CandidateContract.Verify(db, contract, applied, timeout.Token); throw new InvalidOperationException("Unrecorded diagnostic column accepted"); }
                catch (ProbeObservationFailure error) when (error.Message == "PHYSICAL_SCHEMA_DRIFT") { }
                await transaction.RollbackAsync(timeout.Token);
            }
            if (training)
            {
                foreach (var change in new[] {
                    "DROP INDEX IX_ClientSubscriptions_ClientProfileId ON dbo.ClientSubscriptions",
                    "DROP INDEX IX_ClientSubscriptions_ClientProfileId ON dbo.ClientSubscriptions; CREATE INDEX IX_ClientSubscriptions_ClientProfileId ON dbo.ClientSubscriptions(ClientProfileId) WHERE [Status] <> 'Canceled' AND [Status] <> 'ActivationFailed'",
                    "DROP INDEX IX_ClientSubscriptions_ClientProfileId ON dbo.ClientSubscriptions; CREATE UNIQUE INDEX IX_ClientSubscriptions_ClientProfileId ON dbo.ClientSubscriptions(ClientProfileId) WHERE [Status] = 'Active'"
                })
                {
                    await using var transaction = await db.Database.BeginTransactionAsync(timeout.Token);
                    await db.Database.ExecuteSqlRawAsync(change, timeout.Token);
                    try { await CandidateContract.Verify(db, contract, applied, timeout.Token); throw new InvalidOperationException("Invalid predecessor index accepted"); }
                    catch (ProbeObservationFailure error) when (error.Message == "PHYSICAL_SCHEMA_DRIFT") { }
                    await transaction.RollbackAsync(timeout.Token);
                }
            }
            if (payments)
            {
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
        }
        else if (operation == "migrate")
        {
            // Only this disposable, synthetic database can reach this branch.
            await db.Database.MigrateAsync(timeout.Token);
        }
        else if (operation == "adversarial")
        {
            if (!payments && !receptionist && !canonicalPayments && !training && !management && !browser && !diagnostics && !crm) throw new ProbeObservationFailure("INPUT_UNAVAILABLE");
            var applied = db.Database.GetMigrations().ToArray();
            var mutations = payments ? new[] {
                "ALTER INDEX [IX_CommercePaymentReceipts_Provider_Environment_AccountId_EventId] ON [dbo].[CommercePaymentReceipts] DISABLE",
                "ALTER TABLE [dbo].[CommercePaymentEffects] NOCHECK CONSTRAINT [FK_CommercePaymentEffects_CommercePaymentAttempts_AttemptId]",
                "ALTER TABLE [dbo].[CommerceOrders] ALTER COLUMN [PaymentReference] nvarchar(199) NULL"
            } : Array.Empty<string>();
            if (training) mutations = mutations.Concat(new[] {
                "ALTER INDEX IX_ClientSubscriptions_ClientProfileId_SubscriptionScope ON dbo.ClientSubscriptions DISABLE",
                "DROP INDEX IX_ClientSubscriptions_ClientProfileId_SubscriptionScope ON dbo.ClientSubscriptions",
                "ALTER TABLE dbo.ClientSubscriptions NOCHECK CONSTRAINT FK_ClientSubscriptions_CommerceProducts_CommerceProductId",
                "ALTER TABLE dbo.ClientSubscriptionOffers ALTER COLUMN TrainingPlanName nvarchar(255) NULL",
                "CREATE UNIQUE INDEX IX_ClientSubscriptions_ClientProfileId ON dbo.ClientSubscriptions(ClientProfileId) WHERE [Status] <> 'Canceled' AND [Status] <> 'ActivationFailed'"
            }).ToArray();
            if (diagnostics) mutations = mutations.Concat(new[] {
                "ALTER TABLE dbo.RuntimeDiagnosticIncidents DROP COLUMN PerformanceEvidenceJson",
                "ALTER TABLE dbo.RuntimeDiagnosticIncidents ALTER COLUMN PerformanceEvidenceJson nvarchar(2048) NULL",
                "UPDATE dbo.RuntimeDiagnosticIncidents SET PerformanceEvidenceJson = '{}'; ALTER TABLE dbo.RuntimeDiagnosticIncidents ALTER COLUMN PerformanceEvidenceJson nvarchar(max) NOT NULL",
                "ALTER TABLE dbo.RuntimeDiagnosticIncidents ADD DEFAULT ('{}') FOR PerformanceEvidenceJson"
            }).ToArray();
            if (browser) mutations = mutations.Concat(BrowserDrift()).ToArray();
            if (management) mutations = mutations.Concat(ManagementDrift(pending.Single(m => m.Id == ManagementMigration))).ToArray();
            if (receptionist) mutations = mutations.Concat(ReceptionistDrift(pending.Single(m => m.Id == ReceptionistMigration), applied.Contains(ManagementMigration))).ToArray();
            if (appliedReceptionist is not null && !receptionist)
                mutations = mutations.Concat(new[] {
                    "ALTER TABLE dbo.MarketingConnections DROP CONSTRAINT DF_Rehearsal_ReceptionistAdmissionEnabled",
                    "ALTER TABLE dbo.MarketingConnections DROP CONSTRAINT DF_Rehearsal_ReceptionistControlRevision",
                    "ALTER TABLE dbo.MarketingConnections DROP CONSTRAINT DF_Rehearsal_ReceptionistAdmissionEnabled; ALTER TABLE dbo.MarketingConnections ADD DEFAULT (1) FOR ReceptionistAdmissionEnabled",
                    "ALTER TABLE dbo.MarketingConnections DROP CONSTRAINT DF_Rehearsal_ReceptionistControlRevision; ALTER TABLE dbo.MarketingConnections ADD DEFAULT ('00000000-0000-0000-0000-000000000001') FOR ReceptionistControlRevision"
                }).ToArray();
            if (canonicalPayments) mutations = mutations.Concat(new[] {
                "ALTER INDEX [IX_SubscriptionPaymentRefunds_SubscriptionPaymentId_IdempotencyKey] ON dbo.SubscriptionPaymentRefunds DISABLE",
                "ALTER INDEX [IX_SubscriptionPaymentCompletionEffects_SubscriptionPaymentId_Kind] ON dbo.SubscriptionPaymentCompletionEffects DISABLE",
                "ALTER TABLE dbo.SubscriptionPaymentRefunds NOCHECK CONSTRAINT [FK_SubscriptionPaymentRefunds_SubscriptionPayments_SubscriptionPaymentId]",
                "ALTER TABLE dbo.SubscriptionPaymentCompletionEffects NOCHECK CONSTRAINT [FK_SubscriptionPaymentCompletionEffects_SubscriptionPayments_SubscriptionPaymentId]",
                "ALTER TABLE dbo.SubscriptionPaymentRefunds NOCHECK CONSTRAINT [CK_SubscriptionPaymentRefunds_PositiveAmount]",
                "ALTER TABLE dbo.SubscriptionPaymentRefunds ALTER COLUMN Currency nvarchar(2) NOT NULL"
            }).ToArray();
            foreach (var mutation in mutations)
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
            if (crm)
            {
                await VerifyCrmData(db, timeout.Token);
                await CrmDrift(db, contract, db.Database.GetMigrations().ToArray(), true, timeout.Token);
            }
            if (diagnostics) await VerifyDiagnosticsData(db, timeout.Token);
            if (canonicalPayments) await VerifyCanonicalPaymentData(db, timeout.Token);
            if ((management || browser) && !receptionist) await VerifyManagementPredecessor(db, sql.InitialCatalog, timeout.Token);
            if (receptionist)
            {
                var expectedRows = MarketingRows();
                var ids = expectedRows.Select(r => r.Id).ToArray();
                var actualRows = await db.Set<Domain.Entities.MarketingConnection>().AsNoTracking()
                    .Where(r => ids.Contains(r.Id)).OrderBy(r => r.Id).ToArrayAsync(timeout.Token);
                if (actualRows.Length != expectedRows.Length || actualRows.Where((row, index) =>
                    !JsonNode.DeepEquals(LegacyMarketing(row), LegacyMarketing(expectedRows[index]))).Any())
                    throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
                await using var defaults = db.Database.GetDbConnection().CreateCommand();
                defaults.CommandText = """
                    SELECT COUNT(*) FROM dbo.MarketingConnections
                    WHERE OwnerKey IN ('rehearsal-voice-a','rehearsal-voice-b')
                      AND (ReceptionistActiveConfigurationId IS NOT NULL OR ReceptionistIngressKey IS NOT NULL
                        OR ReceptionistProviderProjectKey IS NOT NULL OR ReceptionistAdmissionEnabled <> 0
                        OR ReceptionistControlRevision <> '00000000-0000-0000-0000-000000000000')
                    """;
                defaults.CommandTimeout = 30;
                if (Convert.ToInt32(await defaults.ExecuteScalarAsync(timeout.Token)) != 0)
                    throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
                await VerifyReceptionistData(db, timeout.Token, persist: management || browser);
            }
            if (browser) await VerifyBrowserData(db, timeout.Token);
            if (management) await VerifyManagementData(db, timeout.Token);
            if (payments)
            {
                var orders = await db.CommerceOrders.AsNoTracking().OrderBy(o => o.OrderNumber).ToArrayAsync(timeout.Token);
                if (orders.Length != 2 || orders.Select(o => o.CommerceBusinessId).Distinct().Count() != 2 ||
                    orders.Any(o => o.CheckoutAttemptId != "synthetic-shared-attempt" || o.TotalCents != 1234 ||
                        o.SquarePaymentId != "synthetic-legacy-payment" || o.PaymentProvider is not null ||
                        o.PaymentReference is not null || o.PaymentEnvironment is not null))
                    throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
            }
            if (training) await VerifyTrainingData(db, timeout.Token);
            await CandidateContract.Verify(db, contract, db.Database.GetMigrations().ToArray(), timeout.Token);
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

    private static Domain.Entities.SubscriptionPayment[] CanonicalPaymentRows() => new[] {
        new Domain.Entities.SubscriptionPayment { Id = Guid.Parse("00000000-0000-0000-0000-000000000201"),
            ProviderPaymentId = "synthetic-payment-a", AmountCents = 1234, IdempotencyKey = "synthetic-key-a",
            CreatedUtc = new DateTime(2026, 1, 1), UpdatedUtc = new DateTime(2026, 1, 2) },
        new Domain.Entities.SubscriptionPayment { Id = Guid.Parse("00000000-0000-0000-0000-000000000202"),
            ProviderPaymentId = "synthetic-payment-b", AmountCents = 5678, IdempotencyKey = "synthetic-key-b",
            CreatedUtc = new DateTime(2026, 1, 3), UpdatedUtc = new DateTime(2026, 1, 4) }
    };

    private static JsonNode LegacyPayment(Domain.Entities.SubscriptionPayment row)
    {
        var value = JsonSerializer.SerializeToNode(row)!.AsObject();
        value.Remove(nameof(row.RowVersion)); // SQL-generated; excluded from the deterministic synthetic value comparison.
        return value;
    }

    private static async Task VerifyCanonicalPaymentData(MasterAppDbContext db, CancellationToken cancellation)
    {
        var expected = CanonicalPaymentRows();
        var actual = await db.Set<Domain.Entities.SubscriptionPayment>().AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(cancellation);
        if (actual.Length != expected.Length || actual.Where((row, i) =>
            !JsonNode.DeepEquals(LegacyPayment(row), LegacyPayment(expected[i]))).Any())
            throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
        var assembly = db.GetService<IMigrationsAssembly>();
        var migration = assembly.CreateMigration(assembly.Migrations[CanonicalPaymentMigration], db.Database.ProviderName!);
        var down = db.GetService<IMigrationsSqlGenerator>().Generate(migration.DownOperations);
        const string payment = "'00000000-0000-0000-0000-000000000201'";
        var refund = "INSERT dbo.SubscriptionPaymentRefunds (Id,SubscriptionPaymentId,Currency,IdempotencyKey,AmountMinor,State,ProviderReference,CreatedUtc,Version) VALUES (NEWID()," + payment + ",'USD',REPLICATE('r',160),100,'Completed','synthetic-refund',SYSUTCDATETIME(),1)";
        var effect = "INSERT dbo.SubscriptionPaymentCompletionEffects (Id,SubscriptionPaymentId,Kind,State,RetryCount,CreatedUtc,Version) VALUES (NEWID()," + payment + ",'Complete','Pending',0,SYSUTCDATETIME(),1)";
        // Exercise only the pinned migration in a rolled-back disposable transaction.
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellation))
        {
            foreach (var command in down) await db.Database.ExecuteSqlRawAsync(command.CommandText, cancellation);
            await transaction.RollbackAsync(cancellation);
        }
        foreach (var history in new[] { refund, effect,
            "UPDATE dbo.SubscriptionPayments SET CommerceCaptureVerifiedUtc=SYSUTCDATETIME() WHERE Id=" + payment,
            "UPDATE dbo.SubscriptionPayments SET CommerceCompletionContextJson=N'{{}}' WHERE Id=" + payment })
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellation);
            await db.Database.ExecuteSqlRawAsync(history, cancellation);
            var rejected = false;
            try { foreach (var command in down) await db.Database.ExecuteSqlRawAsync(command.CommandText, cancellation); }
            catch (SqlException error) when (error.Number == 51000) { rejected = true; }
            if (!rejected) throw new InvalidOperationException("History-bearing rollback was accepted");
            await transaction.RollbackAsync(cancellation);
        }
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellation))
        {
            await db.Database.ExecuteSqlRawAsync(refund, cancellation);
            await db.Database.ExecuteSqlRawAsync(refund.Replace("REPLICATE('r',160)", "'second-refund'"), cancellation);
            await db.Database.ExecuteSqlRawAsync(effect, cancellation);
            var rows = await db.Set<Domain.Entities.SubscriptionPaymentRefund>().AsNoTracking().ToArrayAsync(cancellation);
            if (rows.Length != 2 || rows.Sum(x => x.AmountMinor) != 200 || !rows.Any(x => x.IdempotencyKey == new string('r', 160)))
                throw new InvalidOperationException("Canonical refund storage did not round trip");
            await transaction.RollbackAsync(cancellation);
        }
        foreach (var scenario in new[] {
            (Setup: refund, Invalid: refund, Errors: new[] { 2601, 2627 }),
            (Setup: effect, Invalid: effect, Errors: new[] { 2601, 2627 }),
            (Setup: "", Invalid: refund.Replace(",100,'Completed'", ",0,'Completed'"), Errors: new[] { 547 }),
            (Setup: "", Invalid: refund.Replace(payment, "'00000000-0000-0000-0000-000000000299'"), Errors: new[] { 547 }),
            (Setup: "", Invalid: effect.Replace(payment, "'00000000-0000-0000-0000-000000000299'"), Errors: new[] { 547 }),
            (Setup: "", Invalid: refund.Replace("REPLICATE('r',160)", "REPLICATE('r',161)"), Errors: new[] { 2628, 8152 }),
            (Setup: refund, Invalid: "DELETE dbo.SubscriptionPayments WHERE Id=" + payment, Errors: new[] { 547 }),
            (Setup: effect, Invalid: "DELETE dbo.SubscriptionPayments WHERE Id=" + payment, Errors: new[] { 547 }) })
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellation);
            if (scenario.Setup.Length != 0) await db.Database.ExecuteSqlRawAsync(scenario.Setup, cancellation);
            var rejected = false;
            try { await db.Database.ExecuteSqlRawAsync(scenario.Invalid, cancellation); }
            catch (SqlException error) when (scenario.Errors.Contains(error.Number)) { rejected = true; }
            if (!rejected) throw new InvalidOperationException("Canonical payment constraint was not enforced");
            await transaction.RollbackAsync(cancellation);
        }
        await CandidateContract.Verify(db, CandidateContract.Current(db), db.Database.GetMigrations().ToArray(), cancellation);
    }

    private static Domain.Entities.MarketingConnection[] MarketingRows() => new[]
    {
        MarketingRow("00000000-0000-0000-0000-000000000101", "rehearsal-voice-a", "openai"),
        MarketingRow("00000000-0000-0000-0000-000000000102", "rehearsal-voice-b", "openai"),
        MarketingRow("00000000-0000-0000-0000-000000000103", "rehearsal-voice-a", "meta")
    };
    private static Domain.Entities.MarketingConnection MarketingRow(string id, string owner, string provider) => new()
    {
        Id = Guid.Parse(id), OwnerKey = owner, OwnerType = "founder", Provider = provider,
        PixelId = "synthetic-pixel", TestEventCode = "synthetic-event",
        AdsAccessTokenCiphertext = "synthetic-ads-ciphertext", CapiAccessTokenCiphertext = "synthetic-capi-ciphertext",
        AdAccountId = "synthetic-ad-account", AdAccountName = "Preserved account",
        ProviderReviewStatus = "synthetic-reviewed", ProviderAuthorizationMethod = "synthetic-authorized",
        ProviderPermissionsJson = "[\"synthetic-permission\"]", ProviderUserId = "synthetic-user",
        ConnectedUtc = new DateTime(2026, 1, 1), LastVerifiedUtc = new DateTime(2026, 1, 2),
        CreatedUtc = new DateTime(2026, 1, 1), UpdatedUtc = new DateTime(2026, 1, 2),
        AccessTokenExpiresUtc = new DateTime(2027, 1, 1),
        Revision = Guid.Parse("00000000-0000-0000-0000-000000000199")
    };
    private static JsonObject LegacyMarketing(Domain.Entities.MarketingConnection row)
    {
        var json = JsonSerializer.SerializeToNode(row)!.AsObject();
        foreach (var name in new[] { "ReceptionistActiveConfigurationId", "ReceptionistAdmissionEnabled",
            "ReceptionistControlRevision", "ReceptionistIngressKey", "ReceptionistProviderProjectKey" }) json.Remove(name);
        return json;
    }

    private static async Task VerifyCatalogBatchProtocol(MasterAppDbContext db, CancellationToken cancellation)
    {
        await db.Database.OpenConnectionAsync(cancellation);
        var planned = new CandidateContract.CatalogBatch();
        var observed = new List<int>();
        // Same parameter name in33commands proves command-local parameter scope,
        // ordering and the32/33 boundary against real SQL, on the current transaction.
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellation))
        {
            for (var value = 0; value < 33; value++)
            {
                var expected = value;
                planned.Add("SELECT @value", async reader =>
                {
                    if (reader.FieldCount != 1 || !await reader.ReadAsync(cancellation) ||
                        reader.GetInt32(0) != expected || await reader.ReadAsync(cancellation))
                        throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
                    observed.Add(expected);
                }, ("@value", value));
            }
            await planned.Execute(db, cancellation);
            if (!observed.SequenceEqual(Enumerable.Range(0, 33)))
                throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
            await transaction.RollbackAsync(cancellation);
        }
        foreach (var (sql, count) in new[] {
            ("SELECT 1", 2), ("SELECT 1; SELECT 2", 1),
            ("SELECT 1, 2", 1), ("SELECT 1 WHERE 1=0", 1),
            ("SELECT 1 UNION ALL SELECT 2", 1) })
        {
            await using var batch = new SqlBatch((SqlConnection)db.Database.GetDbConnection()) { Timeout = 30 };
            batch.BatchCommands.Add(new SqlBatchCommand(sql));
            await using var reader = await batch.ExecuteReaderAsync(cancellation);
            Func<System.Data.Common.DbDataReader, Task> validate = async result =>
            {
                if (result.FieldCount != 1 || !await result.ReadAsync(cancellation) ||
                    result.GetInt32(0) != 1 || await result.ReadAsync(cancellation))
                    throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
            };
            var rejected = false;
            try { await CandidateContract.CatalogBatch.VerifyResults(reader,
                Enumerable.Repeat(validate, count).ToArray(), cancellation); }
            catch (ProbeObservationFailure error) when (error.Message == "PHYSICAL_SCHEMA_DRIFT") { rejected = true; }
            if (!rejected) throw new InvalidOperationException("Malformed catalog result stream was accepted");
        }
    }

    private static string[] ReceptionistDrift(CandidateContract.Entry entry, bool hasManagement)
    {
        var amounts = entry.Tables.Single(t => t.Name == "ReceptionistBudgetPeriods").Checks
            .Single(c => c.Name == "CK_ReceptionistBudgetPeriod_Amounts").Sql;
        string Precision(string type) => """
            ALTER TABLE dbo.ReceptionistBudgetPeriods DROP CONSTRAINT CK_ReceptionistBudgetPeriod_Amounts;
            ALTER TABLE dbo.ReceptionistBudgetPeriods ALTER COLUMN CashCommitted
            """ + " " + type + " NOT NULL;\nALTER TABLE dbo.ReceptionistBudgetPeriods ADD CONSTRAINT CK_ReceptionistBudgetPeriod_Amounts CHECK (" + amounts + ");";
        return
        [
            Precision("decimal(19,6)"), Precision("decimal(20,5)"),
            """
            DROP INDEX IX_MarketingConnections_Provider_ReceptionistIngressKey ON dbo.MarketingConnections;
            -- Keep this schema-only adversary constructible with the persisted case-distinct synthetic rows.
            -- The enclosing transaction rolls back this fixture-only data adjustment with the schema drift.
            UPDATE dbo.MarketingConnections SET ReceptionistIngressKey=CONVERT(nvarchar(36),Id) WHERE ReceptionistIngressKey IS NOT NULL;
            ALTER TABLE dbo.MarketingConnections ALTER COLUMN ReceptionistIngressKey nvarchar(200) COLLATE Latin1_General_100_CI_AS NULL;
            CREATE UNIQUE INDEX IX_MarketingConnections_Provider_ReceptionistIngressKey ON dbo.MarketingConnections(Provider,ReceptionistIngressKey)
                WHERE ReceptionistIngressKey IS NOT NULL;
            """,
            "ALTER TABLE dbo.ReceptionistBudgetAccounts NOCHECK CONSTRAINT CK_ReceptionistBudgetAccount_Kind",
            """
            ALTER TABLE dbo.ReceptionistBudgetAccounts NOCHECK CONSTRAINT CK_ReceptionistBudgetAccount_Kind;
            ALTER TABLE dbo.ReceptionistBudgetAccounts CHECK CONSTRAINT CK_ReceptionistBudgetAccount_Kind;
            """,
            """
            ALTER TABLE dbo.ReceptionistBudgetAccounts DROP CONSTRAINT CK_ReceptionistBudgetAccount_Kind;
            ALTER TABLE dbo.ReceptionistBudgetAccounts ADD CONSTRAINT CK_ReceptionistBudgetAccount_Kind CHECK ([Kind] IN ('owner','funding','unknown'));
            """,
            """
            ALTER TABLE dbo.MarketingConnections DROP CONSTRAINT FK_MarketingConnections_ReceptionistConfigurationVersions_Id_ReceptionistActiveConfigurationId;
            ALTER TABLE dbo.MarketingConnections ADD CONSTRAINT FK_MarketingConnections_ReceptionistConfigurationVersions_Id_ReceptionistActiveConfigurationId
                FOREIGN KEY (ReceptionistActiveConfigurationId) REFERENCES dbo.ReceptionistConfigurationVersions(Id);
            """,
            "ALTER TABLE dbo.MarketingConnections NOCHECK CONSTRAINT FK_MarketingConnections_ReceptionistConfigurationVersions_Id_ReceptionistActiveConfigurationId",
            "ALTER INDEX IX_MarketingConnections_Provider_ReceptionistProviderProjectKey ON dbo.MarketingConnections DISABLE",
            """
            DROP INDEX IX_MarketingConnections_Provider_ReceptionistProviderProjectKey ON dbo.MarketingConnections;
            CREATE UNIQUE INDEX IX_MarketingConnections_Provider_ReceptionistProviderProjectKey
                ON dbo.MarketingConnections(Provider,ReceptionistProviderProjectKey)
                WHERE ReceptionistProviderProjectKey IS NOT NULL AND ReceptionistAdmissionEnabled=1;
            """,
            (hasManagement ? "ALTER TABLE dbo.ReceptionistOperationPackages DROP CONSTRAINT FK_ReceptionistOperationPackages_ReceptionistConfigurationVersions_MarketingConnectionId_ConfigurationVersionId; ALTER TABLE dbo.ReceptionistAcceptanceWindows DROP CONSTRAINT FK_ReceptionistAcceptanceWindows_ReceptionistConfigurationVersions_MarketingConnectionId_ConfigurationVersionId;" : "") +
            """
            ALTER TABLE dbo.MarketingConnections DROP CONSTRAINT FK_MarketingConnections_ReceptionistConfigurationVersions_Id_ReceptionistActiveConfigurationId;
            ALTER TABLE dbo.ReceptionistCalls DROP CONSTRAINT FK_ReceptionistCalls_ReceptionistConfigurationVersions_MarketingConnectionId_ConfigurationVersionId;
            ALTER TABLE dbo.ReceptionistConfigurationVersions DROP CONSTRAINT AK_ReceptionistConfigurationVersions_MarketingConnectionId_Id;
            CREATE UNIQUE INDEX AK_ReceptionistConfigurationVersions_MarketingConnectionId_Id
                ON dbo.ReceptionistConfigurationVersions(MarketingConnectionId,Id);
            ALTER TABLE dbo.MarketingConnections ADD CONSTRAINT FK_MarketingConnections_ReceptionistConfigurationVersions_Id_ReceptionistActiveConfigurationId
                FOREIGN KEY(Id,ReceptionistActiveConfigurationId) REFERENCES dbo.ReceptionistConfigurationVersions(MarketingConnectionId,Id);
            ALTER TABLE dbo.ReceptionistCalls ADD CONSTRAINT FK_ReceptionistCalls_ReceptionistConfigurationVersions_MarketingConnectionId_ConfigurationVersionId
                FOREIGN KEY(MarketingConnectionId,ConfigurationVersionId) REFERENCES dbo.ReceptionistConfigurationVersions(MarketingConnectionId,Id);
            """ + (hasManagement ? "ALTER TABLE dbo.ReceptionistOperationPackages ADD CONSTRAINT FK_ReceptionistOperationPackages_ReceptionistConfigurationVersions_MarketingConnectionId_ConfigurationVersionId FOREIGN KEY(MarketingConnectionId,ConfigurationVersionId) REFERENCES dbo.ReceptionistConfigurationVersions(MarketingConnectionId,Id); ALTER TABLE dbo.ReceptionistAcceptanceWindows ADD CONSTRAINT FK_ReceptionistAcceptanceWindows_ReceptionistConfigurationVersions_MarketingConnectionId_ConfigurationVersionId FOREIGN KEY(MarketingConnectionId,ConfigurationVersionId) REFERENCES dbo.ReceptionistConfigurationVersions(MarketingConnectionId,Id);" : "")
        ];
    }

    private static async Task VerifyReceptionistData(MasterAppDbContext db, CancellationToken cancellation, bool persist = false)
    {
        // Fixed synthetic values only. This runs after the actual EF migration,
        // inside a rollbacked transaction in the existing loopback-only fixture.
        // No feature CLR dependency is required when the observer is promoted first.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellation);
        await db.Database.ExecuteSqlRawAsync("""
            DECLARE @connectionA uniqueidentifier='00000000-0000-0000-0000-000000000101',
                    @connectionB uniqueidentifier='00000000-0000-0000-0000-000000000102',
                    @accountA uniqueidentifier='00000000-0000-0000-0000-000000000201',
                    @accountB uniqueidentifier='00000000-0000-0000-0000-000000000202',
                    @configA uniqueidentifier='00000000-0000-0000-0000-000000000301',
                    @configB uniqueidentifier='00000000-0000-0000-0000-000000000302',
                    @periodA uniqueidentifier='00000000-0000-0000-0000-000000000401',
                    @periodB uniqueidentifier='00000000-0000-0000-0000-000000000402',
                    @callA uniqueidentifier='00000000-0000-0000-0000-000000000501',
                    @callB uniqueidentifier='00000000-0000-0000-0000-000000000502';
            INSERT dbo.ReceptionistBudgetAccounts(Id,AdmissionEnabled,ControlRevision,Currency,Kind,Revision,ScopeKey,TimeZoneId)
                VALUES(@accountA,0,NEWID(),'USD','owner',NEWID(),'synthetic-owner-a','UTC'),
                      (@accountB,0,NEWID(),'USD','owner',NEWID(),'synthetic-owner-b','UTC');
            INSERT dbo.ReceptionistConfigurationVersions(Id,ConfigurationJson,ConfigurationSha256,CreatedByActorId,CreatedUtc,MarketingConnectionId,Version)
                VALUES(@configA,'{{}}',REPLICATE('a',64),'synthetic-actor','20260101',@connectionA,1),
                      (@configB,'{{}}',REPLICATE('b',64),'synthetic-actor','20260101',@connectionB,1);
            INSERT dbo.ReceptionistBudgetPeriods(Id,BudgetAccountId,CashCommitted,CashLimit,ConsumptionCommitted,ConsumptionLimit,EndUtc,MonthKey,
                OpeningCashCeiling,OpeningCashCommitted,OpeningConsumptionCeiling,OpeningConsumptionCommitted,OpeningReceipt,OpeningReconciled,
                OpeningVoiceSeconds,ReconciledThroughUtc,Revision,StartUtc,VoiceSecondsCommitted)
                SELECT PeriodId,AccountId,0.000001,12345678901234.123456,0,12345678901234.123456,'20260201',202601,
                       12345678901234.123456,0,12345678901234.123456,0,'synthetic-opening',1,0,'20260101',NEWID(),'20260101',0
                FROM (VALUES(@periodA,@accountA),(@periodB,@accountB)) AS p(PeriodId,AccountId);
            INSERT dbo.ReceptionistCalls(Id,ConfigurationVersionId,ConnectionControlRevision,ControlledTest,IngressKey,MarketingConnectionId,
                MustStopUtc,ObservedVoiceSeconds,OwnerBudgetAccountId,OwnerBudgetPeriodId,OwnerControlRevision,Provider,ProviderProjectKey,
                ProviderSessionId,ReservedUtc,ReservedVoiceSeconds,Revision,SidebandFence,SidebandObservationOnly,State,VoiceAllocationUncertain,VoiceUsageFinal)
                SELECT CallId,ConfigId,NEWID(),1,'synthetic-ingress',ConnectionId,'20260101',0,AccountId,PeriodId,NEWID(),'openai',
                       'synthetic-project',CONVERT(nvarchar(36),CallId),'20260101',360,NEWID(),0,0,'reserved',0,0
                FROM (VALUES(@callA,@configA,@connectionA,@accountA,@periodA),(@callB,@configB,@connectionB,@accountB,@periodB))
                    AS c(CallId,ConfigId,ConnectionId,AccountId,PeriodId);
            UPDATE dbo.MarketingConnections SET ReceptionistActiveConfigurationId=@configA,ReceptionistProviderProjectKey='ProjectCase',
                ReceptionistIngressKey='IngressCase' WHERE Id=@connectionA;
            UPDATE dbo.MarketingConnections SET ReceptionistActiveConfigurationId=@configB,ReceptionistProviderProjectKey='projectcase',
                ReceptionistIngressKey='ingresscase' WHERE Id=@connectionB;
            """, cancellation);
        await using (var amount = db.Database.GetDbConnection().CreateCommand())
        {
            amount.Transaction = transaction.GetDbTransaction();
            amount.CommandText = "SELECT CashLimit FROM dbo.ReceptionistBudgetPeriods WHERE Id='00000000-0000-0000-0000-000000000401'";
            amount.CommandTimeout = 30;
            if (Convert.ToDecimal(await amount.ExecuteScalarAsync(cancellation)) != 12345678901234.123456m)
                throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
        }
        foreach (var (sql, constraint) in new (string, string)[]
        {
            ("UPDATE dbo.MarketingConnections SET ReceptionistActiveConfigurationId='00000000-0000-0000-0000-000000000301' WHERE Id='00000000-0000-0000-0000-000000000102'",
                "FK_MarketingConnections_ReceptionistConfigurationVersions_Id_ReceptionistActiveConfigurationId"),
            ("UPDATE dbo.MarketingConnections SET ReceptionistProviderProjectKey='ProjectCase' WHERE Id='00000000-0000-0000-0000-000000000102'",
                "IX_MarketingConnections_Provider_ReceptionistProviderProjectKey"),
            ("UPDATE dbo.MarketingConnections SET ReceptionistIngressKey='IngressCase' WHERE Id='00000000-0000-0000-0000-000000000102'",
                "IX_MarketingConnections_Provider_ReceptionistIngressKey"),
            ("UPDATE dbo.ReceptionistBudgetPeriods SET CashCommitted=-1 WHERE Id='00000000-0000-0000-0000-000000000401'",
                "CK_ReceptionistBudgetPeriod_Amounts"),
            ("UPDATE dbo.ReceptionistBudgetPeriods SET MonthKey=202613 WHERE Id='00000000-0000-0000-0000-000000000401'",
                "CK_ReceptionistBudgetPeriod_Month"),
            ("UPDATE dbo.ReceptionistCalls SET OwnerBudgetPeriodId='00000000-0000-0000-0000-000000000402' WHERE Id='00000000-0000-0000-0000-000000000501'",
                "FK_ReceptionistCalls_ReceptionistBudgetPeriods_OwnerBudgetAccountId_OwnerBudgetPeriodId"),
            ("UPDATE dbo.ReceptionistCalls SET ReservedVoiceSeconds=0 WHERE Id='00000000-0000-0000-0000-000000000501'",
                "CK_ReceptionistCall_Duration"),
            ("UPDATE dbo.ReceptionistCalls SET State='closed' WHERE Id='00000000-0000-0000-0000-000000000501'",
                "CK_ReceptionistCall_State"),
            ("UPDATE dbo.ReceptionistCalls SET SidebandFence=1 WHERE Id='00000000-0000-0000-0000-000000000501'",
                "CK_ReceptionistCall_WorkOwnership")
        })
        {
            await transaction.CreateSavepointAsync("constraint_probe", cancellation);
            var rejected = false;
            try { await db.Database.ExecuteSqlRawAsync(sql, cancellation); }
            catch (SqlException error) when (error.Number is 547 or 2601 or 2627 && error.Message.Contains(constraint, StringComparison.Ordinal))
            { rejected = true; }
            finally { await transaction.RollbackToSavepointAsync("constraint_probe", cancellation); }
            if (!rejected) throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
        }
        if (persist) await transaction.CommitAsync(cancellation);
        else await transaction.RollbackAsync(cancellation);
    }
    private static string ManagementSnapshotPath(string database) => Path.Combine(Path.GetTempPath(), database + ".receptionist-preservation.sha256");
    private static async Task<string> ManagementPredecessorDigest(MasterAppDbContext db, CancellationToken ct)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandTimeout = 30;
        var calls = CandidateContract.Current(db).Migrations.Single(m => m.Id == ReceptionistMigration)
            .Tables.Single(t => t.Name == "ReceptionistCalls").Columns;
        using var quote = new SqlCommandBuilder();
        var callColumns = string.Join(",", calls.Select(c => quote.QuoteIdentifier(c.Name)));
        command.CommandText = $"""
            SELECT
            (SELECT * FROM dbo.MarketingConnections WHERE Id IN ('00000000-0000-0000-0000-000000000101','00000000-0000-0000-0000-000000000102','00000000-0000-0000-0000-000000000103') ORDER BY Id FOR JSON PATH, INCLUDE_NULL_VALUES) AS connections,
            (SELECT * FROM dbo.ReceptionistConfigurationVersions ORDER BY Id FOR JSON PATH, INCLUDE_NULL_VALUES) AS versions,
            (SELECT * FROM dbo.ReceptionistBudgetAccounts ORDER BY Id FOR JSON PATH, INCLUDE_NULL_VALUES) AS accounts,
            (SELECT * FROM dbo.ReceptionistBudgetPeriods ORDER BY Id FOR JSON PATH, INCLUDE_NULL_VALUES) AS periods,
            (SELECT {callColumns} FROM dbo.ReceptionistCalls ORDER BY Id FOR JSON PATH, INCLUDE_NULL_VALUES) AS calls
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
            """;
        var json = Convert.ToString(await command.ExecuteScalarAsync(ct)) ?? throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
        return Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json)));
    }
    private static async Task SaveManagementPredecessor(MasterAppDbContext db, string database, CancellationToken ct)
    {
        await db.Database.ExecuteSqlRawAsync("UPDATE dbo.ReceptionistConfigurationVersions SET AcceptedBindingJson='{{\"syntheticAccepted\":true}}'", ct);
        await File.WriteAllTextAsync(ManagementSnapshotPath(database), await ManagementPredecessorDigest(db, ct), ct);
    }
    private static async Task VerifyManagementPredecessor(MasterAppDbContext db, string database, CancellationToken ct)
    {
        if (!File.Exists(ManagementSnapshotPath(database)) ||
            await File.ReadAllTextAsync(ManagementSnapshotPath(database), ct) != await ManagementPredecessorDigest(db, ct))
            throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
    }
    private static IEnumerable<string> ManagementDrift(CandidateContract.Entry entry)
    {
        foreach (var table in entry.Tables)
        {
            foreach (var key in table.ForeignKeys)
                yield return $"ALTER TABLE dbo.[{table.Name}] NOCHECK CONSTRAINT [{key.Name}]";
            foreach (var check in table.Checks)
                yield return $"ALTER TABLE dbo.[{table.Name}] NOCHECK CONSTRAINT [{check.Name}]";
        }
        foreach (var index in entry.Indexes)
            yield return $"DROP INDEX [{index.Name}] ON dbo.[{index.Table}]";
        yield return "ALTER TABLE dbo.ReceptionistCredentialVersions ALTER COLUMN Provider nvarchar(50) COLLATE Latin1_General_100_CI_AS NOT NULL";
    }
    private static async Task VerifyManagementData(MasterAppDbContext db, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("""
            INSERT dbo.ReceptionistCredentialVersions(Id,MarketingConnectionId,Provider,ProviderProjectKey,Purpose,ProtectedValue,ValueSha256,CreatedByActorId,CreatedUtc,Revision)
            VALUES('00000000-0000-0000-0000-000000000701','00000000-0000-0000-0000-000000000101','openai','synthetic-a','voice-api','synthetic-ciphertext',REPLICATE('a',64),'synthetic-actor','20260101',NEWID()),
                  ('00000000-0000-0000-0000-000000000702','00000000-0000-0000-0000-000000000102','openai','synthetic-b','voice-api','synthetic-ciphertext',REPLICATE('b',64),'synthetic-actor','20260101',NEWID());
            INSERT dbo.ReceptionistOperationPackages(Id,MarketingConnectionId,ConfigurationVersionId,Operation,PayloadJson,PayloadSha256,ApiCredentialVersionId,CreatedByActorId,CreatedUtc,ExpiresUtc,ReviewState,Revision)
            VALUES('00000000-0000-0000-0000-000000000601','00000000-0000-0000-0000-000000000101','00000000-0000-0000-0000-000000000301','accept_binding','{{}}',REPLICATE('a',64),'00000000-0000-0000-0000-000000000701','synthetic-actor','20260101','20260102','pending',NEWID());
            INSERT dbo.ReceptionistAcceptanceWindows(Id,MarketingConnectionId,ConfigurationVersionId,ConfigurationSha256,IngressKey,ProviderProjectKey,RestrictedRouteEvidenceSha256,CreatedByActorId,ReviewedByActorId,StartsUtc,ExpiresUtc,Revision)
            VALUES('00000000-0000-0000-0000-000000000801','00000000-0000-0000-0000-000000000101','00000000-0000-0000-0000-000000000301',REPLICATE('a',64),'synthetic-route','synthetic-a',REPLICATE('b',64),'synthetic-actor','synthetic-actor','20260101','2026-01-01T00:30:00',NEWID());
            """, ct);
        // Execute the shipped EF predicate on real SQL Server, including its padded string-comparison semantics.
        var windowPredicate = typeof(Infrastructure.Reception.ReceptionistAdmissionService).GetMethod("ValidCallWindows",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?? throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
        var validWindows = (System.Linq.IQueryable)windowPredicate.Invoke(null,
            [db, new DateTime(2026, 1, 1, 0, 10, 0, DateTimeKind.Utc)])!;
        foreach (var (hash, expected) in new[] {
            (new string('b', 64), true), (new string('a', 31) + " " + new string('a', 32), false),
            (new string('A', 64), false), (new string('z', 64), false), (new string('a', 63) + " ", false) })
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE dbo.ReceptionistAcceptanceWindows SET RestrictedRouteEvidenceSha256={hash} WHERE Id='00000000-0000-0000-0000-000000000801'", ct);
            // Resolve the candidate-only entity through the returned query. The trusted
            // tooling must also compile against the pre-management release runtime.
            var parameter = System.Linq.Expressions.Expression.Parameter(validWindows.ElementType, "window");
            var predicate = System.Linq.Expressions.Expression.Lambda(
                System.Linq.Expressions.Expression.Equal(System.Linq.Expressions.Expression.Property(parameter, "Id"),
                    System.Linq.Expressions.Expression.Constant(Guid.Parse("00000000-0000-0000-0000-000000000801"))), parameter);
            var filtered = System.Linq.Expressions.Expression.Call(typeof(Queryable), nameof(Queryable.Where),
                [validWindows.ElementType], validWindows.Expression, System.Linq.Expressions.Expression.Quote(predicate));
            var any = System.Linq.Expressions.Expression.Call(typeof(Queryable), nameof(Queryable.Any),
                [validWindows.ElementType], filtered);
            if (validWindows.Provider is not Microsoft.EntityFrameworkCore.Query.IAsyncQueryProvider provider ||
                await provider.ExecuteAsync<Task<bool>>(any, ct) != expected)
                throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
        }
        await db.Database.ExecuteSqlRawAsync("UPDATE dbo.ReceptionistAcceptanceWindows SET RestrictedRouteEvidenceSha256=REPLICATE('b',64) WHERE Id='00000000-0000-0000-0000-000000000801'", ct);
        // A valid reviewed row must succeed before probing individual protections.
        await db.Database.ExecuteSqlRawAsync("UPDATE dbo.ReceptionistOperationPackages SET ReviewState='accepted', ReviewedByActorId='synthetic-actor', ReviewedUtc='20260101', ReviewedPayloadSha256=PayloadSha256", ct);
        foreach (var (sql, constraint) in new (string, string)[] {
            ("UPDATE dbo.ReceptionistOperationPackages SET ConfigurationVersionId='00000000-0000-0000-0000-000000000302'", "FK_ReceptionistOperationPackages_ReceptionistConfigurationVersions_MarketingConnectionId_ConfigurationVersionId"),
            ("UPDATE dbo.ReceptionistOperationPackages SET ApiCredentialVersionId='00000000-0000-0000-0000-000000000702'", "FK_ReceptionistOperationPackages_ReceptionistCredentialVersions_MarketingConnectionId_ApiCredentialVersionId"),
            ("UPDATE dbo.ReceptionistOperationPackages SET WebhookCredentialVersionId='00000000-0000-0000-0000-000000000702'", "FK_ReceptionistOperationPackages_ReceptionistCredentialVersions_MarketingConnectionId_WebhookCredentialVersionId"),
            ("UPDATE dbo.ReceptionistOperationPackages SET ReviewedByActorId=NULL", "CK_ReceptionistOperationPackage_Review"),
            ("UPDATE dbo.ReceptionistOperationPackages SET ReviewedPayloadSha256=NULL", "CK_ReceptionistOperationPackage_Review"),
            ("UPDATE dbo.ReceptionistOperationPackages SET ReviewState='unknown'", "CK_ReceptionistOperationPackage_Review"),
            ("UPDATE dbo.ReceptionistOperationPackages SET ExpiresUtc=CreatedUtc", "CK_ReceptionistOperationPackage_Lifetime"),
            ("UPDATE dbo.ReceptionistAcceptanceWindows SET ConfigurationVersionId='00000000-0000-0000-0000-000000000302'", "FK_ReceptionistAcceptanceWindows_ReceptionistConfigurationVersions_MarketingConnectionId_ConfigurationVersionId"),
            ("UPDATE dbo.ReceptionistAcceptanceWindows SET ExpiresUtc=StartsUtc", "CK_ReceptionistAcceptanceWindow_Lifetime"),
            ("DELETE dbo.ReceptionistCredentialVersions WHERE Id='00000000-0000-0000-0000-000000000701'", "FK_ReceptionistOperationPackages_ReceptionistCredentialVersions_MarketingConnectionId_ApiCredentialVersionId")
        })
        {
            await transaction.CreateSavepointAsync("management_probe", ct);
            var rejected = false;
            try { await db.Database.ExecuteSqlRawAsync(sql, ct); }
            catch (SqlException error) when (error.Number == 547 && error.Message.Contains(constraint, StringComparison.Ordinal)) { rejected = true; }
            finally { await transaction.RollbackToSavepointAsync("management_probe", ct); }
            if (!rejected) throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
        }
        await transaction.RollbackAsync(ct);
    }

    private static readonly Guid TrainingBusiness = Guid.Parse("cfaf0000-0000-4000-8000-000000000001");
    private static readonly Guid TrainingProduct = Guid.Parse("cfaf0000-0000-4000-8000-000000000002");
    private static readonly Guid TrainingClient = Guid.Parse("cfaf0000-0000-4000-8000-000000000003");
    private static readonly Guid TrainingOffer = Guid.Parse("cfaf0000-0000-4000-8000-000000000004");
    private static object[] TrainingLegacyRows() => [
        new Domain.Entities.CommerceBusiness { Id = TrainingBusiness, Key = "rehearsal-training", DisplayName = "Legacy business" },
        new Domain.Entities.CommerceProduct { Id = TrainingProduct, CommerceBusinessId = TrainingBusiness,
            ExternalProductKey = "legacy-training-fixture", Name = "Legacy product", Slug = "legacy-training-fixture", Description = "Legacy description", PriceCents = 4321, IsActive = false },
        new Domain.Entities.ClientProfile { Id = TrainingClient, ClientUserId = "lead:training-rehearsal", Email = "training-rehearsal@example.invalid", NormalizedEmail = "training-rehearsal@example.invalid" },
        new Domain.Entities.ClientSubscriptionOffer { Id = TrainingOffer, ClientProfileId = TrainingClient, OwnerAgentUserId = "synthetic-training-owner", MonthlyAmountCents = 10000 },
        new Domain.Entities.ClientSubscription { ClientProfileId = TrainingClient, AcceptedOfferId = TrainingOffer,
            MonthlyAmountCents = 10000, Status = Domain.Billing.ClientSubscriptionStatus.Active },
        new Domain.Entities.ClientSubscription { ClientProfileId = TrainingClient, AcceptedOfferId = TrainingOffer,
            MonthlyAmountCents = 9000, Status = Domain.Billing.ClientSubscriptionStatus.Canceled },
        new Domain.Entities.ClientSubscription { ClientProfileId = TrainingClient, AcceptedOfferId = TrainingOffer,
            MonthlyAmountCents = 8000, Status = Domain.Billing.ClientSubscriptionStatus.ActivationFailed }
    ];
    private static async Task VerifyTrainingData(MasterAppDbContext db, CancellationToken ct)
    {
        var product = await db.CommerceProducts.AsNoTracking().SingleAsync(x => x.Id == TrainingProduct, ct);
        var offer = await db.ClientSubscriptionOffers.AsNoTracking().SingleAsync(x => x.Id == TrainingOffer, ct);
        var subscription = await db.ClientSubscriptions.AsNoTracking().SingleAsync(x => x.ClientProfileId == TrainingClient && x.Status == Domain.Billing.ClientSubscriptionStatus.Active, ct);
        if (product.Name != "Legacy product" || product.Description != "Legacy description" || product.PriceCents != 4321 || product.IsActive ||
            product.BillingKind != "OneTime" || offer.MonthlyAmountCents != 10000 || offer.OwnerAgentUserId != "synthetic-training-owner" ||
            offer.SubscriptionScope != "LegendMembership" || offer.CommerceBusinessId.HasValue || offer.CommerceProductId.HasValue ||
            offer.TrainingBenefits is not null || offer.TrainingPlanName is not null || offer.TrainingPromotionCode is not null ||
            subscription.MonthlyAmountCents != 10000 || subscription.AcceptedOfferId != TrainingOffer ||
            subscription.Status != Domain.Billing.ClientSubscriptionStatus.Active || subscription.SubscriptionScope != "LegendMembership" ||
            subscription.CommerceBusinessId.HasValue || subscription.CommerceProductId.HasValue)
            throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
        var historical = await db.ClientSubscriptions.AsNoTracking().Where(x => x.ClientProfileId == TrainingClient &&
            x.Status != Domain.Billing.ClientSubscriptionStatus.Active).OrderBy(x => x.MonthlyAmountCents).ToArrayAsync(ct);
        if (historical.Length != 2 || historical[0].MonthlyAmountCents != 8000 || historical[1].MonthlyAmountCents != 9000 ||
            historical[0].Status != Domain.Billing.ClientSubscriptionStatus.ActivationFailed || historical[1].Status != Domain.Billing.ClientSubscriptionStatus.Canceled ||
            historical.Any(x => x.SubscriptionScope != "LegendMembership" || x.CommerceBusinessId.HasValue || x.CommerceProductId.HasValue || x.AcceptedOfferId != TrainingOffer))
            throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        Domain.Entities.ClientSubscription Row(Domain.Billing.ClientSubscriptionStatus status) => new() {
            ClientProfileId = TrainingClient, AcceptedOfferId = TrainingOffer, CommerceBusinessId = TrainingBusiness,
            CommerceProductId = TrainingProduct, SubscriptionScope = Domain.Billing.ClientSubscriptionScope.Training(TrainingBusiness),
            MonthlyAmountCents = 35000, Status = status };
        db.ClientSubscriptions.AddRange(Row(Domain.Billing.ClientSubscriptionStatus.Active),
            Row(Domain.Billing.ClientSubscriptionStatus.Canceled), Row(Domain.Billing.ClientSubscriptionStatus.ActivationFailed));
        await db.SaveChangesAsync(ct);
        db.ClientSubscriptions.Add(Row(Domain.Billing.ClientSubscriptionStatus.Active));
        try { await db.SaveChangesAsync(ct); throw new InvalidOperationException("Duplicate nonterminal training scope accepted"); }
        catch (DbUpdateException error) when (error.InnerException is SqlException sql && sql.Number is 2601 or 2627) { }
        await transaction.RollbackAsync(ct);
        db.ChangeTracker.Clear();
        await using var membershipTransaction = await db.Database.BeginTransactionAsync(ct);
        db.ClientSubscriptions.Add(new Domain.Entities.ClientSubscription { ClientProfileId = TrainingClient, AcceptedOfferId = TrainingOffer,
            MonthlyAmountCents = 10000, Status = Domain.Billing.ClientSubscriptionStatus.Active });
        try { await db.SaveChangesAsync(ct); throw new InvalidOperationException("Duplicate nonterminal membership accepted"); }
        catch (DbUpdateException error) when (error.InnerException is SqlException sql && sql.Number is 2601 or 2627) { }
        await membershipTransaction.RollbackAsync(ct);
        db.ChangeTracker.Clear();
    }

}
