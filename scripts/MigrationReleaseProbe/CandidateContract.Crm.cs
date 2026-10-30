using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

internal static partial class CandidateContract
{
    internal const string CrmMigration = "20261030070000_CrmRelationshipHistory";
    internal const string CrmEntrySha256 = "c7bc7e795c962bd11870e05a6d51c595a7b2106e2987deb7d905cc0b784d857c";
    internal const string CrmTransition = "crm-relationship-history-v1";
    // Exact tool-owned specification. This does not grant generic nullable-column
    // or check-constraint mutation authority to candidate contracts.
    internal static IReadOnlyList<MigrationOperation> CrmOperations()
    {
        var migrationBuilder = new MigrationBuilder("Microsoft.EntityFrameworkCore.SqlServer");

        migrationBuilder.AddColumn<Guid>(name: "AssociationVersion", table: "WebsiteLeadIntakeLinks", type: "uniqueidentifier", nullable: false, defaultValue: Guid.Empty);
        migrationBuilder.AlterColumn<string>(name: "WorkstationLeadId", table: "WebsiteLeadIntakeLinks", type: "nvarchar(64)", maxLength: 64, nullable: true,
            oldClrType: typeof(string), oldType: "nvarchar(64)", oldMaxLength: 64);
        migrationBuilder.AddColumn<Guid>(name: "HistoryClientProfileId", table: "WebsiteLeadIntakeLinks", type: "uniqueidentifier", nullable: true);
        migrationBuilder.CreateIndex(name: "IX_WebsiteLeadIntakeLinks_HistoryClientProfileId", table: "WebsiteLeadIntakeLinks", column: "HistoryClientProfileId");
        migrationBuilder.AddForeignKey(name: "FK_WebsiteLeadIntakeLinks_ClientProfiles_HistoryClientProfileId", table: "WebsiteLeadIntakeLinks", column: "HistoryClientProfileId",
            principalTable: "ClientProfiles", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
        migrationBuilder.AddCheckConstraint(name: "CK_WebsiteLeadIntakeLinks_HistoryAssociation", table: "WebsiteLeadIntakeLinks", sql: "(([WorkstationLeadId] IS NOT NULL AND [HistoryClientProfileId] IS NULL) OR ([WorkstationLeadId] IS NULL AND [HistoryClientProfileId] IS NOT NULL AND [CommerceBusinessId] IS NULL))");
        migrationBuilder.CreateTable("CrmHistoryEntries", columns: table => new
        {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                RelationshipKey = table.Column<string>(type: "nvarchar(100)", nullable: false, maxLength: 100),
                RecordedBy = table.Column<string>(type: "nvarchar(450)", nullable: false, maxLength: 450),
                RecordedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                FiledUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                OccurredUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                OccurredDate = table.Column<string>(type: "nvarchar(10)", nullable: true, maxLength: 10),
                TimeZoneId = table.Column<string>(type: "nvarchar(100)", nullable: false, maxLength: 100),
                TimePrecision = table.Column<string>(type: "nvarchar(20)", nullable: false, maxLength: 20),
                Type = table.Column<string>(type: "nvarchar(60)", nullable: false, maxLength: 60),
                Source = table.Column<string>(type: "nvarchar(80)", nullable: false, maxLength: 80),
                SourceId = table.Column<string>(type: "nvarchar(200)", nullable: false, maxLength: 200),
                ContentJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                SubmissionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                RelatedEntryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                FormName = table.Column<string>(type: "nvarchar(160)", nullable: true, maxLength: 160),
                FormVersion = table.Column<string>(type: "nvarchar(100)", nullable: true, maxLength: 100),
                Product = table.Column<string>(type: "nvarchar(100)", nullable: true, maxLength: 100),
                CaseReference = table.Column<string>(type: "nvarchar(200)", nullable: true, maxLength: 200),
                AssignedOwner = table.Column<string>(type: "nvarchar(450)", nullable: true, maxLength: 450),
                DueUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                Status = table.Column<string>(type: "nvarchar(40)", nullable: false, maxLength: 40),
                EvidenceStatus = table.Column<string>(type: "nvarchar(80)", nullable: false, maxLength: 80)
        }, constraints: table => table.PrimaryKey("PK_CrmHistoryEntries", x => x.Id));
        migrationBuilder.CreateIndex("IX_CrmHistoryEntries_RelationshipKey_Source_SourceId", "CrmHistoryEntries", new[] { "RelationshipKey", "Source", "SourceId" }, unique: true);
        migrationBuilder.CreateIndex("IX_CrmHistoryEntries_RelationshipKey_OccurredUtc_Id", "CrmHistoryEntries", new[] { "RelationshipKey", "OccurredUtc", "Id" }, unique: false);
        migrationBuilder.CreateIndex("IX_CrmHistoryEntries_RelationshipKey_RecordedUtc_Id", "CrmHistoryEntries", new[] { "RelationshipKey", "RecordedUtc", "Id" }, unique: false);
        migrationBuilder.CreateIndex("IX_CrmHistoryEntries_SubmissionId", "CrmHistoryEntries", new[] { "SubmissionId" }, unique: false);

        return migrationBuilder.Operations;
    }

    private static string? CrmOperationMetadata(MigrationOperation operation)
    {
        if (operation.GetAnnotations().Any() || operation.IsDestructiveChange) return null;
        object? value = operation switch {
            AddForeignKeyOperation f => new { f.Schema, f.Table, f.Name, f.Columns,
                f.PrincipalSchema, f.PrincipalTable, f.PrincipalColumns, f.OnDelete, f.OnUpdate },
            CreateTableOperation t when t.PrimaryKey is not null => new {
                t.Schema, t.Name, t.Comment,
                Columns = t.Columns.Select(ColumnMetadata).ToArray(),
                PrimaryKey = new { t.PrimaryKey.Schema, t.PrimaryKey.Table, t.PrimaryKey.Name,
                    t.PrimaryKey.Columns, Annotations = t.PrimaryKey.GetAnnotations().Select(a => new {a.Name, a.Value}).ToArray() },
                ForeignKeys = t.ForeignKeys.Select(CrmOperationMetadata).ToArray(),
                UniqueKeys = t.UniqueConstraints.Select(k => new { k.Schema, k.Table, k.Name, k.Columns,
                    Annotations = k.GetAnnotations().Select(a => new { a.Name, a.Value }).ToArray() }).ToArray(),
                Checks = t.CheckConstraints.Select(CrmOperationMetadata).ToArray()
            },
            _ => null
        };
        return value is null ? BrowserOperationMetadata(operation) :
            operation.GetType().FullName + ":" + JsonSerializer.Serialize(value, Json);
    }

    private static bool ExactCrmOperations(IReadOnlyList<MigrationOperation> operations)
    {
        var expected = CrmOperations();
        return operations.Count == expected.Count && operations.Zip(expected).All(p =>
            CrmOperationMetadata(p.First) is { } actual && actual == CrmOperationMetadata(p.Second));
    }

    private static Entry CrmEntry(bool supported)
    {
        var entry = Describe("crm-descriptor-only", CrmOperations());
        return entry with { Id = CrmMigration, Supported = supported, Guards = [CrmTransition] };
    }
    private static bool ExactCrmEntry(Entry entry) => entry.Id == CrmMigration && entry.Supported &&
        JsonSerializer.Serialize(entry, Json) == JsonSerializer.Serialize(CrmEntry(true), Json);

    private static void PlanCrm(CatalogBatch batch, Entry entry, bool applied, CancellationToken cancellation)
    {
        PlanStructures(batch, entry, applied, cancellation);
        foreach (var group in entry.Columns.Concat(applied ? entry.Tables.SelectMany(t => t.Columns) : [])
            .GroupBy(c => (c.Schema, c.Table)))
            PlanColumns(batch, group.ToArray(), applied, cancellation);
        var lead = new Column("dbo", "WebsiteLeadIntakeLinks", "WorkstationLeadId", "nvarchar(64)",
            applied, JsonSerializer.SerializeToElement<object?>(null), null, null, null);
        var client = new Column("dbo", "WebsiteLeadIntakeLinks", "HistoryClientProfileId", "uniqueidentifier",
            true, JsonSerializer.SerializeToElement<object?>(null), null, null, null);
        var business = new Column("dbo", "WebsiteLeadIntakeLinks", "CommerceBusinessId", "uniqueidentifier",
            true, JsonSerializer.SerializeToElement<object?>(null), null, null, null);
        PlanColumns(batch, [lead, business], true, cancellation);
        var check = CrmOperations().OfType<AddCheckConstraintOperation>().Single();
        PlanChecks(batch, new Table("dbo", "WebsiteLeadIntakeLinks", [lead, client, business],
            new Key("PK_WebsiteLeadIntakeLinks", ["Id"]), [], [],
            applied ? [new CheckConstraint(check.Name, check.Sql)] : []), cancellation);
    }
}
