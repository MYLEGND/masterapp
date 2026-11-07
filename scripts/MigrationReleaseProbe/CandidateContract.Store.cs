using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

internal static partial class CandidateContract
{
    internal const string StoreContinuationMigration = "20261102062753_ScopedStoreContinuations";
    internal const string StoreCrmMigration = "20261102064637_ExplicitBusinessCrmCapability";
    internal const string StoreProvisioningMigration = "20261102085754_ScopedStoreProvisioningEvidence";
    internal const string StoreMembershipMigration = "20261102160514_ScopedStoreMembershipInvitations";
    internal static readonly string[] StoreMigrations = [StoreContinuationMigration, StoreCrmMigration, StoreProvisioningMigration, StoreMembershipMigration];
    internal const string StoreInitialCheck = "(([Purpose] IN ('Activation', 'SignIn', 'WebsiteEditor', 'TrainingBilling') AND [ClientProfileId] IS NOT NULL AND [ClientProfileId] <> '00000000-0000-0000-0000-000000000000') OR ([Purpose] = 'StoreManagement' AND [CommerceBusinessId] IS NOT NULL AND [CommerceBusinessId] <> '00000000-0000-0000-0000-000000000000' AND [ActorUserId] IS NOT NULL AND LTRIM(RTRIM([ActorUserId])) <> '' AND [DestinationOrigin] IS NOT NULL AND LTRIM(RTRIM([DestinationOrigin])) <> '' AND [WebsiteSiteKey] IS NOT NULL AND LTRIM(RTRIM([WebsiteSiteKey])) <> '' AND [WebsiteOwnerKey] IS NOT NULL AND LTRIM(RTRIM([WebsiteOwnerKey])) <> '' AND [BrowserCorrelationHash] IS NOT NULL AND LTRIM(RTRIM([BrowserCorrelationHash])) <> ''))";
    internal const string StoreFinalCheck = "(([Purpose] IN ('Activation', 'SignIn', 'WebsiteEditor', 'TrainingBilling') AND [ClientProfileId] IS NOT NULL AND [ClientProfileId] <> '00000000-0000-0000-0000-000000000000') OR ([Purpose] = 'BusinessMembership' AND [ClientProfileId] IS NOT NULL AND [CommerceBusinessId] IS NOT NULL AND [CommerceBusinessMemberId] IS NOT NULL AND [ActorUserId] IS NOT NULL) OR ([Purpose] = 'StoreManagement' AND [CommerceBusinessId] IS NOT NULL AND [CommerceBusinessId] <> '00000000-0000-0000-0000-000000000000' AND [ActorUserId] IS NOT NULL AND LTRIM(RTRIM([ActorUserId])) <> '' AND [DestinationOrigin] IS NOT NULL AND LTRIM(RTRIM([DestinationOrigin])) <> '' AND [WebsiteSiteKey] IS NOT NULL AND LTRIM(RTRIM([WebsiteSiteKey])) <> '' AND [WebsiteOwnerKey] IS NOT NULL AND LTRIM(RTRIM([WebsiteOwnerKey])) <> '' AND [BrowserCorrelationHash] IS NOT NULL AND LTRIM(RTRIM([BrowserCorrelationHash])) <> ''))";
    internal static bool IsStoreMigration(string id) => StoreMigrations.Contains(id, StringComparer.Ordinal);
    // Frozen tool-owned SQL Server specification, independent of the candidate assembly.
    // Only this exact ordered operation sequence can be represented by the descriptor.
    internal static IReadOnlyList<MigrationOperation> StoreOperations(string id)
    {
        var b = new MigrationBuilder("Microsoft.EntityFrameworkCore.SqlServer");
        switch (id)
        {
            case StoreContinuationMigration:
                b.AlterColumn<Guid>("ClientProfileId", "ClientIdentityContinuations", type: "uniqueidentifier", nullable: true,
                    oldClrType: typeof(Guid), oldType: "uniqueidentifier");
                b.AddColumn<string>("BrowserCorrelationHash", "ClientIdentityContinuations", type: "nvarchar(128)", maxLength: 128, nullable: true);
                b.AddColumn<string>("DestinationOrigin", "ClientIdentityContinuations", type: "nvarchar(512)", maxLength: 512, nullable: true);
                b.AddColumn<string>("WebsiteOwnerKey", "ClientIdentityContinuations", type: "nvarchar(450)", maxLength: 450, nullable: true);
                b.AddColumn<string>("WebsiteSiteKey", "ClientIdentityContinuations", type: "nvarchar(64)", maxLength: 64, nullable: true);
                b.Sql(StoreConstraintSql(false));
                break;
            case StoreCrmMigration:
                b.AddColumn<bool>("CanManageCrm", "CommerceBusinessMembers", type: "bit", nullable: false, defaultValue: false);
                var grant = "[CanManageOrders] = 1 OR (LOWER([RoleKey]) COLLATE Latin1_General_100_BIN2 IN (N'owner',N'platform-owner',N'account') AND DATALENGTH([RoleKey]) = DATALENGTH(RTRIM([RoleKey])))";
                b.Sql(DeferredStoreSql("UPDATE [CommerceBusinessMembers] SET [CanManageCrm] = CASE WHEN " + grant + " THEN 1 ELSE 0 END"));
                break;
            case StoreProvisioningMigration:
                b.AddColumn<Guid>("ProvisionedStoreBusinessId", "WebsiteContentState", type: "uniqueidentifier", nullable: true);
                b.Sql(DeferredStoreSql("UPDATE [WebsiteContentState] SET [ProvisionedStoreBusinessId] = [CommerceBusinessId] " +
                    "WHERE [CommerceBusinessId] IS NOT NULL AND (" + StoreEnabled("[DraftJson]") +
                    " OR EXISTS (SELECT 1 FROM [WebsiteContentVersion] v WHERE v.[StateId] = [WebsiteContentState].[Id] " +
                    "AND v.[SnapshotRetiredUtc] IS NULL AND " + StoreEnabled("v.[DocumentJson]") + "))"));
                break;
            case StoreMembershipMigration:
                b.DropCheckConstraint("CK_ClientIdentityContinuations_PurposeBinding", "ClientIdentityContinuations");
                b.AddColumn<Guid>("CommerceBusinessMemberId", "ClientIdentityContinuations", type: "uniqueidentifier", nullable: true);
                b.Sql(StoreConstraintSql(true));
                break;
            default: throw new InvalidOperationException();
        }
        return b.Operations;
    }
    private static string DeferredStoreSql(string sql) => "EXEC(N'" + sql.Replace("'", "''") + "');";
    internal static string StoreConstraintSql(bool membership) => DeferredStoreSql(
        "ALTER TABLE [ClientIdentityContinuations] ADD CONSTRAINT [CK_ClientIdentityContinuations_PurposeBinding] CHECK (" +
        (membership ? StoreFinalCheck : StoreInitialCheck) + ")");
    private static string StoreEnabled(string column)
    {
        var json = "CASE WHEN ISJSON(" + column + ") = 1 THEN " + column + " ELSE N'{}' END";
        return "(SELECT COUNT(*) FROM OPENJSON(" + json + ") WHERE LOWER([key]) = N'store') = 1 " +
            "AND EXISTS (SELECT 1 FROM OPENJSON(" + json + ") WHERE [key] COLLATE Latin1_General_100_BIN2 = N'store' AND DATALENGTH([key]) = DATALENGTH(N'store') AND [type] = 5) " +
            "AND (SELECT COUNT(*) FROM OPENJSON(" + json + ", '$.store') WHERE LOWER([key]) = N'enabled') = 1 " +
            "AND EXISTS (SELECT 1 FROM OPENJSON(" + json + ", '$.store') WHERE [key] COLLATE Latin1_General_100_BIN2 = N'enabled' AND DATALENGTH([key]) = DATALENGTH(N'enabled') AND [type] = 3 AND [value] = N'true')";
    }
    private static string? StoreOperationMetadata(MigrationOperation operation)
    {
        if (operation.GetAnnotations().Any() || operation.IsDestructiveChange) return null;
        object? value = operation switch {
            SqlOperation q => new { q.Sql, q.SuppressTransaction },
            DropCheckConstraintOperation c => new { c.Schema, c.Table, c.Name },
            _ => null
        };
        return value is null ? BrowserOperationMetadata(operation) :
            operation.GetType().FullName + ":" + JsonSerializer.Serialize(value, Json);
    }
    private static bool ExactStoreOperations(string id, IReadOnlyList<MigrationOperation> operations)
    {
        var expected = StoreOperations(id);
        return operations.Count == expected.Count && operations.Zip(expected).All(pair =>
            StoreOperationMetadata(pair.First) is { } actual && actual == StoreOperationMetadata(pair.Second));
    }
    private static Entry StoreEntry(string id, bool supported) => new(id, supported,
        StoreOperations(id).OfType<AddColumnOperation>().Select(DescribeColumn).ToArray(), [], [],
        ["reviewed-store-transition-v1:" + id], []);
    internal static bool ExactStoreEntry(Entry entry) => IsStoreMigration(entry.Id) && entry.Supported &&
        JsonSerializer.Serialize(entry, Json) == JsonSerializer.Serialize(StoreEntry(entry.Id, true), Json);

    private static void PlanStore(CatalogBatch batch, Entry entry, bool applied, string[] history, CancellationToken ct)
    {
        foreach (var group in entry.Columns.GroupBy(c => (c.Schema, c.Table)))
            PlanColumns(batch, group.ToArray(), applied, ct);
        if (entry.Id != StoreContinuationMigration) return;
        var continuationApplied = history.Contains(StoreContinuationMigration);
        var membershipApplied = history.Contains(StoreMembershipMigration);
        if (membershipApplied && !continuationApplied) throw new ProbeObservationFailure("MIGRATION_COMPATIBILITY_UNPROVEN");
        var none = JsonSerializer.SerializeToElement<object?>(null);
        Column Col(string name, string type, bool nullable = true) => new("dbo", "ClientIdentityContinuations", name, type, nullable, none, null, null, null);
        PlanColumns(batch, [Col("ClientProfileId", "uniqueidentifier", continuationApplied)], true, ct);
        var columns = new[] { Col("Purpose", "nvarchar(32)", false), Col("ClientProfileId", "uniqueidentifier"),
            Col("CommerceBusinessId", "uniqueidentifier"), Col("CommerceBusinessMemberId", "uniqueidentifier"),
            Col("ActorUserId", "nvarchar(450)") }.Concat(StoreEntry(StoreContinuationMigration, true).Columns).ToArray();
        PlanChecks(batch, new Table("dbo", "ClientIdentityContinuations", columns,
            new Key("PK_ClientIdentityContinuations", ["Id"]), [], [], continuationApplied ?
            [new CheckConstraint("CK_ClientIdentityContinuations_PurposeBinding", membershipApplied ? StoreFinalCheck : StoreInitialCheck)] : []), ct);
    }
}
