using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

internal static partial class CandidateContract
{
    internal const string AssistantScopeMigration = "20261110222649_AddBusinessAssistantScopeAndFunding";
    internal const string AssistantSettlementMigration = "20261110230942_AddAssistantCostReceiptSettlement";
    internal static readonly string[] AssistantMigrations = [AssistantScopeMigration, AssistantSettlementMigration];
    internal static bool IsAssistantMigration(string id) => AssistantMigrations.Contains(id);

    // Explicit tool-owned specifications; no candidate code runs in the credentialed observer.
    internal static IReadOnlyList<MigrationOperation> AssistantOperations(string id)
    {
        var migrationBuilder = new MigrationBuilder("Microsoft.EntityFrameworkCore.SqlServer");
        if (id == AssistantScopeMigration)
        {
            migrationBuilder.DropIndex(
                name: "IX_ReceptionistCostReservations_CallId_ComponentKey",
                table: "ReceptionistCostReservations");

            migrationBuilder.AlterColumn<Guid>(
                name: "CallId",
                table: "ReceptionistCostReservations",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AddColumn<Guid>(
                name: "AssistantOperationId",
                table: "ReceptionistCostReservations",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FeatureKey",
                table: "ReceptionistCostReservations",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OperationFingerprint",
                table: "ReceptionistCostReservations",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AssistantConfigurationRevision",
                table: "MessageConversations",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AssistantConnectionId",
                table: "MessageConversations",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AssistantProvider",
                table: "MessageConversations",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AssistantWorkspaceKey",
                table: "MessageConversations",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AssistantConfigurationJson",
                table: "MarketingConnections",
                type: "nvarchar(max)",
                maxLength: 8000,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AssistantControlRevision",
                table: "MarketingConnections",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "AssistantCredentialCiphertext",
                table: "MarketingConnections",
                type: "nvarchar(max)",
                maxLength: 24000,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionistCostReservations_CallId_ComponentKey",
                table: "ReceptionistCostReservations",
                columns: new[] { "CallId", "ComponentKey" },
                unique: true,
                filter: "[CallId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionistCostReservations_OwnerBudgetAccountId_AssistantOperationId_ComponentKey",
                table: "ReceptionistCostReservations",
                columns: new[] { "OwnerBudgetAccountId", "AssistantOperationId", "ComponentKey" },
                unique: true,
                filter: "[AssistantOperationId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionistCostReservations_OwnerBudgetAccountId_OwnerBudgetPeriodId",
                table: "ReceptionistCostReservations",
                columns: new[] { "OwnerBudgetAccountId", "OwnerBudgetPeriodId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_ReceptionistCostReservation_Operation",
                table: "ReceptionistCostReservations",
                sql: "([CallId] IS NOT NULL AND [AssistantOperationId] IS NULL AND [FeatureKey] IS NULL AND [OperationFingerprint] IS NULL) OR ([CallId] IS NULL AND [AssistantOperationId] IS NOT NULL AND [FeatureKey] IS NOT NULL AND [FeatureKey] = 'business-assistant' AND [OperationFingerprint] IS NOT NULL)");

            migrationBuilder.AddForeignKey(
                name: "FK_ReceptionistCostReservations_ReceptionistBudgetPeriods_OwnerBudgetAccountId_OwnerBudgetPeriodId",
                table: "ReceptionistCostReservations",
                columns: new[] { "OwnerBudgetAccountId", "OwnerBudgetPeriodId" },
                principalTable: "ReceptionistBudgetPeriods",
                principalColumns: new[] { "BudgetAccountId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }
        else if (id == AssistantSettlementMigration)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ReceptionistUsageReceipt_Target",
                table: "ReceptionistUsageReceipts");

            migrationBuilder.AddColumn<Guid>(
                name: "AssistantReservationId",
                table: "ReceptionistUsageReceipts",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AssistantBindingJson",
                table: "ReceptionistCostReservations",
                type: "nvarchar(max)",
                maxLength: 8000,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionistUsageReceipts_AssistantReservationId",
                table: "ReceptionistUsageReceipts",
                column: "AssistantReservationId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ReceptionistUsageReceipt_Target",
                table: "ReceptionistUsageReceipts",
                sql: "([CallId] IS NOT NULL AND [AccountChargeId] IS NULL AND [AssistantReservationId] IS NULL) OR ([CallId] IS NULL AND [AccountChargeId] IS NOT NULL AND [AssistantReservationId] IS NULL) OR ([CallId] IS NULL AND [AccountChargeId] IS NULL AND [AssistantReservationId] IS NOT NULL)");

            migrationBuilder.AddForeignKey(
                name: "FK_ReceptionistUsageReceipts_ReceptionistCostReservations_AssistantReservationId",
                table: "ReceptionistUsageReceipts",
                column: "AssistantReservationId",
                principalTable: "ReceptionistCostReservations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
        else throw new InvalidOperationException();
        return migrationBuilder.Operations;
    }

    internal static string AssistantDownGuard(string id) => id switch {
        AssistantScopeMigration => "IF EXISTS (SELECT 1 FROM [ReceptionistCostReservations] WHERE [AssistantOperationId] IS NOT NULL) OR EXISTS (SELECT 1 FROM [MessageConversations] WHERE [AssistantWorkspaceKey] IS NOT NULL) OR EXISTS (SELECT 1 FROM [MarketingConnections] WHERE [AssistantConfigurationJson] IS NOT NULL OR [AssistantCredentialCiphertext] IS NOT NULL) THROW 51000, 'Assistant data exists; destructive schema rollback is prohibited. Use reviewed forward recovery.', 1;",
        AssistantSettlementMigration => "IF EXISTS (SELECT 1 FROM [ReceptionistUsageReceipts] WHERE [AssistantReservationId] IS NOT NULL) OR EXISTS (SELECT 1 FROM [ReceptionistCostReservations] WHERE [AssistantBindingJson] IS NOT NULL) THROW 51000, 'Assistant settlement evidence exists; retain schema and use reviewed forward recovery.', 1;",
        _ => throw new InvalidOperationException()
    };

    private static string? AssistantOperationMetadata(MigrationOperation operation)
    {
        if (operation.GetAnnotations().Any() || operation.IsDestructiveChange) return null;
        object? value = operation switch {
            DropCheckConstraintOperation c => new { c.Schema, c.Table, c.Name },
            AddForeignKeyOperation f => new { f.Schema, f.Table, f.Name, f.Columns, f.PrincipalSchema,
                f.PrincipalTable, f.PrincipalColumns, f.OnDelete, f.OnUpdate },
            _ => null
        };
        return value is null ? BrowserOperationMetadata(operation) :
            operation.GetType().FullName + ":" + JsonSerializer.Serialize(value, Json);
    }
    internal static bool ExactAssistantOperations(string id, IReadOnlyList<MigrationOperation> operations)
    {
        var expected = AssistantOperations(id);
        return operations.Count == expected.Count && operations.Zip(expected).All(p =>
            AssistantOperationMetadata(p.First) is { } actual && actual == AssistantOperationMetadata(p.Second));
    }
    private static Entry AssistantEntry(string id, bool supported)
    {
        var operations = AssistantOperations(id);
        return new(id, supported, operations.OfType<AddColumnOperation>().Select(DescribeColumn).ToArray(), [],
            operations.OfType<CreateIndexOperation>().Select(i => new Index(i.Schema ?? "dbo", i.Table,
                i.Name, i.Columns, i.IsUnique, i.Filter)).ToArray(), [id],
            operations.OfType<AddForeignKeyOperation>().Select(f => new TableForeignKey(f.Schema ?? "dbo", f.Table,
                DescribeForeignKey(f))).ToArray());
    }
    internal static bool ExactAssistantEntry(Entry entry) => IsAssistantMigration(entry.Id) && entry.Supported &&
        JsonSerializer.Serialize(entry, Json) == JsonSerializer.Serialize(AssistantEntry(entry.Id, true), Json);

    private static Entry[] EffectiveAssistantSchema(Entry[] entries, Entry[] original, string[] applied)
    {
        var assistants = original.Where(m => IsAssistantMigration(m.Id)).ToArray();
        if (assistants.Length == 0) return entries;
        var coordination = original.SingleOrDefault(m => m.Id == CoordinationMigration);
        if (!assistants.Select(m => m.Id).SequenceEqual(AssistantMigrations.Take(assistants.Length)) ||
            assistants.Any(m => !ExactAssistantEntry(m)) ||
            assistants.SkipWhile(m => applied.Contains(m.Id)).Any(m => applied.Contains(m.Id)) ||
            coordination is null || !coordination.Supported ||
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(coordination, Json))) !=
                "d2da70a25230e14bf520e4129ac95a9fc558e9316385cedfcb172a5d82564fd6" ||
            assistants.Any(m => applied.Contains(m.Id)) && !applied.Contains(CoordinationMigration))
            throw new ProbeObservationFailure("MIGRATION_COMPATIBILITY_UNPROVEN");
        var active = assistants.Where(m => applied.Contains(m.Id)).ToArray();
        if (active.Length == 0) return entries;
        return entries.Select(m => m.Id != CoordinationMigration ? m : m with {
            Tables = m.Tables.Select(t => t.Name is not ("ReceptionistCostReservations" or "ReceptionistUsageReceipts") ? t : t with {
                Columns = t.Columns.Select(c => t.Name == "ReceptionistCostReservations" && c.Name == "CallId"
                    ? c with { Nullable = true } : c).Concat(active.SelectMany(a => a.Columns).Where(c => c.Table == t.Name)).ToArray(),
                Checks = t.Checks.Where(c => !(active.Any(a => a.Id == AssistantSettlementMigration) && c.Name == "CK_ReceptionistUsageReceipt_Target"))
                    .Concat(active.SelectMany(a => AssistantOperations(a.Id).OfType<AddCheckConstraintOperation>())
                        .Where(c => c.Table == t.Name).Select(c => new CheckConstraint(c.Name, c.Sql))).ToArray()
            }).ToArray(),
            Indexes = m.Indexes.Select(i => i.Name == "IX_ReceptionistCostReservations_CallId_ComponentKey"
                ? i with { Filter = "[CallId] IS NOT NULL" } : i).ToArray()
        }).ToArray();
    }

    private static void PlanAssistant(CatalogBatch batch, Entry entry, bool applied, bool coordinationApplied,
        CancellationToken cancellation)
    {
        foreach (var group in entry.Columns.GroupBy(c => (c.Schema, c.Table)))
        {
            var coordinationTable = group.Key.Table is "ReceptionistCostReservations" or "ReceptionistUsageReceipts";
            // Existing coordination overlay owns applied columns; pending tables own absence.
            if (coordinationTable && (applied || !coordinationApplied)) continue;
            PlanColumns(batch, group.ToArray(), applied, cancellation);
        }
        if (!coordinationApplied) return;
        foreach (var index in entry.Indexes.Where(i => i.Name != "IX_ReceptionistCostReservations_CallId_ComponentKey"))
            PlanIndex(batch, index, applied, false, false, cancellation);
        foreach (var foreignKey in entry.ForeignKeys) PlanForeignKey(batch, foreignKey, applied, cancellation);
    }
}
