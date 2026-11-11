using Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

internal static partial class RehearsalFixture
{
    private static readonly Guid AssistantConversationId = Guid.Parse("00000000-0000-0000-0000-000000000801");
    private static async Task PrepareAssistantBaseline(MasterAppDbContext db, CandidateContract.Entry[] pending, CancellationToken ct)
    {
        if (!db.GetService<IMigrationsAssembly>().Migrations.ContainsKey(CandidateContract.AssistantScopeMigration)) return;
        // Explicit synthetic predecessor construction, never candidate Down execution.
        foreach (var entry in pending.Where(m => CandidateContract.IsAssistantMigration(m.Id)).Reverse())
        {
            var operations = CandidateContract.AssistantOperations(entry.Id);
            var b = new MigrationBuilder("Microsoft.EntityFrameworkCore.SqlServer");
            foreach (var f in operations.OfType<AddForeignKeyOperation>()) b.DropForeignKey(f.Name, f.Table);
            foreach (var i in operations.OfType<CreateIndexOperation>()) b.DropIndex(i.Name, i.Table);
            foreach (var c in operations.OfType<AddCheckConstraintOperation>()) b.DropCheckConstraint(c.Name, c.Table);
            foreach (var c in operations.OfType<AddColumnOperation>().Reverse()) b.DropColumn(c.Name, c.Table);
            if (entry.Id == CandidateContract.AssistantScopeMigration)
            {
                b.DropForeignKey("FK_ReceptionistCostReservations_ReceptionistCalls_CallId_OwnerBudgetAccountId_OwnerBudgetPeriodId", "ReceptionistCostReservations");
                b.DropIndex("IX_ReceptionistCostReservations_CallId_OwnerBudgetAccountId_OwnerBudgetPeriodId", "ReceptionistCostReservations");
                b.AlterColumn<Guid>("CallId", "ReceptionistCostReservations", type: "uniqueidentifier", nullable: false);
                b.CreateIndex("IX_ReceptionistCostReservations_CallId_OwnerBudgetAccountId_OwnerBudgetPeriodId", "ReceptionistCostReservations", ["CallId", "OwnerBudgetAccountId", "OwnerBudgetPeriodId"]);
                b.AddForeignKey("FK_ReceptionistCostReservations_ReceptionistCalls_CallId_OwnerBudgetAccountId_OwnerBudgetPeriodId",
                    "ReceptionistCostReservations", ["CallId", "OwnerBudgetAccountId", "OwnerBudgetPeriodId"],
                    "ReceptionistCalls", principalColumns: ["Id", "OwnerBudgetAccountId", "OwnerBudgetPeriodId"], onDelete: ReferentialAction.Restrict);
                // Explicit historical unfiltered index, as in the browser fixture;
                // a standalone generator lacks the historical model/version metadata.
                b.Sql("CREATE UNIQUE INDEX IX_ReceptionistCostReservations_CallId_ComponentKey ON dbo.ReceptionistCostReservations(CallId,ComponentKey)");
            }
            else b.AddCheckConstraint("CK_ReceptionistUsageReceipt_Target", "ReceptionistUsageReceipts",
                "([CallId] IS NOT NULL AND [AccountChargeId] IS NULL) OR ([CallId] IS NULL AND [AccountChargeId] IS NOT NULL)");
            foreach (var command in db.GetService<IMigrationsSqlGenerator>().Generate(b.Operations))
                await db.Database.ExecuteSqlRawAsync(command.CommandText, ct);
        }
        if (!pending.Any(m => m.Id == CandidateContract.AssistantScopeMigration))
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE dbo.MarketingConnections ADD CONSTRAINT DF_Rehearsal_AssistantControlRevision DEFAULT ('00000000-0000-0000-0000-000000000000') FOR AssistantControlRevision", ct);
    }

    private static async Task SeedAssistantLegacy(MasterAppDbContext db, CancellationToken ct)
    {
        await db.Database.ExecuteSqlRawAsync("""
            INSERT dbo.ReceptionistCostReservations(Id,CallId,CashFinal,CashObserved,CashReserved,ComponentKey,
                ConsumptionFinal,ConsumptionObserved,ConsumptionReserved,FundingBudgetAccountId,FundingBudgetPeriodId,
                FundingControlRevision,OwnerBudgetAccountId,OwnerBudgetPeriodId,Revision)
            VALUES('00000000-0000-0000-0000-000000000601','00000000-0000-0000-0000-000000000501',0,0.000001,1.234567,'legacy-call',
                0,0.000002,2.345678,'00000000-0000-0000-0000-000000000202','00000000-0000-0000-0000-000000000402',
                '00000000-0000-0000-0000-000000000901','00000000-0000-0000-0000-000000000201','00000000-0000-0000-0000-000000000401',
                '00000000-0000-0000-0000-000000000902');
            INSERT dbo.ReceptionistAccountCharges(Id,CashFinal,CashObserved,ComponentKey,ConsumptionFinal,ConsumptionObserved,
                FundingBudgetAccountId,OwnerBudgetAccountId,ProviderChargeKey,Revision)
            VALUES('00000000-0000-0000-0000-000000000701',0,0,'legacy-charge',0,0,
                '00000000-0000-0000-0000-000000000202','00000000-0000-0000-0000-000000000201','synthetic-charge',
                '00000000-0000-0000-0000-000000000903');
            INSERT dbo.ReceptionistUsageReceipts(Id,CallId,AccountChargeId,ComponentKey,PayloadSha256,ProviderReceiptKey,RecordedUtc,ScopeKey)
            VALUES('00000000-0000-0000-0000-000000000711','00000000-0000-0000-0000-000000000501',NULL,'legacy-call',REPLICATE('a',64),'synthetic-call','20260101','synthetic'),
                ('00000000-0000-0000-0000-000000000712',NULL,'00000000-0000-0000-0000-000000000701','legacy-charge',REPLICATE('b',64),'synthetic-charge','20260101','synthetic');
            """, ct);
    }

    private static IEnumerable<string> AssistantDrift() => new[] {
        "ALTER TABLE dbo.ReceptionistCostReservations NOCHECK CONSTRAINT CK_ReceptionistCostReservation_Operation",
        "ALTER TABLE dbo.ReceptionistCostReservations NOCHECK CONSTRAINT FK_ReceptionistCostReservations_ReceptionistBudgetPeriods_OwnerBudgetAccountId_OwnerBudgetPeriodId",
        "ALTER TABLE dbo.ReceptionistUsageReceipts NOCHECK CONSTRAINT CK_ReceptionistUsageReceipt_Target",
        "ALTER TABLE dbo.ReceptionistUsageReceipts NOCHECK CONSTRAINT FK_ReceptionistUsageReceipts_ReceptionistCostReservations_AssistantReservationId",
        "ALTER INDEX IX_ReceptionistCostReservations_OwnerBudgetAccountId_AssistantOperationId_ComponentKey ON dbo.ReceptionistCostReservations DISABLE",
        "DROP INDEX IX_ReceptionistCostReservations_CallId_ComponentKey ON dbo.ReceptionistCostReservations; CREATE UNIQUE INDEX IX_ReceptionistCostReservations_CallId_ComponentKey ON dbo.ReceptionistCostReservations(CallId,ComponentKey)",
        "ALTER TABLE dbo.MessageConversations ALTER COLUMN AssistantProvider nvarchar(40) NULL"
    };

    private static async Task VerifyAssistantData(MasterAppDbContext db, bool legacySeeded, CancellationToken ct)
    {
        var conversation = await db.MessageConversations.AsNoTracking().SingleAsync(x => x.Id == AssistantConversationId, ct);
        if (conversation.Subject != "retained synthetic conversation" || conversation.CreatedByUserId != "synthetic-assistant-owner" ||
            conversation.AssistantWorkspaceKey is not null || conversation.AssistantProvider is not null || conversation.AssistantConnectionId is not null ||
            conversation.AssistantConfigurationRevision is not null)
            throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
        if (!legacySeeded) await SeedAssistantLegacy(db, ct);
        var retained = await db.Database.SqlQueryRaw<int>("""
            SELECT COUNT(*) AS Value FROM dbo.ReceptionistCostReservations WHERE Id='00000000-0000-0000-0000-000000000601'
                AND CallId='00000000-0000-0000-0000-000000000501' AND CashReserved=1.234567 AND ConsumptionReserved=2.345678
                AND CashObserved=0.000001 AND ConsumptionObserved=0.000002 AND CashFinal=0 AND ConsumptionFinal=0
                AND ComponentKey='legacy-call' AND AssistantOperationId IS NULL AND FeatureKey IS NULL AND OperationFingerprint IS NULL
                AND AssistantBindingJson IS NULL AND OwnerBudgetAccountId='00000000-0000-0000-0000-000000000201'
                AND OwnerBudgetPeriodId='00000000-0000-0000-0000-000000000401'
                AND FundingBudgetAccountId='00000000-0000-0000-0000-000000000202'
                AND FundingBudgetPeriodId='00000000-0000-0000-0000-000000000402'
                AND FundingControlRevision='00000000-0000-0000-0000-000000000901' AND Revision='00000000-0000-0000-0000-000000000902'
            """).SingleAsync(ct);
        var receipts = await db.Database.SqlQueryRaw<int>("""
            SELECT COUNT(*) AS Value FROM dbo.ReceptionistUsageReceipts WHERE AssistantReservationId IS NULL AND RecordedUtc='20260101' AND ScopeKey='synthetic' AND
            ((Id='00000000-0000-0000-0000-000000000711' AND CallId='00000000-0000-0000-0000-000000000501' AND AccountChargeId IS NULL AND ComponentKey='legacy-call' AND PayloadSha256=REPLICATE('a',64) AND ProviderReceiptKey='synthetic-call') OR
            (Id='00000000-0000-0000-0000-000000000712' AND CallId IS NULL AND AccountChargeId='00000000-0000-0000-0000-000000000701' AND ComponentKey='legacy-charge' AND PayloadSha256=REPLICATE('b',64) AND ProviderReceiptKey='synthetic-charge'))
            """).SingleAsync(ct);
        if (retained != 1 || receipts != 2) throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("""
            INSERT dbo.ReceptionistCostReservations(Id,CallId,AssistantOperationId,FeatureKey,OperationFingerprint,CashFinal,CashObserved,CashReserved,
                ComponentKey,ConsumptionFinal,ConsumptionObserved,ConsumptionReserved,FundingBudgetAccountId,FundingBudgetPeriodId,
                FundingControlRevision,OwnerBudgetAccountId,OwnerBudgetPeriodId,Revision,AssistantBindingJson)
            SELECT '00000000-0000-0000-0000-000000000602',NULL,'00000000-0000-0000-0000-000000000603','business-assistant',REPLICATE('c',64),
                CashFinal,CashObserved,CashReserved,'assistant',ConsumptionFinal,ConsumptionObserved,ConsumptionReserved,FundingBudgetAccountId,
                FundingBudgetPeriodId,FundingControlRevision,OwnerBudgetAccountId,OwnerBudgetPeriodId,Revision,'{{"synthetic":true}}'
            FROM dbo.ReceptionistCostReservations WHERE Id='00000000-0000-0000-0000-000000000601';
            INSERT dbo.ReceptionistUsageReceipts(Id,AssistantReservationId,ComponentKey,PayloadSha256,ProviderReceiptKey,RecordedUtc,ScopeKey)
            VALUES('00000000-0000-0000-0000-000000000713','00000000-0000-0000-0000-000000000602','assistant',REPLICATE('c',64),'synthetic-assistant','20260101','synthetic');
            INSERT dbo.ReceptionistCostReservations(Id,CallId,AssistantOperationId,FeatureKey,OperationFingerprint,CashFinal,CashObserved,CashReserved,
                ComponentKey,ConsumptionFinal,ConsumptionObserved,ConsumptionReserved,FundingBudgetAccountId,FundingBudgetPeriodId,
                FundingControlRevision,OwnerBudgetAccountId,OwnerBudgetPeriodId,Revision)
            SELECT '00000000-0000-0000-0000-000000000604',NULL,AssistantOperationId,FeatureKey,OperationFingerprint,CashFinal,CashObserved,CashReserved,
                ComponentKey,ConsumptionFinal,ConsumptionObserved,ConsumptionReserved,OwnerBudgetAccountId,OwnerBudgetPeriodId,
                FundingControlRevision,FundingBudgetAccountId,FundingBudgetPeriodId,Revision
            FROM dbo.ReceptionistCostReservations WHERE Id='00000000-0000-0000-0000-000000000602';
            """, ct);
        foreach (var (mutation, constraint) in new (string, string)[] {
            ("UPDATE dbo.ReceptionistCostReservations SET CallId='00000000-0000-0000-0000-000000000501' WHERE Id='00000000-0000-0000-0000-000000000602'", "CK_ReceptionistCostReservation_Operation"),
            ("UPDATE dbo.ReceptionistCostReservations SET AssistantOperationId=NULL WHERE Id='00000000-0000-0000-0000-000000000602'", "CK_ReceptionistCostReservation_Operation"),
            ("UPDATE dbo.ReceptionistCostReservations SET FeatureKey='other' WHERE Id='00000000-0000-0000-0000-000000000602'", "CK_ReceptionistCostReservation_Operation"),
            ("UPDATE dbo.ReceptionistCostReservations SET OperationFingerprint=NULL WHERE Id='00000000-0000-0000-0000-000000000602'", "CK_ReceptionistCostReservation_Operation"),
            ("UPDATE dbo.ReceptionistCostReservations SET OwnerBudgetPeriodId='00000000-0000-0000-0000-000000000402' WHERE Id='00000000-0000-0000-0000-000000000602'", "FK_ReceptionistCostReservations_ReceptionistBudgetPeriods_OwnerBudgetAccountId_OwnerBudgetPeriodId"),
            ("UPDATE dbo.ReceptionistUsageReceipts SET CallId='00000000-0000-0000-0000-000000000501' WHERE Id='00000000-0000-0000-0000-000000000713'", "CK_ReceptionistUsageReceipt_Target"),
            ("UPDATE dbo.ReceptionistUsageReceipts SET AssistantReservationId=NULL WHERE Id='00000000-0000-0000-0000-000000000713'", "CK_ReceptionistUsageReceipt_Target"),
            ("UPDATE dbo.ReceptionistUsageReceipts SET AssistantReservationId=NEWID() WHERE Id='00000000-0000-0000-0000-000000000713'", "FK_ReceptionistUsageReceipts_ReceptionistCostReservations_AssistantReservationId"),
        })
        {
            await transaction.CreateSavepointAsync("assistant_constraint", ct);
            var rejected = false;
            try { await db.Database.ExecuteSqlRawAsync(mutation, ct); }
            catch (SqlException error) when (error.Number is 547 or 2601 or 2627 && error.Message.Contains(constraint, StringComparison.Ordinal)) { rejected = true; }
            finally { await transaction.RollbackToSavepointAsync("assistant_constraint", ct); }
            if (!rejected) throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
        }
        // Legacy writers can still omit every assistant field.
        const string legacyColumns = "CallId,CashFinal,CashObserved,CashReserved,ComponentKey,ConsumptionFinal,ConsumptionObserved,ConsumptionReserved,FundingBudgetAccountId,FundingBudgetPeriodId,FundingControlRevision,OwnerBudgetAccountId,OwnerBudgetPeriodId,Revision";
        var clone = "INSERT dbo.ReceptionistCostReservations(Id," + legacyColumns + ") SELECT NEWID()," + legacyColumns + " FROM dbo.ReceptionistCostReservations WHERE Id='00000000-0000-0000-0000-000000000601'";
        await transaction.CreateSavepointAsync("assistant_duplicate", ct);
        var duplicateRejected = false;
        try { await db.Database.ExecuteSqlRawAsync(clone, ct); }
        catch (SqlException e) when (e.Number is 2601 or 2627 && e.Message.Contains("IX_ReceptionistCostReservations_CallId_ComponentKey", StringComparison.Ordinal)) { duplicateRejected = true; }
        finally { await transaction.RollbackToSavepointAsync("assistant_duplicate", ct); }
        if (!duplicateRejected) throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
        await db.Database.ExecuteSqlRawAsync(clone.Replace("SELECT NEWID()," + legacyColumns,
            "SELECT NEWID()," + legacyColumns.Replace("ComponentKey", "'legacy-new-component'")), ct);
        await transaction.CreateSavepointAsync("assistant_duplicate", ct);
        duplicateRejected = false;
        try { await db.Database.ExecuteSqlRawAsync("UPDATE dbo.ReceptionistCostReservations SET CallId=NULL,AssistantOperationId='00000000-0000-0000-0000-000000000603',FeatureKey='business-assistant',OperationFingerprint=REPLICATE('c',64),ComponentKey='assistant' WHERE Id='00000000-0000-0000-0000-000000000601'", ct); }
        catch (SqlException e) when (e.Number is 2601 or 2627 && e.Message.Contains("IX_ReceptionistCostReservations_OwnerBudgetAccountId_AssistantOperationId_ComponentKey", StringComparison.Ordinal)) { duplicateRejected = true; }
        finally { await transaction.RollbackToSavepointAsync("assistant_duplicate", ct); }
        if (!duplicateRejected) throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
        foreach (var id in CandidateContract.AssistantMigrations)
        {
            var assembly = db.GetService<IMigrationsAssembly>();
            var guard = assembly.CreateMigration(assembly.Migrations[id], db.Database.ProviderName!).DownOperations.First() as SqlOperation
                ?? throw new ProbeObservationFailure("MIGRATION_COMPATIBILITY_UNPROVEN");
            if (guard.Sql != CandidateContract.AssistantDownGuard(id) || guard.SuppressTransaction || guard.GetAnnotations().Any())
                throw new ProbeObservationFailure("MIGRATION_COMPATIBILITY_UNPROVEN");
            await transaction.CreateSavepointAsync("assistant_down", ct);
            var blocked = false;
            try { await db.Database.ExecuteSqlRawAsync(CandidateContract.AssistantDownGuard(id), ct); }
            catch (SqlException e) when (e.Number == 51000) { blocked = true; }
            finally { await transaction.RollbackToSavepointAsync("assistant_down", ct); }
            if (!blocked) throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
        }
        await transaction.RollbackAsync(ct);
    }
}
