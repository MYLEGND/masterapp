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
    private const string MemberMigration = "20261104012500_ParfaitMemberPageRestrictions";
    private const string PromotionMigration = "20261104014500_TrainingPromotionMemberLimits";
    internal const string BundleMigration = "20261110000000_AddCommerceBundleInventory";
    private static bool HasBundleMigration(MasterAppDbContext db) => db.GetService<IMigrationsAssembly>().Migrations.ContainsKey(BundleMigration);
    private static readonly string[] ExpandedMigrations = [OwnerMigration, AutomationMigration, ShippingMigration, MemberMigration, PromotionMigration];
    private static readonly Guid ExpandedBusiness = Guid.Parse("b1000000-0000-0000-0000-000000000001");
    private static readonly Guid ExpandedAuthorization = Guid.Parse("b1000000-0000-0000-0000-000000000002");
    private static readonly Guid ExpandedProfile = Guid.Parse("b1000000-0000-0000-0000-000000000003");

    private static bool IsExpandedFixture(CandidateContract.Entry entry)
    {
        if (entry.Id == PromotionMigration)
            return JsonSerializer.Serialize(entry, CandidateContract.Json) == JsonSerializer.Serialize(
                CandidateContract.Describe(PromotionMigration, ExpectedStandaloneOperations(PromotionMigration)), CandidateContract.Json);
        if (entry.Tables.Length != 0 || entry.Indexes.Length != 0 || entry.Guards.Length != 0 || entry.ForeignKeys.Length != 0) return false;
        if (entry.Id == OwnerMigration) return !entry.Supported && entry.Columns.Length == 0;
        var expected = entry.Id switch {
            MemberMigration => new[] { ("CommerceBusinessMembers", "ParfaitPageGrantsJson") },
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

    private static IReadOnlyList<MigrationOperation> ExpectedStandaloneOperations(string id)
    {
        var builder = new MigrationBuilder("Microsoft.EntityFrameworkCore.SqlServer");
        if (id == MemberMigration)
            builder.AddColumn<string>("ParfaitPageGrantsJson", "CommerceBusinessMembers", type: "nvarchar(max)", nullable: true);
        else if (id == BundleMigration)
        {
            builder.AddColumn<string>("BundleComponentsJson", "CommerceProducts", type: "nvarchar(max)", nullable: true);
            builder.AddColumn<string>("InventoryRequirementsJson", "CommerceOrderLines", type: "nvarchar(max)", nullable: true);
        }
        else if (id == PromotionMigration)
        {
            builder.AddColumn<int>("UsageLimit", "CommerceProductDiscounts", type: "int", nullable: true);
            builder.AddColumn<Guid>("TrainingDiscountId", "ClientSubscriptionOffers", type: "uniqueidentifier", nullable: true);
            builder.CreateIndex("IX_ClientSubscriptionOffers_TrainingDiscountId_ClientProfileId", "ClientSubscriptionOffers", new[] { "TrainingDiscountId", "ClientProfileId" });
            builder.AddForeignKey("FK_ClientSubscriptionOffers_CommerceProductDiscounts_TrainingDiscountId", "ClientSubscriptionOffers", "TrainingDiscountId", "CommerceProductDiscounts", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
        }
        else throw new InvalidOperationException("Unknown standalone fixture");
        return builder.Operations;
    }

    internal static bool ExactStandaloneFixtureOperations(string id, IReadOnlyList<MigrationOperation> operations)
    {
        // Serialize every operation property and annotation from the explicit
        // fixture oracle, including FK actions absent from the public descriptor.
        string Identity(MigrationOperation operation) => JsonSerializer.Serialize(new {
            Properties = operation.GetType().GetProperties().Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
                .OrderBy(p => p.Name).ToDictionary(p => p.Name, p => p.GetValue(operation) is Type type ? (object?)type.FullName : p.GetValue(operation)),
            Annotations = operation.GetAnnotations().OrderBy(a => a.Name).Select(a => new { a.Name, a.Value }).ToArray()
        });
        var expected = ExpectedStandaloneOperations(id);
        return operations.Count == expected.Count && operations.Zip(expected).All(pair =>
            pair.First.GetType() == pair.Second.GetType() && Identity(pair.First) == Identity(pair.Second));
    }

    internal static void ValidateExpandedFixture(MasterAppDbContext db, CandidateContract.Contract contract)
    {
        ValidateSetupGuideFixture(db, contract);
        var guide = HasSetupGuideMigration(db);
        var bundle = HasBundleMigration(db);
        var rows = contract.Migrations.Where(m => ExpandedMigrations.Contains(m.Id)).ToArray();
        if (rows.Length == 0 && !guide && !bundle) return;
        var storeEnd = Array.FindIndex(contract.Migrations, m => m.Id == CandidateContract.StoreMigrations[^1]) + 1;
        var reviewedSuffix = guide ? ExpandedMigrations.Append(SetupGuideMigration) : ExpandedMigrations;
        if (bundle) reviewedSuffix = reviewedSuffix.Append(BundleMigration);
        if (storeEnd == 0 || !contract.Migrations.Skip(storeEnd).Select(m => m.Id).SequenceEqual(reviewedSuffix)
            || rows.Any(m => !IsExpandedFixture(m))) throw new ProbeObservationFailure("MIGRATION_COMPATIBILITY_UNPROVEN");
        var assembly = db.GetService<IMigrationsAssembly>();
        if (bundle)
        {
            var operations = assembly.CreateMigration(assembly.Migrations[BundleMigration], db.Database.ProviderName!).UpOperations;
            var entries = contract.Migrations.Where(m => m.Id == BundleMigration).ToArray();
            if (!ExactStandaloneFixtureOperations(BundleMigration, operations) || entries.Length != 1 ||
                JsonSerializer.Serialize(entries[0], CandidateContract.Json) != JsonSerializer.Serialize(
                    CandidateContract.Describe(BundleMigration, ExpectedStandaloneOperations(BundleMigration)), CandidateContract.Json))
                throw new ProbeObservationFailure("MIGRATION_COMPATIBILITY_UNPROVEN");
        }
        if (!ExactOwnerFixtureOperations(assembly.CreateMigration(assembly.Migrations[OwnerMigration], db.Database.ProviderName!).UpOperations))
            throw new ProbeObservationFailure("MIGRATION_COMPATIBILITY_UNPROVEN");
        foreach (var id in new[] { MemberMigration, PromotionMigration })
            if (!ExactStandaloneFixtureOperations(id, assembly.CreateMigration(assembly.Migrations[id], db.Database.ProviderName!).UpOperations))
                throw new ProbeObservationFailure("MIGRATION_COMPATIBILITY_UNPROVEN");
    }

    private static void SeedExpandedFixture(MasterAppDbContext db, bool paymentPending)
    {
        db.Add(new CommerceBusiness { Id = ExpandedBusiness, Key = "expanded-store-fixture", DisplayName = "Retained fixture" });
        db.Add(new CommerceBusinessSettings { CommerceBusinessId = ExpandedBusiness });
        db.Add(new ClientProfile { Id = ExpandedProfile, ClientUserId = "lead:expanded-fixture", Email = "expanded@example.invalid" });
        var product = new CommerceProduct { CommerceBusinessId = ExpandedBusiness, ExternalProductKey = "expanded-plan", Name = "Retained plan" };
        db.Add(product);
        db.Add(new CommerceProductDiscount { CommerceProductId = product.Id, Code = "RETAINED", ExternalDiscountKey = "expanded-discount", Amount = 29 });
        db.Add(new CommerceBusinessMember { CommerceBusinessId = ExpandedBusiness, Email = "expanded@example.invalid", NormalizedEmail = "expanded@example.invalid", DisplayName = "Retained member" });
        db.Add(new ClientSubscriptionOffer { ClientProfileId = ExpandedProfile, OwnerAgentUserId = "expanded-fixture", MonthlyAmountCents = 10000 });
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
        if (!applied.Contains(OwnerMigration) || !applied.Contains(PromotionMigration) ||
            HasSetupGuideMigration(db) && !applied.Contains(SetupGuideMigration))
        {
            var rejected = false;
            try { await CandidateContract.Verify(db, contract, applied, ct); }
            catch (ProbeObservationFailure error) when (error.Message == "MIGRATION_COMPATIBILITY_UNPROVEN") { rejected = true; }
            if (!rejected) throw new InvalidOperationException("Production unsupported append rejection was lost");
        }
        await VerifyExpandedOwner(db, applied, ct);
        await VerifyPromotionStructures(db, applied.Contains(PromotionMigration), ct);
        // Only the independently checked nonadditive owner entry is removed from
        // this disposable physical-plan input. Every other current entry remains.
        var physical = contract with { Migrations = contract.Migrations.Where(m => m.Id != OwnerMigration).ToArray() };
        physical = physical with { Migrations = physical.Migrations.Select(m => m.Id == PromotionMigration
            ? m with { Supported = true, Indexes = [], ForeignKeys = [] }
            : m.Id == SetupGuideMigration ? m with { Supported = true } : m).ToArray() };
        // Only this disposable physical projection admits the exact guide table.
        // ValidateExpandedFixture already checked every raw operation and seed byte;
        // the production descriptor remains unsupported because it contains InsertData.
        if (applied.Contains(OwnerMigration)) physical = AppliedOwnerFixtureContract(physical);
        await CandidateContract.Verify(db, physical, applied, ct);
        if (HasSetupGuideMigration(db)) await VerifySetupGuideSeed(db, applied.Contains(SetupGuideMigration), ct);
    }

    private static async Task VerifyPromotionStructures(MasterAppDbContext db, bool applied, CancellationToken ct)
    {
        var indexes = await db.Database.SqlQueryRaw<int>("""
            SELECT COUNT(*) AS Value FROM sys.indexes WHERE object_id=OBJECT_ID('dbo.ClientSubscriptionOffers')
              AND name='IX_ClientSubscriptionOffers_TrainingDiscountId_ClientProfileId'
            """).SingleAsync(ct);
        var foreignKeys = await db.Database.SqlQueryRaw<int>("""
            SELECT COUNT(*) AS Value FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID('dbo.ClientSubscriptionOffers')
              AND name='FK_ClientSubscriptionOffers_CommerceProductDiscounts_TrainingDiscountId'
            """).SingleAsync(ct);
        if (!applied) { if (indexes != 0 || foreignKeys != 0) throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT"); return; }
        var exactIndex = await db.Database.SqlQueryRaw<int>("""
            SELECT COUNT(*) AS Value FROM sys.indexes i WHERE i.object_id=OBJECT_ID('dbo.ClientSubscriptionOffers')
              AND i.name='IX_ClientSubscriptionOffers_TrainingDiscountId_ClientProfileId'
              AND i.is_unique=0 AND i.has_filter=0 AND i.filter_definition IS NULL AND i.is_disabled=0 AND i.type=2
              AND (SELECT COUNT(*) FROM sys.index_columns c WHERE c.object_id=i.object_id AND c.index_id=i.index_id)=2
              AND EXISTS(SELECT 1 FROM sys.index_columns c WHERE c.object_id=i.object_id AND c.index_id=i.index_id
                AND c.key_ordinal=1 AND c.is_included_column=0 AND c.is_descending_key=0 AND COL_NAME(c.object_id,c.column_id)='TrainingDiscountId')
              AND EXISTS(SELECT 1 FROM sys.index_columns c WHERE c.object_id=i.object_id AND c.index_id=i.index_id
                AND c.key_ordinal=2 AND c.is_included_column=0 AND c.is_descending_key=0 AND COL_NAME(c.object_id,c.column_id)='ClientProfileId')
            """).SingleAsync(ct);
        var exactForeignKey = await db.Database.SqlQueryRaw<int>("""
            SELECT COUNT(*) AS Value FROM sys.foreign_keys f WHERE f.parent_object_id=OBJECT_ID('dbo.ClientSubscriptionOffers')
              AND f.name='FK_ClientSubscriptionOffers_CommerceProductDiscounts_TrainingDiscountId'
              AND f.referenced_object_id=OBJECT_ID('dbo.CommerceProductDiscounts') AND f.is_disabled=0 AND f.is_not_trusted=0
              AND f.delete_referential_action=0 AND f.update_referential_action=0 AND f.is_not_for_replication=0
              AND (SELECT COUNT(*) FROM sys.foreign_key_columns c WHERE c.constraint_object_id=f.object_id)=1
              AND EXISTS(SELECT 1 FROM sys.foreign_key_columns c WHERE c.constraint_object_id=f.object_id
                AND COL_NAME(c.parent_object_id,c.parent_column_id)='TrainingDiscountId'
                AND COL_NAME(c.referenced_object_id,c.referenced_column_id)='Id')
            """).SingleAsync(ct);
        if (indexes != 1 || foreignKeys != 1 || exactIndex != 1 || exactForeignKey != 1)
            throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
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
        var member = await db.CommerceBusinessMembers.AsNoTracking().SingleAsync(x => x.CommerceBusinessId == ExpandedBusiness, ct);
        var discount = await db.CommerceProductDiscounts.AsNoTracking().SingleAsync(x => x.ExternalDiscountKey == "expanded-discount", ct);
        var offer = await db.ClientSubscriptionOffers.AsNoTracking().SingleAsync(x => x.ClientProfileId == ExpandedProfile, ct);
        if (member.ParfaitPageGrantsJson is not null || member.DisplayName != "Retained member" || discount.UsageLimit is not null
            || discount.Amount != 29 || offer.TrainingDiscountId is not null || offer.MonthlyAmountCents != 10000)
            throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
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
        // Old writers omit the additive fields; new writers retain their exact values.
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE dbo.CommerceBusinessMembers SET DisplayName='Old writer' WHERE Id={member.Id}", ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE dbo.CommerceProductDiscounts SET Amount=28 WHERE Id={discount.Id}", ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE dbo.ClientSubscriptionOffers SET MonthlyAmountCents=9900 WHERE Id={offer.Id}", ct);
        var updatedMember = await db.CommerceBusinessMembers.SingleAsync(x => x.Id == member.Id, ct);
        var updatedDiscount = await db.CommerceProductDiscounts.SingleAsync(x => x.Id == discount.Id, ct);
        var updatedOffer = await db.ClientSubscriptionOffers.SingleAsync(x => x.Id == offer.Id, ct);
        if (updatedMember.ParfaitPageGrantsJson is not null || updatedDiscount.UsageLimit is not null || updatedOffer.TrainingDiscountId is not null)
            throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
        updatedMember.ParfaitPageGrantsJson = "[\"/internal/commerce/orders\"]";
        updatedDiscount.UsageLimit = 100;
        updatedOffer.TrainingDiscountId = discount.Id;
        await db.SaveChangesAsync(ct); db.ChangeTracker.Clear();
        if ((await db.CommerceBusinessMembers.AsNoTracking().SingleAsync(x => x.Id == member.Id, ct)).ParfaitPageGrantsJson != "[\"/internal/commerce/orders\"]"
            || (await db.CommerceProductDiscounts.AsNoTracking().SingleAsync(x => x.Id == discount.Id, ct)).UsageLimit != 100
            || (await db.ClientSubscriptionOffers.AsNoTracking().SingleAsync(x => x.Id == offer.Id, ct)).TrainingDiscountId != discount.Id)
            throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
        await transaction.RollbackAsync(ct);
    }
}
