using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

internal static partial class CandidateContract
{
    internal const string BrowserMigration = "20261026223148_ReceptionistBrowserCreation";
    internal const string BrowserTransition = "receptionist-browser-creation-v1";
    private const string CoordinationMigration = "20261020160000_AddReceptionistCoordination";
    private const string ProviderIndexName = "IX_ReceptionistCalls_Provider_ProviderProjectKey_ProviderSessionId";
    internal const string BrowserCheckSql = "([BrowserCreationId] IS NULL AND [BrowserOfferSha256] IS NULL AND [ProviderSessionId] IS NOT NULL) OR ([BrowserCreationId] IS NOT NULL AND [BrowserOfferSha256] IS NOT NULL AND LEN([BrowserOfferSha256]) = 64 AND [ProviderAcceptAttemptId] IS NOT NULL AND ([ProviderSessionId] IS NOT NULL OR ([State] = 'uncertain' AND [ProviderClosedUtc] IS NULL AND [SidebandLeaseToken] IS NULL)))";

    // Tool-owned exact operation specification. Never load candidate code in the
    // credentialed observer, or infer arbitrary AlterColumn/DropIndex authority.
    internal static IReadOnlyList<MigrationOperation> BrowserOperations()
    {
        var b = new MigrationBuilder("Microsoft.EntityFrameworkCore.SqlServer");
        b.DropIndex(ProviderIndexName, "ReceptionistCalls");
        b.AlterColumn<string>("ProviderSessionId", "ReceptionistCalls", type: "nvarchar(200)", maxLength: 200,
            nullable: true, collation: "Latin1_General_100_BIN2", oldClrType: typeof(string),
            oldType: "nvarchar(200)", oldMaxLength: 200, oldCollation: "Latin1_General_100_BIN2");
        b.AddColumn<Guid>("BrowserCreationId", "ReceptionistCalls", type: "uniqueidentifier", nullable: true);
        b.AddColumn<string>("BrowserOfferSha256", "ReceptionistCalls", type: "nvarchar(64)", maxLength: 64,
            nullable: true, collation: "Latin1_General_100_BIN2");
        b.CreateIndex("IX_ReceptionistCalls_BrowserCreationId", "ReceptionistCalls", "BrowserCreationId",
            unique: true, filter: "[BrowserCreationId] IS NOT NULL");
        b.CreateIndex(ProviderIndexName, "ReceptionistCalls", ["Provider", "ProviderProjectKey", "ProviderSessionId"],
            unique: true, filter: "[ProviderSessionId] IS NOT NULL");
        b.AddCheckConstraint("CK_ReceptionistCall_BrowserCreation", "ReceptionistCalls", BrowserCheckSql);
        return b.Operations;
    }

    private static object ColumnMetadata(ColumnOperation c) => new {
        c.Schema, c.Table, c.Name, ClrType = c.ClrType?.FullName, c.ColumnType, c.IsUnicode, c.MaxLength,
        c.IsFixedLength, c.IsRowVersion, c.IsNullable, c.DefaultValue, c.DefaultValueSql, c.ComputedColumnSql,
        c.IsStored, c.Comment, c.Collation, c.Precision, c.Scale, c.IsDestructiveChange,
        Annotations = c.GetAnnotations().Select(a => new { a.Name, a.Value }).ToArray()
    };
    private static string? BrowserOperationMetadata(MigrationOperation operation)
    {
        if (operation.GetAnnotations().Any() || operation.IsDestructiveChange) return null;
        object? value = operation switch {
            AlterColumnOperation c when !c.OldColumn.GetAnnotations().Any() => new {
                Current = ColumnMetadata(c), Previous = ColumnMetadata(c.OldColumn) },
            AddColumnOperation c => ColumnMetadata(c),
            DropIndexOperation d => new { d.Schema, d.Table, d.Name },
            CreateIndexOperation i => new { i.Schema, i.Table, i.Name, i.Columns, i.IsUnique, i.Filter, i.IsDescending },
            AddCheckConstraintOperation c => new { c.Schema, c.Table, c.Name, c.Sql },
            _ => null
        };
        return value is null ? null : operation.GetType().FullName + ":" + JsonSerializer.Serialize(value, Json);
    }
    private static bool ExactBrowserOperations(IReadOnlyList<MigrationOperation> operations)
    {
        var expected = BrowserOperations();
        return operations.Count == expected.Count && operations.Zip(expected).All(p =>
            BrowserOperationMetadata(p.First) is { } actual && actual == BrowserOperationMetadata(p.Second));
    }
    private static Entry BrowserEntry(bool supported) => new(BrowserMigration, supported,
        BrowserOperations().OfType<AddColumnOperation>().Select(DescribeColumn).ToArray(), [],
        BrowserOperations().OfType<CreateIndexOperation>().Select(i => new Index(i.Schema ?? "dbo", i.Table,
            i.Name, i.Columns, i.IsUnique, i.Filter)).ToArray(), [BrowserTransition], []);
    private static bool ExactBrowserEntry(Entry entry) => entry.Id == BrowserMigration && entry.Supported &&
        JsonSerializer.Serialize(entry, Json) == JsonSerializer.Serialize(BrowserEntry(true), Json);

    private static Entry[] EffectiveBrowserSchema(Entry[] entries, string[] applied)
    {
        var browser = entries.SingleOrDefault(m => m.Id == BrowserMigration);
        if (browser is null) return entries;
        var coordination = entries.SingleOrDefault(m => m.Id == CoordinationMigration);
        if (!ExactBrowserEntry(browser) || coordination is null || !coordination.Supported ||
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(coordination, Json))) !=
                "d2da70a25230e14bf520e4129ac95a9fc558e9316385cedfcb172a5d82564fd6" ||
            applied.Contains(BrowserMigration) && !applied.Contains(CoordinationMigration))
            throw new ProbeObservationFailure("MIGRATION_COMPATIBILITY_UNPROVEN");
        if (!applied.Contains(BrowserMigration)) return entries;
        // Observation-only copy: historical descriptors/digests never change.
        return entries.Select(m => m.Id != CoordinationMigration ? m : m with {
            Tables = m.Tables.Select(t => t.Name != "ReceptionistCalls" ? t : t with {
                Columns = t.Columns.Select(c => c.Name == "ProviderSessionId" ? c with { Nullable = true } : c)
                    .Concat(browser.Columns).ToArray(),
                Checks = [..t.Checks, new("CK_ReceptionistCall_BrowserCreation", BrowserCheckSql)]
            }).ToArray(),
            Indexes = m.Indexes.Select(i => i.Name == ProviderIndexName ? i with {
                Filter = "[ProviderSessionId] IS NOT NULL" } : i).ToArray()
        }).ToArray();
    }
    private static void PlanBrowser(CatalogBatch batch, Entry browser, bool applied, bool coordinationApplied,
        CancellationToken cancellation)
    {
        // Coordination's table absence covers every child when still pending.
        if (!coordinationApplied) return;
        // Existing same-name provider index and complete checks are owned by the
        // original/overlaid coordination entry, not a second contradictory plan.
        var index = browser.Indexes.Single(i => i.Name == "IX_ReceptionistCalls_BrowserCreationId");
        PlanIndex(batch, index, applied, false, false, cancellation);
        if (!applied) PlanColumns(batch, browser.Columns, false, cancellation);
    }
}
