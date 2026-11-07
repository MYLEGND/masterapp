using Domain.Entities;
using Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;

internal static partial class RehearsalFixture
{
    private static readonly Guid StoreBusiness = Guid.Parse("a1000000-0000-0000-0000-000000000001");
    private static readonly Guid StoreClient = Guid.Parse("a1000000-0000-0000-0000-000000000002");
    private static readonly Guid StoreContinuation = Guid.Parse("a1000000-0000-0000-0000-000000000003");
    private static readonly (string Role, bool Orders, bool Grant)[] StoreGrants = [
        ("owner", false, true), ("OWNER", false, true), ("platform-owner", false, true),
        ("account", false, true), ("owner ", false, false), ("owner\t", false, false),
        ("staff", true, true), ("staff", false, false), ("accóunt", false, false)];
    private static readonly (string Draft, string? Snapshot, bool Retired, bool Bound, bool Expected)[] StoreEvidence = [
        ("{\"store\":{\"enabled\":true}}", null, false, true, true),
        ("{}", "{\"store\":{\"enabled\":true}}", false, true, true),
        ("{}", "{\"store\":{\"enabled\":true}}", true, true, false),
        ("{\"store\":{\"enabled\":true}}", null, false, false, false),
        ("{\"store\":{\"enabled\":\"true\"}}", null, false, true, false),
        ("{\"Store\":{\"enabled\":true}}", null, false, true, false),
        ("{\"store\":{\"Enabled\":true}}", null, false, true, false),
        ("{\"store\":{\"enabled\":true},\"Store\":{\"enabled\":false}}", null, false, true, false),
        ("{\"store\":{\"enabled\":true,\"enabled\":false}}", null, false, true, false),
        ("not json", null, false, true, false), ("{}", null, false, true, false)];
    private static void SeedStoreData(MasterAppDbContext db, bool retentionPending)
    {
        db.Add(new CommerceBusiness { Id = StoreBusiness, Key = "store-contract-fixture", DisplayName = "Synthetic Store" });
        db.Add(new ClientProfile { Id = StoreClient, ClientUserId = "lead:store-contract", Email = "store@example.invalid", NormalizedEmail = "store@example.invalid" });
        db.Add(new ClientIdentityContinuation { Id = StoreContinuation, ClientProfileId = StoreClient,
            Purpose = Domain.Billing.ClientIdentityContinuationPurpose.SignIn, TokenHash = new string('d', 64),
            IntendedNormalizedEmail = "store@example.invalid", ReturnUrl = "/retained", ExpiresUtc = new DateTime(2027, 1, 1),
            CreatedUtc = new DateTime(2026, 1, 1) });
        for (var i = 0; i < StoreGrants.Length; i++)
        {
            var row = StoreGrants[i];
            db.Add(new CommerceBusinessMember { CommerceBusinessId = StoreBusiness, RoleKey = row.Role,
                Email = $"store-{i}@example.invalid", NormalizedEmail = $"store-{i}@example.invalid", DisplayName = "retained",
                CanManageOrders = row.Orders, CanManageCrm = row.Grant, Status = i % 2 == 0 ? "Active" : "Inactive" });
        }
        for (var i = 0; i < StoreEvidence.Length; i++)
        {
            var row = StoreEvidence[i];
            var state = new WebsiteContentState { OwnerKey = "store-contract-" + i, SiteKey = "legend", DraftJson = row.Draft,
                CommerceBusinessId = row.Bound ? StoreBusiness : null,
                ProvisionedStoreBusinessId = row.Expected ? StoreBusiness : null };
            db.Add(state);
            if (row.Snapshot is not null && !(row.Retired && retentionPending)) db.Add(new WebsiteContentVersion { StateId = state.Id, Revision = 1,
                ActorUserId = "store-contract", DocumentJson = row.Snapshot, SnapshotRetiredUtc = row.Retired ? new DateTime(2026, 1, 1) : null });
        }
    }
    private static async Task PrepareStoreBaseline(MasterAppDbContext db, CandidateContract.Entry[] pending, CancellationToken ct)
    {
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE dbo.ClientIdentityContinuations DROP CONSTRAINT CK_ClientIdentityContinuations_PurposeBinding", ct);
        if (!pending.Any(m => m.Id == CandidateContract.StoreContinuationMigration))
            await db.Database.ExecuteSqlRawAsync(CandidateContract.StoreConstraintSql(false), ct);
    }
    private static async Task FinishStoreBaseline(MasterAppDbContext db, CandidateContract.Entry[] pending, CancellationToken ct)
    {
        if (!pending.Any(m => m.Id == CandidateContract.StoreCrmMigration))
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE dbo.CommerceBusinessMembers ADD CONSTRAINT DF_Rehearsal_CanManageCrm DEFAULT (0) FOR CanManageCrm", ct);
        if (pending.Any(m => m.Id == CandidateContract.StoreContinuationMigration))
        {
            await db.Database.ExecuteSqlRawAsync("""
                ALTER TABLE dbo.ClientIdentityContinuations DROP CONSTRAINT FK_ClientIdentityContinuations_ClientProfiles_ClientProfileId;
                DROP INDEX IX_ClientIdentityContinuations_ClientProfileId_ExpiresUtc ON dbo.ClientIdentityContinuations;
                DROP INDEX IX_ClientIdentityContinuations_ClientProfileId_ConsumedUtc ON dbo.ClientIdentityContinuations;
                ALTER TABLE dbo.ClientIdentityContinuations ALTER COLUMN ClientProfileId uniqueidentifier NOT NULL;
                CREATE INDEX IX_ClientIdentityContinuations_ClientProfileId_ExpiresUtc ON dbo.ClientIdentityContinuations(ClientProfileId,ExpiresUtc);
                CREATE INDEX IX_ClientIdentityContinuations_ClientProfileId_ConsumedUtc ON dbo.ClientIdentityContinuations(ClientProfileId,ConsumedUtc);
                ALTER TABLE dbo.ClientIdentityContinuations WITH CHECK ADD CONSTRAINT FK_ClientIdentityContinuations_ClientProfiles_ClientProfileId
                    FOREIGN KEY(ClientProfileId) REFERENCES dbo.ClientProfiles(Id) ON DELETE CASCADE;
                """, ct);
        }
    }
    private static async Task StorePreconditionDrift(MasterAppDbContext db, CandidateContract.Contract contract,
        string[] applied, CandidateContract.Entry[] pending, CancellationToken ct)
    {
        var column = pending.First(m => CandidateContract.IsStoreMigration(m.Id)).Columns[0];
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            // These names/types come from the exact tool-owned descriptor validated above.
            using var quote = new SqlCommandBuilder();
            var partial = "ALTER TABLE dbo." + quote.QuoteIdentifier(column.Table) + " ADD " +
                quote.QuoteIdentifier(column.Name) + " " + column.Type + " NULL";
            await db.Database.ExecuteSqlRawAsync(partial, ct);
            try { await VerifyFixtureContract(db, contract, applied, ct); throw new InvalidOperationException("Partial Store schema accepted"); }
            catch (ProbeObservationFailure e) when (e.Message == "PHYSICAL_SCHEMA_DRIFT") { }
            await transaction.RollbackAsync(ct);
        }
        var baseline = await StoreBaselineSnapshot(db, ct);
        var baselineHistory = (await db.Database.GetAppliedMigrationsAsync(ct)).ToArray();
        // Execute the real pending migration commands inside a transaction, fail
        // after them, then prove history/schema and retained rows were rolled back.
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            var assembly = db.GetService<IMigrationsAssembly>();
            foreach (var entry in pending)
            {
                var migration = assembly.CreateMigration(assembly.Migrations[entry.Id], db.Database.ProviderName!);
                var model = db.GetService<IModelRuntimeInitializer>().Initialize(migration.TargetModel, designTime: true);
                foreach (var command in db.GetService<IMigrationsSqlGenerator>().Generate(migration.UpOperations, model))
                    await ExecuteStoreSql(db, command.CommandText, ct);
                await db.Database.ExecuteSqlRawAsync(db.GetService<IHistoryRepository>().GetInsertScript(new HistoryRow(entry.Id, "10.0.0")), ct);
            }
            var failed = false;
            try { await db.Database.ExecuteSqlRawAsync("THROW 51000, 'Synthetic transaction failure', 1;", ct); }
            catch (SqlException e) when (e.Number == 51000) { failed = true; }
            await transaction.RollbackAsync(ct);
            if (!failed) throw new InvalidOperationException("Failure injection did not fail");
        }
        if (!(await db.Database.GetAppliedMigrationsAsync(ct)).SequenceEqual(baselineHistory) ||
            await StoreBaselineSnapshot(db, ct) != baseline)
            throw new InvalidOperationException("Store transaction retention failed");
        await VerifyFixtureContract(db, contract, baselineHistory, ct);
    }
    private static async Task ExecuteStoreSql(MasterAppDbContext db, string sql, CancellationToken ct)
    {
        // Execute generated SQL literally: JSON braces are SQL data, not EF format placeholders.
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = sql;
        command.CommandTimeout = 60;
        await command.ExecuteNonQueryAsync(ct);
    }
    private static async Task<string> StoreBaselineSnapshot(MasterAppDbContext db, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandTimeout = 30;
        command.CommandText = """
            SELECT (SELECT
                (SELECT * FROM dbo.ClientIdentityContinuations ORDER BY Id FOR JSON PATH, INCLUDE_NULL_VALUES) AS continuations,
                (SELECT * FROM dbo.CommerceBusinessMembers ORDER BY Id FOR JSON PATH, INCLUDE_NULL_VALUES) AS members,
                (SELECT * FROM dbo.WebsiteContentState ORDER BY Id FOR JSON PATH, INCLUDE_NULL_VALUES) AS states,
                (SELECT * FROM dbo.WebsiteContentVersion ORDER BY Id FOR JSON PATH, INCLUDE_NULL_VALUES) AS versions
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER)
            """;
        return Convert.ToString(await command.ExecuteScalarAsync(ct)) ?? throw new InvalidOperationException();
    }
    private static IEnumerable<string> StoreDrift() => [
        "ALTER TABLE dbo.ClientIdentityContinuations NOCHECK CONSTRAINT CK_ClientIdentityContinuations_PurposeBinding",
        "ALTER TABLE dbo.ClientIdentityContinuations DROP CONSTRAINT CK_ClientIdentityContinuations_PurposeBinding; ALTER TABLE dbo.ClientIdentityContinuations ADD CONSTRAINT CK_ClientIdentityContinuations_PurposeBinding CHECK (ClientProfileId IS NOT NULL)",
        "ALTER TABLE dbo.ClientIdentityContinuations DROP CONSTRAINT CK_ClientIdentityContinuations_PurposeBinding; ALTER TABLE dbo.ClientIdentityContinuations DROP CONSTRAINT FK_ClientIdentityContinuations_ClientProfiles_ClientProfileId; DROP INDEX IX_ClientIdentityContinuations_ClientProfileId_ExpiresUtc ON dbo.ClientIdentityContinuations; DROP INDEX IX_ClientIdentityContinuations_ClientProfileId_ConsumedUtc ON dbo.ClientIdentityContinuations; ALTER TABLE dbo.ClientIdentityContinuations ALTER COLUMN ClientProfileId uniqueidentifier NOT NULL",
        "ALTER TABLE dbo.ClientIdentityContinuations ALTER COLUMN WebsiteSiteKey nvarchar(63) NULL",
        "ALTER TABLE dbo.WebsiteContentState ADD DEFAULT ('00000000-0000-0000-0000-000000000001') FOR ProvisionedStoreBusinessId"
    ];
    private static async Task VerifyStoreData(MasterAppDbContext db, CandidateContract.Entry[] pending, CancellationToken ct)
    {
        var continuation = await db.Set<ClientIdentityContinuation>().AsNoTracking().SingleAsync(c => c.Id == StoreContinuation, ct);
        if (continuation.ClientProfileId != StoreClient || continuation.ReturnUrl != "/retained" || continuation.TokenHash != new string('d', 64) ||
            continuation.Purpose != Domain.Billing.ClientIdentityContinuationPurpose.SignIn || continuation.CommerceBusinessMemberId is not null)
            throw new InvalidOperationException("Store continuation retention failed");
        for (var i = 0; i < StoreGrants.Length; i++)
        {
            var email = $"store-{i}@example.invalid";
            var row = await db.Set<CommerceBusinessMember>().AsNoTracking().SingleAsync(m => m.NormalizedEmail == email, ct);
            var expected = StoreGrants[i];
            if (row.CanManageCrm != expected.Grant || row.RoleKey != expected.Role || row.CanManageOrders != expected.Orders ||
                row.Status != (i % 2 == 0 ? "Active" : "Inactive") || row.DisplayName != "retained")
                throw new InvalidOperationException("Store grant retention failed");
        }
        for (var i = 0; i < StoreEvidence.Length; i++)
        {
            var key = "store-contract-" + i;
            var row = await db.Set<WebsiteContentState>().AsNoTracking().SingleAsync(s => s.OwnerKey == key, ct);
            if (row.ProvisionedStoreBusinessId != (StoreEvidence[i].Expected ? StoreBusiness : null) || row.DraftJson != StoreEvidence[i].Draft)
                throw new InvalidOperationException("Store provisioning evidence failed");
        }
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        foreach (var mutation in new[] {
            "UPDATE dbo.ClientIdentityContinuations SET ClientProfileId=NULL WHERE Id='a1000000-0000-0000-0000-000000000003'",
            "UPDATE dbo.ClientIdentityContinuations SET Purpose='StoreManagement' WHERE Id='a1000000-0000-0000-0000-000000000003'",
            "UPDATE dbo.ClientIdentityContinuations SET Purpose='BusinessMembership' WHERE Id='a1000000-0000-0000-0000-000000000003'" })
        {
            await transaction.CreateSavepointAsync("store_guard", ct);
            var rejected = false;
            try { await db.Database.ExecuteSqlRawAsync(mutation, ct); }
            catch (SqlException e) when (e.Number == 547) { rejected = true; }
            await transaction.RollbackToSavepointAsync("store_guard", ct);
            if (!rejected) throw new InvalidOperationException("Invalid Store identity accepted");
        }
        await db.Database.ExecuteSqlRawAsync("""
            UPDATE dbo.ClientIdentityContinuations SET Purpose='StoreManagement', ClientProfileId=NULL,
                CommerceBusinessId='a1000000-0000-0000-0000-000000000001', ActorUserId='synthetic-actor',
                DestinationOrigin='https://store.example.invalid', WebsiteSiteKey='legend', WebsiteOwnerKey='synthetic-owner', BrowserCorrelationHash='synthetic-browser'
                WHERE Id='a1000000-0000-0000-0000-000000000003';
            UPDATE dbo.ClientIdentityContinuations SET Purpose='BusinessMembership', ClientProfileId='a1000000-0000-0000-0000-000000000002',
                CommerceBusinessMemberId=(SELECT TOP (1) Id FROM dbo.CommerceBusinessMembers WHERE CommerceBusinessId='a1000000-0000-0000-0000-000000000001' ORDER BY Id)
                WHERE Id='a1000000-0000-0000-0000-000000000003';
            """, ct);
        // Legitimate post-migration edits are not historical backfill drift.
        await db.Database.ExecuteSqlRawAsync("UPDATE dbo.CommerceBusinessMembers SET CanManageCrm=1-CAST(CanManageCrm AS int) WHERE CommerceBusinessId='a1000000-0000-0000-0000-000000000001'", ct);
        await VerifyFixtureContract(db, CandidateContract.Current(db), db.Database.GetMigrations().ToArray(), ct);
        await transaction.RollbackAsync(ct);
    }
}
