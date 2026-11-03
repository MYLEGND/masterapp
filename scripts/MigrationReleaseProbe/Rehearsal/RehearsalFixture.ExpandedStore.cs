using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using System.Text.Json;

internal static partial class RehearsalFixture
{
    // Disposable SQL fixture coverage only. These entries grant NO production
    // append authority; the owner AlterColumn remains unsupported there.
    private const string OwnerMigration = "20261103081639_ScopedMerchantWebsiteOwners";
    private const string AutomationMigration = "20261103170724_SharedCommerceAutomationState";
    private const string ShippingMigration = "20261103174446_SharedCommerceShippingState";
    private static readonly string[] ExpandedMigrations = [OwnerMigration, AutomationMigration, ShippingMigration];
    private static readonly Guid ExpandedBusiness = Guid.Parse("b1000000-0000-0000-0000-000000000001");
    private static readonly Guid ExpandedAuthorization = Guid.Parse("b1000000-0000-0000-0000-000000000002");
    private static readonly Guid ExpandedProfile = Guid.Parse("b1000000-0000-0000-0000-000000000003");

    private static bool IsExpandedFixture(CandidateContract.Entry entry)
    {
        if (entry.Tables.Length != 0 || entry.Indexes.Length != 0 || entry.Guards.Length != 0 || entry.ForeignKeys.Length != 0) return false;
        if (entry.Id == OwnerMigration) return !entry.Supported && entry.Columns.Length == 0;
        var expected = entry.Id switch {
            AutomationMigration => new[] { ("CommerceBusinessSettings", "CustomerAutomationStateJson") },
            ShippingMigration => new[] { ("CommerceOrders", "ShippingLabelStateJson"), ("CommerceBusinessSettings", "ShippingConfigurationJson") },
            _ => []
        };
        return expected.Length > 0 && entry.Supported && entry.Columns.Length == expected.Length
            && entry.Columns.Zip(expected).All(pair => pair.First is { Schema: "dbo", Type: "nvarchar(max)", Nullable: true,
                Precision: null, Scale: null, Collation: null } && pair.First.Default.ValueKind == JsonValueKind.Null
                && pair.First.Table == pair.Second.Item1 && pair.First.Name == pair.Second.Item2);
    }

    internal static bool ExactOwnerFixtureOperations(IReadOnlyList<MigrationOperation> operations)
    {
        var builder = new MigrationBuilder("Microsoft.EntityFrameworkCore.SqlServer");
        builder.AlterColumn<Guid>("ClientProfileId", "CommerceMerchantAuthorizations", type: "uniqueidentifier", nullable: true,
            oldClrType: typeof(Guid), oldType: "uniqueidentifier");
        string Identity(ColumnOperation c) => JsonSerializer.Serialize(new {
            c.Schema, c.Table, c.Name, ClrType = c.ClrType.FullName, c.ColumnType, c.IsUnicode, c.MaxLength,
            c.IsFixedLength, c.IsRowVersion, c.IsNullable, c.DefaultValue, c.DefaultValueSql, c.ComputedColumnSql,
            c.IsStored, c.Comment, c.Collation, c.Precision, c.Scale, c.IsDestructiveChange,
            Annotations = c.GetAnnotations().Select(a => new { a.Name, a.Value }).ToArray()
        });
        var expected = (AlterColumnOperation)builder.Operations.Single();
        return operations.Count == 1 && operations[0] is AlterColumnOperation actual
            && Identity(actual) == Identity(expected) && Identity(actual.OldColumn) == Identity(expected.OldColumn);
    }

    internal static void ValidateExpandedFixture(MasterAppDbContext db, CandidateContract.Contract contract)
    {
        var rows = contract.Migrations.Where(m => ExpandedMigrations.Contains(m.Id)).ToArray();
        if (rows.Length == 0) return;
        var storeEnd = Array.FindIndex(contract.Migrations, m => m.Id == CandidateContract.StoreMigrations[^1]) + 1;
        if (storeEnd == 0 || !contract.Migrations.Skip(storeEnd).Select(m => m.Id).SequenceEqual(ExpandedMigrations)
            || rows.Any(m => !IsExpandedFixture(m))) throw new ProbeObservationFailure("MIGRATION_COMPATIBILITY_UNPROVEN");
        var assembly = db.GetService<IMigrationsAssembly>();
        if (!ExactOwnerFixtureOperations(assembly.CreateMigration(assembly.Migrations[OwnerMigration], db.Database.ProviderName!).UpOperations))
            throw new ProbeObservationFailure("MIGRATION_COMPATIBILITY_UNPROVEN");
    }

    private static void SeedExpandedFixture(MasterAppDbContext db, bool paymentPending)
    {
        db.Add(new CommerceBusiness { Id = ExpandedBusiness, Key = "expanded-store-fixture", DisplayName = "Retained fixture" });
        db.Add(new CommerceBusinessSettings { CommerceBusinessId = ExpandedBusiness });
        db.Add(new CommerceOrder { CommerceBusinessId = ExpandedBusiness, OrderNumber = "EXPANDED-RETAINED", TotalCents = 4321, Email = "fixture@example.invalid" });
        if (!paymentPending) db.Add(new CommerceMerchantAuthorization { Id = ExpandedAuthorization, CommerceBusinessId = ExpandedBusiness,
            ClientProfileId = ExpandedProfile, StateHash = "retained-owner-fixture", Provider = "Square", Environment = "Sandbox",
            ActorId = "retained-actor", ProtectedReturnTicket = "synthetic-ticket", CallbackUrl = "https://example.invalid/callback", ExpiresUtc = new DateTime(2027, 1, 1) });
    }

    private static async Task VerifyExpandedOwner(MasterAppDbContext db, string[] applied, CancellationToken ct)
    {
        var table = await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM sys.tables WHERE object_id=OBJECT_ID('dbo.CommerceMerchantAuthorizations')").SingleAsync(ct);
        if (table == 0 && !applied.Contains("20261011064911_AddBusinessScopedMerchantPayments")) return;
        var nullable = applied.Contains(OwnerMigration) ? 1 : 0;
        var count = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*) AS Value FROM sys.columns WHERE object_id=OBJECT_ID('dbo.CommerceMerchantAuthorizations')
                AND name='ClientProfileId' AND system_type_id=36 AND max_length=16 AND is_nullable={nullable}
            """).SingleAsync(ct);
        var foreignKey = await db.Database.SqlQueryRaw<int>("""
            SELECT COUNT(*) AS Value FROM sys.foreign_keys f
            JOIN sys.foreign_key_columns c ON c.constraint_object_id=f.object_id
            WHERE f.parent_object_id=OBJECT_ID('dbo.CommerceMerchantAuthorizations')
                AND f.referenced_object_id=OBJECT_ID('dbo.CommerceBusinesses') AND f.is_disabled=0 AND f.is_not_trusted=0
                AND COL_NAME(c.parent_object_id,c.parent_column_id)='CommerceBusinessId'
                AND COL_NAME(c.referenced_object_id,c.referenced_column_id)='Id' AND f.delete_referential_action=0
            """).SingleAsync(ct);
        if (count != 1 || foreignKey != 1) throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
    }

    private static async Task VerifyFixtureContract(MasterAppDbContext db, CandidateContract.Contract contract, string[] applied, CancellationToken ct)
    {
        ValidateExpandedFixture(db, contract);
        if (!contract.Migrations.Any(m => m.Id == OwnerMigration)) { await CandidateContract.Verify(db, contract, applied, ct); return; }
        if (!applied.Contains(OwnerMigration))
        {
            var rejected = false;
            try { await CandidateContract.Verify(db, contract, applied, ct); }
            catch (ProbeObservationFailure error) when (error.Message == "MIGRATION_COMPATIBILITY_UNPROVEN") { rejected = true; }
            if (!rejected) throw new InvalidOperationException("Production owner append rejection was lost");
        }
        await VerifyExpandedOwner(db, applied, ct);
        // Only the independently checked nonadditive owner entry is removed from
        // this disposable physical-plan input. Every other current entry remains.
        var physical = contract with { Migrations = contract.Migrations.Where(m => m.Id != OwnerMigration).ToArray() };
        if (applied.Contains(OwnerMigration)) physical = AppliedOwnerFixtureContract(physical);
        await CandidateContract.Verify(db, physical, applied, ct);
    }

    internal static CandidateContract.Contract AppliedOwnerFixtureContract(CandidateContract.Contract contract)
    {
        const string payment = "20261011064911_AddBusinessScopedMerchantPayments";
        var entry = contract.Migrations.Single(x => x.Id == payment);
        var table = entry.Tables.Single(x => x.Schema == "dbo" && x.Name == "CommerceMerchantAuthorizations");
        var column = table.Columns.Single(x => x.Name == "ClientProfileId");
        if (column is not { Schema: "dbo", Table: "CommerceMerchantAuthorizations", Type: "uniqueidentifier", Nullable: false,
                Precision: null, Scale: null, Collation: null } || column.Default.ValueKind != JsonValueKind.Null)
            throw new ProbeObservationFailure("MIGRATION_COMPATIBILITY_UNPROVEN");
        return contract with { Migrations = contract.Migrations.Select(x => x.Id != payment ? x : x with {
            Tables = x.Tables.Select(t => t != table ? t : t with {
                Columns = t.Columns.Select(c => c != column ? c : c with { Nullable = true }).ToArray() }).ToArray() }).ToArray() };
    }

    private static async Task VerifyExpandedData(MasterAppDbContext db, bool paymentPending, CancellationToken ct)
    {
        var order = await db.CommerceOrders.AsNoTracking().SingleAsync(x => x.CommerceBusinessId == ExpandedBusiness, ct);
        var settings = await db.CommerceBusinessSettings.AsNoTracking().SingleAsync(x => x.CommerceBusinessId == ExpandedBusiness, ct);
        if (order.OrderNumber != "EXPANDED-RETAINED" || order.TotalCents != 4321 || order.ShippingLabelStateJson is not null
            || settings.CustomerAutomationStateJson is not null || settings.ShippingConfigurationJson is not null)
            throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
        if (!paymentPending)
        {
            var authorization = await db.Set<CommerceMerchantAuthorization>().AsNoTracking().SingleAsync(x => x.Id == ExpandedAuthorization, ct);
            if (authorization.ClientProfileId != ExpandedProfile || authorization.ActorId != "retained-actor"
                || authorization.CommerceBusinessId != ExpandedBusiness || authorization.StateHash != "retained-owner-fixture"
                || authorization.ProtectedReturnTicket != "synthetic-ticket" || authorization.CallbackUrl != "https://example.invalid/callback"
                || authorization.ExpiresUtc != new DateTime(2027, 1, 1))
                throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
        }
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // An old writer omits every new JSON field and continues to update the retained row.
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE dbo.CommerceOrders SET TotalCents=4322 WHERE Id={order.Id}", ct);
        if ((await db.CommerceOrders.AsNoTracking().SingleAsync(x => x.Id == order.Id, ct)).ShippingLabelStateJson is not null)
            throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
        db.Add(new CommerceMerchantAuthorization { CommerceBusinessId = ExpandedBusiness, ClientProfileId = null, Provider = "Square",
            Environment = "Sandbox", ActorId = "website-owner", StateHash = "website-owner-fixture", ProtectedReturnTicket = "synthetic",
            CallbackUrl = "https://example.invalid/callback", ExpiresUtc = new DateTime(2027, 1, 1) });
        var currentSettings = await db.CommerceBusinessSettings.SingleAsync(x => x.CommerceBusinessId == ExpandedBusiness, ct);
        var currentOrder = await db.CommerceOrders.SingleAsync(x => x.Id == order.Id, ct);
        currentSettings.CustomerAutomationStateJson = "{\"workflows\":[]}";
        currentSettings.ShippingConfigurationJson = "{\"connections\":[]}";
        currentOrder.ShippingLabelStateJson = "{\"status\":\"Quoted\"}";
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        var roundTrip = await db.CommerceBusinessSettings.AsNoTracking().SingleAsync(x => x.CommerceBusinessId == ExpandedBusiness, ct);
        if (roundTrip.ShippingConfigurationJson != "{\"connections\":[]}" || roundTrip.CustomerAutomationStateJson != "{\"workflows\":[]}"
            || (await db.CommerceOrders.AsNoTracking().SingleAsync(x => x.Id == order.Id, ct)).ShippingLabelStateJson != "{\"status\":\"Quoted\"}")
            throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
        if (!await db.Set<CommerceMerchantAuthorization>().AsNoTracking().AnyAsync(x =>
                x.StateHash == "website-owner-fixture" && x.ClientProfileId == null && x.CommerceBusinessId == ExpandedBusiness, ct))
            throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
        await transaction.RollbackAsync(ct);
    }
}
