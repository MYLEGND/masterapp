using System.Text.Json;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

internal static class CandidateContractTests
{
    internal static void Run()
    {
        // No connection is opened. These are real provider migration operations,
        // not a second handwritten representation of the payment schema.
        using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseSqlServer("Server=127.0.0.1;Database=ContractTests;Integrated Security=true").Options);
        var assembly = db.GetService<IMigrationsAssembly>();
        const string payment = "20261011064911_AddBusinessScopedMerchantPayments";
        var operations = assembly.CreateMigration(assembly.Migrations[payment], db.Database.ProviderName!).UpOperations;
        var entry = CandidateContract.Describe(payment, operations);
        Console.WriteLine($"CONTRACT_SHAPE: supported={entry.Supported}, tables={entry.Tables.Length}, indexes={entry.Indexes.Length}");
        Check(entry.Supported, "payment operation support");
        Check(entry.Columns.Length == 3 && entry.Tables.Length == 8 && entry.Indexes.Length == 15,
            "complete payment schema representation");
        Check(entry.Guards.SequenceEqual(new[] { CandidateContract.CheckoutGuard }), "read-only guard identity");
        Check(!CandidateContract.Describe(payment, operations.Where(o => o is not SqlOperation).ToArray()).Supported,
            "existing-table unique index requires duplicate guard");
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, CandidateContract.Export(db));
            var decoded = CandidateContract.Read(path);
            Check(decoded.Migrations.Single(m => m.Id == payment).Supported, "candidate contract round trip");
            foreach (var unsupported in new MigrationOperation[] {
                new DropTableOperation { Name = "CommerceOrders" },
                new SqlOperation { Sql = "DELETE FROM CommerceOrders" },
                new SqlOperation { Sql = CandidateContract.CheckoutGuardSql, SuppressTransaction = true },
                new AddColumnOperation { Table = "CommerceOrders", Name = "Unsafe", ColumnType = "int", IsNullable = false },
                new CreateIndexOperation { Table = "CommerceOrders", Name = "Unknown", Columns = new[] { "Id" }, IsUnique = true }
            }) Check(!CandidateContract.Describe(payment, operations.Concat(new[] { unsupported }).ToArray()).Supported,
                "unknown or destructive operation rejected");
            var malformed = entry with { Tables = new[] { entry.Tables[0] with {
                PrimaryKey = new CandidateContract.Key("bad", new[] { "MissingColumn" }) } } };
            File.WriteAllText(path, JsonSerializer.Serialize(new CandidateContract.Contract(2, new[] { malformed }), CandidateContract.Json));
            try { CandidateContract.Read(path); throw new Exception("Malformed contract accepted"); }
            catch (InvalidOperationException) { }
            File.WriteAllText(path, JsonSerializer.Serialize(new CandidateContract.Contract(1, new[] { entry }), CandidateContract.Json));
            try { CandidateContract.Read(path); throw new Exception("Stale contract accepted"); }
            catch (InvalidOperationException) { }
        }
        finally { File.Delete(path); }
        Console.WriteLine("LEGEND_MIGRATION_CONTRACT_TESTS:PASS");
    }
    private static void Check(bool condition, string assertion)
    { if (!condition) { Console.Error.WriteLine("CONTRACT_ASSERTION_FAILED: " + assertion); throw new InvalidOperationException(); } }
}
