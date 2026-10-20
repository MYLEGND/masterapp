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
            File.WriteAllText(path, JsonSerializer.Serialize(new CandidateContract.Contract(3, new[] { malformed }), CandidateContract.Json));
            try { CandidateContract.Read(path); throw new Exception("Malformed contract accepted"); }
            catch (InvalidOperationException) { }
            // A tool-only precursor can promote the observer before the feature
            // exists. Any assembly containing the feature must exercise its exact
            // operations and fixture; the SQL job independently applies that same
            // assembly's migrations to a disposable database.
            if (assembly.Migrations.TryGetValue(RehearsalFixture.ReceptionistMigration, out var receptionist))
                Receptionist(assembly.CreateMigration(receptionist, db.Database.ProviderName!).UpOperations, path);
            else Console.WriteLine("RECEPTIONIST_CONTRACT:NOT_PRESENT_IN_ASSEMBLY");
            PredicateBounds();
            DefaultDefinitions();
            CatalogPlans(CandidateContract.Current(db));
            foreach (var staleVersion in new[] { 1, 2 })
            {
                File.WriteAllText(path, JsonSerializer.Serialize(new CandidateContract.Contract(staleVersion, new[] { entry }), CandidateContract.Json));
                try { CandidateContract.Read(path); throw new Exception("Stale contract accepted"); }
                catch (InvalidOperationException) { }
            }
        }
        finally { File.Delete(path); }
        Console.WriteLine("LEGEND_MIGRATION_CONTRACT_TESTS:PASS");
    }
    private static void CatalogPlans(CandidateContract.Contract contract)
    {
        // Golden plans were derived from the original serial observer source and
        // exact contract artifact12286103244 (not an instrumented binary). They bind every SELECT, order and
        // parameter name/value, not merely a command count or new implementation.
        var suffix = contract.Migrations.Where(m => StringComparer.Ordinal.Compare(m.Id,
            "20261007134500_AddFounderAssistantRules") >= 0 && StringComparer.Ordinal.Compare(m.Id,
            RehearsalFixture.ReceptionistMigration) <= 0).ToArray();
        Check(suffix.All(m => m.Supported), "golden suffix is additive");
        void Verify(CandidateContract.Entry[] entries, string[] applied, int count, string digest)
        {
            var plan = CandidateContract.Plan(entries, applied, CancellationToken.None);
            var text = string.Join("\n--next--\n", plan.Commands.Select(c => c.Sql + "\n" +
                string.Join("\n", c.Parameters.Select(p => p.Name + "=" + p.Value))));
            var actual = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(text)));
            Check(plan.Commands.Count == count && actual == digest, "serial/batch exact ordered query plan");
            Check(plan.Commands.Chunk(CandidateContract.CatalogBatch.MaximumCommands).Count() == (count + 31) / 32,
                "bounded batch count");
        }
        var prefix = suffix.Where(m => m.Id != RehearsalFixture.ReceptionistMigration).ToArray();
        Verify(prefix, prefix.Select(m => m.Id).ToArray(), 60,
            "7c073dbf3499cea966223ee5eb7fa86db2241e0d5ad0e8ac4b20e99b5862e452");
        if (suffix.Any(m => m.Id == RehearsalFixture.ReceptionistMigration))
        {
            Verify(suffix, prefix.Select(m => m.Id).ToArray(), 77,
                "0b2abf92677119e023d7d43b4069e271d961d8def5197f133665c4c6e4616d3d");
            Verify(suffix, suffix.Select(m => m.Id).ToArray(), 179,
                "f849352447c5ea74d344066e6eb982efa1b1b66d37a793d6a5abc615d3e4ae9d");
            Verify(suffix, new[] { "20261007134500_AddFounderAssistantRules" }, 30,
                "7e35bf384f013d084c4a4916fb71039130c78b8c97937a8c3cdd26549c9142d7");
        }
    }

    private static void Receptionist(IReadOnlyList<MigrationOperation> operations, string path)
    {
        var entry = CandidateContract.Describe(RehearsalFixture.ReceptionistMigration, operations);
        Check(entry.Supported && entry.Columns.Length == 5 && entry.Tables.Length == 12 &&
            entry.Indexes.Length == 42 && entry.ForeignKeys.Length == 1 && entry.Guards.Length == 0,
            "complete receptionist operations represented");
        Check(entry.Tables.Sum(t => t.UniqueKeys.Length) == 5 && entry.Tables.Sum(t => t.Checks.Length) == 16,
            "all receptionist unique keys and checks represented");
        Check(entry.Columns.Single(c => c.Name == "ReceptionistControlRevision").Default.GetString() == Guid.Empty.ToString(),
            "existing rows receive the explicit empty revision");
        Check(RehearsalFixture.IsReceptionistFixture(entry), "exact reviewed physical rehearsal boundary");
        Check(!RehearsalFixture.IsReceptionistFixture(entry with { Id = "20261020160001_UnknownSuccessor" }),
            "fixture is not a general migration allowlist");
        var changedTable = entry.Tables[0] with { Checks = [] };
        Check(!RehearsalFixture.IsReceptionistFixture(entry with { Tables = entry.Tables.Skip(1).Prepend(changedTable).ToArray() }),
            "same migration id with changed operations needs new fixture review");
        File.WriteAllText(path, JsonSerializer.Serialize(new CandidateContract.Contract(3, [entry]), CandidateContract.Json));
        Check(CandidateContract.Read(path).Migrations.Single().Supported, "receptionist version three roundtrip");
        foreach (var operation in new MigrationOperation[] {
            new AddColumnOperation { Table = "MarketingConnections", Name = "OtherIdentity", ColumnType = "nvarchar(200)",
                IsNullable = true, Collation = "Latin1_General_100_CI_AS" },
            new AddColumnOperation { Table = "MarketingConnections", Name = "OtherRevision", ColumnType = "uniqueidentifier",
                IsNullable = false, DefaultValue = Guid.Parse("11111111-1111-1111-1111-111111111111") },
            new AddForeignKeyOperation { Table = "MarketingConnections", Name = "OtherAuthority", Columns = ["Id"],
                PrincipalTable = "ReceptionistConfigurationVersions", PrincipalColumns = ["Id"] },
            new AddCheckConstraintOperation { Table = "MarketingConnections", Name = "OtherCheck", Sql = "[Id] IS NOT NULL" }
        })
            Check(!CandidateContract.Describe(entry.Id, operations.Append(operation).ToArray()).Supported,
                "unreviewed existing-table constraints and collation refused");
        Check(!CandidateContract.Describe(entry.Id, operations.Where(o =>
            o is not AddColumnOperation { Name: "ReceptionistActiveConfigurationId" }).ToArray()).Supported,
            "existing-table foreign key requires its new nullable column");
        Check(!CandidateContract.Describe(entry.Id, operations.Where(o =>
            o is not AddColumnOperation { Name: "ReceptionistProviderProjectKey" }).ToArray()).Supported,
            "existing-table unique project index requires its new nullable column");
        var moneyTable = entry.Tables.Single(t => t.Name == "ReceptionistBudgetPeriods");
        var cash = moneyTable.Columns.Single(c => c.Name == "CashCommitted");
        foreach (var malformed in new[] {
            entry with { Tables = entry.Tables.Select(t => t == moneyTable ? t with {
                Columns = t.Columns.Select(c => c == cash ? c with { Scale = 5 } : c).ToArray() } : t).ToArray() },
            entry with { Tables = entry.Tables.Select(t => t == moneyTable ? t with {
                Checks = [new("bad", "[Undeclared] >= 0")] } : t).ToArray() },
            entry with { ForeignKeys = [entry.ForeignKeys[0] with {
                Constraint = entry.ForeignKeys[0].Constraint with { PrincipalColumns = ["Id", "MarketingConnectionId"] } }] }
        })
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new CandidateContract.Contract(3, [malformed]), CandidateContract.Json));
            try { CandidateContract.Read(path); throw new Exception("Malformed receptionist contract accepted"); }
            catch (InvalidOperationException) { }
        }
        foreach (var table in entry.Tables)
            foreach (var check in table.Checks)
                Check(CandidateContract.CheckCanonical(check.Sql, table.Columns) is not null, "every actual check parsed");
        var account = entry.Tables.Single(t => t.Name == "ReceptionistBudgetAccounts");
        Same("[Kind] IN ('owner','funding')", "(([Kind]='funding' OR [Kind]='owner'))", account.Columns);
        Same("[MonthKey] % 100 BETWEEN 1 AND 12",
            "(([MonthKey]%(100))<=(12) AND ([MonthKey]%(100))>=(1))", moneyTable.Columns);
        var charges = entry.Tables.Single(t => t.Name == "ReceptionistAccountCharges");
        Same("CAST([CashObserved] AS decimal(20,6)) = 0",
            "(CONVERT([decimal](20,6),[CashObserved],(0))=(0))", charges.Columns);
        Same("CAST([CashObserved] AS decimal(20,6)) = 0", "([CashObserved]=(0))", charges.Columns);
        Check(CandidateContract.CheckCanonical("[Kind]='owner'", account.Columns) !=
            CandidateContract.CheckCanonical("[Kind]='OWNER'", account.Columns), "literal case is not normalized");
        Check(CandidateContract.CheckCanonical("[Kind]='é'", account.Columns) !=
            CandidateContract.CheckCanonical("[Kind]=N'é'", account.Columns), "non-ASCII Unicode prefix retains meaning");
        Console.WriteLine("RECEPTIONIST_CONTRACT:PROVEN");
    }
    private static void DefaultDefinitions()
    {
        var bit = new CandidateContract.Column("dbo", "Synthetic", "Enabled", "bit", false,
            JsonSerializer.SerializeToElement(false), null, null, null);
        foreach (var value in new[] { false, true })
        {
            var column = bit with { Default = JsonSerializer.SerializeToElement(value) };
            var literal = value ? "1" : "0";
            foreach (var definition in new[] { literal, "((" + literal + "))", "(CONVERT([bit],(" + literal + ")))" })
                Check(CandidateContract.DefaultMatches(column, definition), "exact bit default representation accepted");
            foreach (var definition in new[] { value ? "0" : "1", "CONVERT([bit],(" + (value ? "0" : "1") + "))",
                "CONVERT([bit],(2))", "CONVERT([bit],(-1))", "CONVERT([bit],(0+0))", "CONVERT([bit],(0),0)",
                "CONVERT([int],(0))", "CONVERT([bit],('0'))", "CAST(0 AS int)", "(0); SELECT 1", "" })
                Check(!CandidateContract.DefaultMatches(column, definition), "nonliteral or changed bit default rejected");
        }
        Check(!CandidateContract.DefaultMatches(bit with { Type = "int" }, "CONVERT([bit],(0))"),
            "bit conversion does not normalize other column types");
        Check(!CandidateContract.DefaultMatches(bit with { Default = JsonSerializer.SerializeToElement(0) }, "CONVERT([bit],(0))"),
            "bit conversion requires a boolean contract default");
        var text = bit with { Type = "nvarchar(100)", Default = JsonSerializer.SerializeToElement("literal") };
        Check(CandidateContract.DefaultMatches(text, "(N'literal')"), "existing Unicode literal comparison preserved");
        Check(!CandidateContract.DefaultMatches(text, "(N'LITERAL')"), "string default case remains significant");
        var guid = bit with { Type = "uniqueidentifier", Default = JsonSerializer.SerializeToElement(Guid.Empty.ToString()) };
        Check(CandidateContract.DefaultMatches(guid, "('00000000-0000-0000-0000-000000000000')"),
            "existing Guid default comparison preserved");
        var number = bit with { Type = "int", Default = JsonSerializer.SerializeToElement(42) };
        Check(CandidateContract.DefaultMatches(number, "((42))"), "existing numeric default comparison preserved");
    }
    private static void Same(string expected, string actual, CandidateContract.Column[] columns) =>
        Check(CandidateContract.CheckCanonical(expected, columns) is { } left &&
            left == CandidateContract.CheckCanonical(actual, columns), "SQL Server predicate rendering preserves meaning");
    private static void PredicateBounds()
    {
        var columns = new[] { new CandidateContract.Column("dbo", "Synthetic", "A", "int", false,
            JsonSerializer.SerializeToElement<object?>(null), null, null, null) };
        foreach (var sql in new[] { "[Unknown] > 0", "ABS([A]) > 0", "[A] > 0; SELECT 1", "[A] > 0 -- ignored",
            "[A] > 0 OR 1=1 /*comment*/", "CONVERT(decimal(20,6),[A])=0", "[A] % 0 = 0",
            new string('(', 65) + "[A]>0" + new string(')', 65), new string(' ', 8193),
            string.Join(" OR ", Enumerable.Repeat("[A]=0", 700)) })
            Check(CandidateContract.CheckCanonical(sql, columns) is null, "unknown or unbounded predicate refused");
        Check(CandidateContract.CheckCanonical("([A]=1 OR [A]=2) AND [A]=3", columns) !=
            CandidateContract.CheckCanonical("[A]=1 OR ([A]=2 AND [A]=3)", columns), "boolean parentheses retain semantics");
    }
    private static void Check(bool condition, string assertion)
    { if (!condition) { Console.Error.WriteLine("CONTRACT_ASSERTION_FAILED: " + assertion); throw new InvalidOperationException(); } }
}
