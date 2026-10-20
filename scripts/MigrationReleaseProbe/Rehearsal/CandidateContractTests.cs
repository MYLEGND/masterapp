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
            CatalogSnapshot();
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
        // Golden descriptors derive from the original serial plans and exact
        // contract artifact12286103244. They preserve each validator's kind,
        // order and parameter values while replacing its catalog transport.
        var suffix = contract.Migrations.Where(m => StringComparer.Ordinal.Compare(m.Id,
            "20261007134500_AddFounderAssistantRules") >= 0 && StringComparer.Ordinal.Compare(m.Id,
            RehearsalFixture.ReceptionistMigration) <= 0).ToArray();
        Check(suffix.All(m => m.Supported), "golden suffix is additive");
        void Verify(CandidateContract.Entry[] entries, string[] applied, int count, string digest)
        {
            var plan = CandidateContract.Plan(entries, applied, CancellationToken.None);
            var text = string.Join("\n--next--\n", plan.Commands.Select(c => (c.Kind?.ToString() ?? "Sql:" + c.Sql) + "\n" +
                string.Join("\n", c.Parameters.Select(p => p.Name + "=" + p.Value))));
            var actual = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(text)));
            Check(plan.Commands.Count == count && actual == digest, "snapshot exact ordered validator plan");

        }
        var prefix = suffix.Where(m => m.Id != RehearsalFixture.ReceptionistMigration).ToArray();
        Verify(prefix, prefix.Select(m => m.Id).ToArray(), 60,
            "be430157db19b20042c44db3fe7f5b15fb68286146a7e63838ecbdaa4a75647f");
        if (suffix.Any(m => m.Id == RehearsalFixture.ReceptionistMigration))
        {
            Verify(suffix, prefix.Select(m => m.Id).ToArray(), 77,
                "afe703d8df06b4ccee657cf5ed10cc3b202cfaab6e61c3673d615a498cdb40e9");
            Verify(suffix, suffix.Select(m => m.Id).ToArray(), 179,
                "6f095ed8307650f0814b1fd394ccac921e8e298dc4377ec2f091060baa49ba40");
            Verify(suffix, new[] { "20261007134500_AddFounderAssistantRules" }, 30,
                "3a5f67231a8f4d8f1c1559844a9f17c91c5eec8e2abd9f78984be0c854f1d538");
        }
    }

    private static void CatalogSnapshot()
    {
        // Exercise the production reader projection and unchanged validators,
        // without a SQL connection. Existing disposable SQL tests exercise joins.
        var token = CancellationToken.None;
        void Reject(Action action)
        {
            try { action(); } catch (ProbeObservationFailure) { return; }
            throw new InvalidOperationException("Catalog snapshot drift was accepted");
        }
        System.Data.DataTable Data(CandidateContract.CatalogKind kind, params object[][] rows)
        {
            var table = new System.Data.DataTable();
            foreach (var type in CandidateContract.CatalogBatch.Types(kind)) table.Columns.Add("c" + table.Columns.Count, type);
            foreach (var row in rows) table.Rows.Add(row);
            return table;
        }
        var nullValue = JsonSerializer.SerializeToElement<object?>(null);
        var columns = new[] { "A", "B" }.Select(n => new CandidateContract.Column("dbo", "Synthetic", n, "int", false, nullValue, null, null, null)).ToArray();
        var table = new CandidateContract.Table("dbo", "Synthetic", columns, new("PK_Synthetic", ["A", "B"]), [], [], []);
        var entry = new CandidateContract.Entry("29990101000000_Synthetic", true, [], [table], [], [], []);
        var applied = CandidateContract.Plan([entry], [entry.Id], token);
        var pending = CandidateContract.Plan([entry], [], token);
        var snapshot = new Dictionary<CandidateContract.CatalogKind, System.Data.DataTable>
        {
            [CandidateContract.CatalogKind.Table] = Data(CandidateContract.CatalogKind.Table, ["dbo", "Synthetic", 1]),
            [CandidateContract.CatalogKind.Columns] = Data(CandidateContract.CatalogKind.Columns,
                ["dbo", "Synthetic", "A", "int", (short)4, false, DBNull.Value, (byte)0, false, false, (byte)10, DBNull.Value],
                ["dbo", "Synthetic", "B", "int", (short)4, false, DBNull.Value, (byte)0, false, false, (byte)10, DBNull.Value]),
            [CandidateContract.CatalogKind.Index] = Data(CandidateContract.CatalogKind.Index,
                ["dbo", "Synthetic", "PK_Synthetic", "A", true, true, DBNull.Value, false, false, false, false, false],
                ["dbo", "Synthetic", "PK_Synthetic", "B", true, true, DBNull.Value, false, false, false, false, false]),
            [CandidateContract.CatalogKind.Checks] = Data(CandidateContract.CatalogKind.Checks)
        };
        void Verify(CandidateContract.CatalogBatch.Command command)
        {
            using var selected = CandidateContract.CatalogBatch.Select(snapshot[command.Kind!.Value], command, token);
            using var reader = selected.CreateDataReader();
            CandidateContract.CatalogBatch.VerifyResults(reader, [command.Validate], token).GetAwaiter().GetResult();
        }
        foreach (var command in applied.Commands) Verify(command);
        var index = applied.Commands.Single(c => c.Kind == CandidateContract.CatalogKind.Index);
        var indexData = snapshot[CandidateContract.CatalogKind.Index];
        indexData.Rows[0][3] = "B"; indexData.Rows[1][3] = "A";
        Reject(() => Verify(index));
        indexData.Rows[0][3] = "A"; indexData.Rows[1][3] = "B";
        indexData.Rows[0][2] = "pk_synthetic";
        Reject(() => Verify(index)); // Ordinal name matching deliberately fails closed.
        indexData.Rows[0][2] = "PK_Synthetic";
        var columnCommand = applied.Commands.Single(c => c.Kind == CandidateContract.CatalogKind.Columns);
        var columnData = snapshot[CandidateContract.CatalogKind.Columns];
        columnData.Rows.Add(columnData.Rows[0].ItemArray);
        Reject(() => Verify(columnCommand));
        columnData.Rows.RemoveAt(2);
        columnData.Rows.Clear();
        Reject(() => Verify(columnCommand));
        var presence = snapshot[CandidateContract.CatalogKind.Table];
        presence.Rows[0][2] = 0;
        foreach (var command in pending.Commands) Verify(command);
        Reject(() => Verify(applied.Commands.Single(c => c.Kind == CandidateContract.CatalogKind.Table)));
        presence.Rows[0][2] = 1;
        Reject(() => Verify(pending.Commands.Single()));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try
        {
            using var unused = CandidateContract.CatalogBatch.Select(indexData, index, cancelled.Token);
            throw new InvalidOperationException("Cancelled projection was accepted");
        }
        catch (OperationCanceledException) { }

        void Read(CandidateContract.CatalogKind[] kinds, params System.Data.DataTable[] sets)
        {
            using var reader = new System.Data.DataTableReader(sets);
            var received = new Dictionary<CandidateContract.CatalogKind, System.Data.DataTable>();
            try { CandidateContract.CatalogBatch.ReadResults(reader, kinds, [("dbo", "Synthetic")], received, token).GetAwaiter().GetResult(); }
            finally { foreach (var data in received.Values) data.Dispose(); }
        }
        Read([CandidateContract.CatalogKind.Table], presence);
        Reject(() => Read([CandidateContract.CatalogKind.Table, CandidateContract.CatalogKind.Columns], presence));
        Reject(() => Read([CandidateContract.CatalogKind.Table], presence, columnData));
        using var outOfScope = Data(CandidateContract.CatalogKind.Table, ["dbo", "Other", 1]);
        Reject(() => Read([CandidateContract.CatalogKind.Table], outOfScope));
        using var duplicate = Data(CandidateContract.CatalogKind.Table, ["dbo", "Synthetic", 1], ["dbo", "Synthetic", 1]);
        Reject(() => Read([CandidateContract.CatalogKind.Table], duplicate));
        using var missing = Data(CandidateContract.CatalogKind.Table);
        Reject(() => Read([CandidateContract.CatalogKind.Table], missing));
        using var wrongShape = Data(CandidateContract.CatalogKind.Checks);
        Reject(() => Read([CandidateContract.CatalogKind.Table], wrongShape));
        using var wrongType = new System.Data.DataTable();
        wrongType.Columns.Add("schema", typeof(string)); wrongType.Columns.Add("table", typeof(string)); wrongType.Columns.Add("count", typeof(long));
        Reject(() => Read([CandidateContract.CatalogKind.Table], wrongType));
        foreach (var data in snapshot.Values) data.Dispose();
        Console.WriteLine("CATALOG_SNAPSHOT_PROJECTION_AND_PROTOCOL:PASS");
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
