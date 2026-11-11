using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

internal static partial class CandidateContractTests
{
    private static void AssistantContractTests(MasterAppDbContext db, IMigrationsAssembly assembly)
    {
        var present = CandidateContract.AssistantMigrations.Where(assembly.Migrations.ContainsKey).ToArray();
        Check(present.SequenceEqual(CandidateContract.AssistantMigrations.Take(present.Length)), "assistant migration prefix");
        if (present.Length == 0) return;
        foreach (var id in present)
        {
            IReadOnlyList<MigrationOperation> Actual() => assembly.CreateMigration(assembly.Migrations[id], db.Database.ProviderName!).UpOperations;
            var operations = Actual();
            Check(CandidateContract.ExactAssistantOperations(id, operations), "exact assistant operations");
            Check(!CandidateContract.ExactAssistantOperations(id, operations.Reverse().ToArray()), "assistant order drift");
            Check(!CandidateContract.ExactAssistantOperations(id, operations.Skip(1).ToArray()), "assistant missing operation");
            Check(!CandidateContract.ExactAssistantOperations(id, operations.Concat(operations.Take(1)).ToArray()), "assistant extra operation");
            foreach (var mutation in new Action<IReadOnlyList<MigrationOperation>>[] {
                x => x[0].AddAnnotation("Unreviewed", true),
                x => x.OfType<AddColumnOperation>().First().IsNullable = false,
                x => x.OfType<AddColumnOperation>().First().ColumnType = "nvarchar(40)",
                x => x.OfType<AddColumnOperation>().First().DefaultValue = "unexpected",
                x => x.OfType<CreateIndexOperation>().First().Columns = ["Unknown"],
                x => x.OfType<CreateIndexOperation>().First().Filter = "[Unknown] IS NOT NULL",
                x => x.OfType<AddForeignKeyOperation>().First().OnDelete = ReferentialAction.Cascade,
                x => x.OfType<AddForeignKeyOperation>().First().PrincipalColumns = ["Unknown"],
                x => x.OfType<AddCheckConstraintOperation>().First().Sql = "1=1" })
            {
                var changed = Actual(); mutation(changed);
                Check(!CandidateContract.ExactAssistantOperations(id, changed), "assistant metadata drift rejected");
                Check(!CandidateContract.Describe(id, changed).Supported, "assistant drift cannot become supported");
            }
        }
        var altered = assembly.CreateMigration(assembly.Migrations[CandidateContract.AssistantScopeMigration], db.Database.ProviderName!).UpOperations;
        altered.OfType<AlterColumnOperation>().Single().OldColumn.IsNullable = true;
        Check(!CandidateContract.ExactAssistantOperations(CandidateContract.AssistantScopeMigration, altered), "assistant predecessor nullability drift");
        var contract = CandidateContract.Current(db);
        var entries = contract.Migrations.Where(m => m.Id is RehearsalFixture.ReceptionistMigration or CandidateContract.BrowserMigration || CandidateContract.IsAssistantMigration(m.Id)).ToArray();
        var predecessors = contract.Migrations.Where(m => !CandidateContract.IsAssistantMigration(m.Id)).Select(m => m.Id).ToArray();
        foreach (var applied in new[] { predecessors, predecessors.Append(CandidateContract.AssistantScopeMigration).ToArray(), contract.Migrations.Select(m => m.Id).ToArray() })
            CandidateContract.Plan(entries, applied, CancellationToken.None);
        if (present.Contains(CandidateContract.AssistantSettlementMigration)) try {
            CandidateContract.Plan(entries, predecessors.Append(CandidateContract.AssistantSettlementMigration).ToArray(), CancellationToken.None);
            throw new Exception("Assistant noncontiguous history accepted");
        } catch (ProbeObservationFailure error) when (error.Message == "MIGRATION_COMPATIBILITY_UNPROVEN") { }
        foreach (var entry in entries.Where(m => CandidateContract.IsAssistantMigration(m.Id)))
        {
            var bad = entry with { Columns = entry.Columns.Reverse().ToArray() };
            Check(!CandidateContract.ExactAssistantEntry(bad), "assistant descriptor drift");
        }
        Console.WriteLine("LEGEND_ASSISTANT_MIGRATION_CONTRACT:PASS");
    }
}
