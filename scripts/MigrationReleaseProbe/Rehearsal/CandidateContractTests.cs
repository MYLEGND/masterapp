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
            if (assembly.Migrations.TryGetValue(CandidateContract.TrainingMigration, out var training))
                Training(assembly.CreateMigration(training, db.Database.ProviderName!).UpOperations);
            if (assembly.Migrations.TryGetValue(RehearsalFixture.ManagementMigration, out var management))
                Management(assembly.CreateMigration(management, db.Database.ProviderName!).UpOperations, path);
            if (assembly.Migrations.TryGetValue(CandidateContract.BrowserMigration, out var browser))
                Browser(assembly.CreateMigration(browser, db.Database.ProviderName!).UpOperations, path, CandidateContract.Current(db));
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
    private static void Browser(IReadOnlyList<MigrationOperation> operations, string path, CandidateContract.Contract contract)
    {
        var id = CandidateContract.BrowserMigration;
        var entry = CandidateContract.Describe(id, operations);
        Check(entry.Supported && entry.Columns.Length == 2 && entry.Indexes.Length == 2 &&
            entry.Guards.SequenceEqual(new[] { CandidateContract.BrowserTransition }), "exact seven-operation browser transition");
        File.WriteAllText(path, JsonSerializer.Serialize(new CandidateContract.Contract(3, [entry]), CandidateContract.Json));
        Check(CandidateContract.Read(path).Migrations.Single().Supported, "browser roundtrip");
        for (var i = 0; i < operations.Count; i++)
        {
            Check(!CandidateContract.Describe(id, operations.Where((_, n) => n != i).ToArray()).Supported, "omitted operation rejected");
            var changed = operations.ToArray();
            (changed[i], changed[(i + 1) % changed.Length]) = (changed[(i + 1) % changed.Length], changed[i]);
            Check(!CandidateContract.Describe(id, changed).Supported, "reordered operations rejected");
            operations[i].AddAnnotation("Unreviewed", true);
            Check(!CandidateContract.Describe(id, operations).Supported, "annotated operation rejected");
            operations[i].RemoveAnnotation("Unreviewed");
        }
        Check(!CandidateContract.Describe(id, [..operations, operations[0]]).Supported, "appended operation rejected");
        var alter = operations.OfType<AlterColumnOperation>().Single();
        alter.OldColumn.IsNullable = true;
        Check(!CandidateContract.Describe(id, operations).Supported, "old column drift rejected");
        alter.OldColumn.IsNullable = false;
        alter.OldColumn.AddAnnotation("Unreviewed", true);
        Check(!CandidateContract.Describe(id, operations).Supported, "old column annotations rejected");
        alter.OldColumn.RemoveAnnotation("Unreviewed");
        alter.MaxLength = 199;
        Check(!CandidateContract.Describe(id, operations).Supported, "new metadata drift rejected");
        alter.MaxLength = 200;
        var check = operations.OfType<AddCheckConstraintOperation>().Single();
        var original = check.Sql; check.Sql = original.Replace("64", "63");
        Check(!CandidateContract.Describe(id, operations).Supported, "check drift rejected"); check.Sql = original;
        Check(CandidateContract.Describe(id, operations).Supported, "mutations restored");
        var source = contract.Migrations.Single(m => m.Id == RehearsalFixture.ReceptionistMigration);
        var originalJson = JsonSerializer.Serialize(source, CandidateContract.Json);
        var pending = CandidateContract.Plan([source, entry], [], CancellationToken.None);
        Check(!pending.Commands.Any(c => c.Parameters.Any(p => p.Name == "@table" && Equals(p.Value, "ReceptionistCalls")) &&
            c.Kind != CandidateContract.CatalogKind.Table), "absent coordination table has no child queries");
        var before = CandidateContract.Plan([source, entry], [source.Id], CancellationToken.None);
        var after = CandidateContract.Plan([source, entry], [source.Id, entry.Id], CancellationToken.None);
        Check(before.Commands.Count > 0 && after.Commands.Count > 0, "both applied schema states planned");
        Check(originalJson == JsonSerializer.Serialize(source, CandidateContract.Json), "predecessor serialization unchanged");
        try { CandidateContract.Plan([source, entry], [entry.Id], CancellationToken.None); throw new Exception("Invalid browser history accepted"); }
        catch (ProbeObservationFailure) { }
        var columns = source.Tables.Single(t => t.Name == "ReceptionistCalls").Columns.Concat(entry.Columns).ToArray();
        Same(CandidateContract.BrowserCheckSql, CandidateContract.BrowserCheckSql.Replace("LEN([BrowserOfferSha256]) = 64", "len([BrowserOfferSha256])=(64)"), columns);
        foreach (var invalid in new[] { "LEN([State], 1)=64", "LEN('literal')=64", "LEN([BrowserCreationId])=64",
            "LEN([Missing])=64", "LEN(LEN([State]))=64", "DATALENGTH([State])=64" })
            Check(CandidateContract.CheckCanonical(invalid, columns) is null, "bounded string LEN only");
        Check(CandidateContract.CheckCanonical("LEN([State])=64", columns) !=
            CandidateContract.CheckCanonical("LEN([BrowserOfferSha256])=64", columns), "LEN retains column identity");
        BrowserCatalog(source, entry, before, after);
        foreach (var bad in new[] { entry with { Guards = [] }, entry with { Indexes = [] },
            entry with { Columns = [entry.Columns[0] with { Nullable = false }, entry.Columns[1]] } })
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new CandidateContract.Contract(3, [bad]), CandidateContract.Json));
            try { CandidateContract.Read(path); throw new Exception("Mutated browser descriptor accepted"); }
            catch (InvalidOperationException) { }
        }
        try { CandidateContract.Plan([source with { Columns = [] }, entry], [source.Id, entry.Id], CancellationToken.None);
            throw new Exception("Mutated predecessor accepted"); } catch (ProbeObservationFailure) { }
        Console.WriteLine("BROWSER_CONTRACT:PROVEN:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(entry, CandidateContract.Json))));
    }

    private static void BrowserCatalog(CandidateContract.Entry source, CandidateContract.Entry browser,
        CandidateContract.CatalogBatch before, CandidateContract.CatalogBatch after)
    {
        var original = source.Tables.Single(t => t.Name == "ReceptionistCalls");
        const string providerIndex = "IX_ReceptionistCalls_Provider_ProviderProjectKey_ProviderSessionId";
        foreach (var applied in new[] { false, true })
        {
            var columns = original.Columns.Select(c => c.Name == "ProviderSessionId" ? c with { Nullable = applied } : c)
                .Concat(applied ? browser.Columns : []).ToArray();
            var snapshots = new Dictionary<CandidateContract.CatalogKind, System.Data.DataTable>();
            foreach (var kind in new[] { CandidateContract.CatalogKind.Columns, CandidateContract.CatalogKind.Index, CandidateContract.CatalogKind.Checks })
            {
                var data = new System.Data.DataTable();
                foreach (var type in CandidateContract.CatalogBatch.Types(kind)) data.Columns.Add("c" + data.Columns.Count, type);
                snapshots.Add(kind, data);
            }
            var columnData = snapshots[CandidateContract.CatalogKind.Columns];
            foreach (var c in columns)
            {
                var type = c.Type.Split('(')[0];
                var length = type == "nvarchar" ? (c.Type.Contains("max") ? -1 : int.Parse(c.Type.Split('(')[1].TrimEnd(')')) * 2) : 16;
                columnData.Rows.Add("dbo", original.Name, c.Name, type, (short)length, c.Nullable, DBNull.Value,
                    (byte)(type == "datetime2" ? 7 : c.Scale ?? 0), false, false, (byte)(c.Precision ?? 0), (object?)c.Collation ?? DBNull.Value);
            }
            var indexData = snapshots[CandidateContract.CatalogKind.Index];
            foreach (var name in new[] { "Provider", "ProviderProjectKey", "ProviderSessionId" })
                indexData.Rows.Add("dbo", original.Name, providerIndex, name, true, false,
                    applied ? "([ProviderSessionId] IS NOT NULL)" : DBNull.Value, false, false, false, false, false);
            if (applied) indexData.Rows.Add("dbo", original.Name, "IX_ReceptionistCalls_BrowserCreationId", "BrowserCreationId", true, false,
                "([BrowserCreationId] IS NOT NULL)", false, false, false, false, false);
            var checkData = snapshots[CandidateContract.CatalogKind.Checks];
            foreach (var c in original.Checks.Concat(applied ? new[] { new CandidateContract.CheckConstraint("CK_ReceptionistCall_BrowserCreation", CandidateContract.BrowserCheckSql) } : []))
                checkData.Rows.Add("dbo", original.Name, c.Name, c.Sql, false, false, false);
            var commands = (applied ? after : before).Commands.Where(c => snapshots.ContainsKey(c.Kind ?? CandidateContract.CatalogKind.Table) &&
                c.Parameters.Any(p => p.Name == "@table" && Equals(p.Value, original.Name)) &&
                (c.Kind != CandidateContract.CatalogKind.Index || c.Parameters.Any(p => p.Name == "@name" &&
                    (Equals(p.Value, providerIndex) || Equals(p.Value, "IX_ReceptionistCalls_BrowserCreationId"))))).ToArray();
            void Verify()
            {
                foreach (var command in commands)
                {
                    using var selected = CandidateContract.CatalogBatch.Select(snapshots[command.Kind!.Value], command, CancellationToken.None);
                    using var reader = selected.CreateDataReader();
                    CandidateContract.CatalogBatch.VerifyResults(reader, [command.Validate], CancellationToken.None).GetAwaiter().GetResult();
                }
            }
            void Drift(System.Data.DataRow row, int field, object value)
            {
                var saved = row[field]; row[field] = value;
                try { Verify(); throw new Exception("Browser catalog drift accepted"); }
                catch (ProbeObservationFailure) { }
                finally { row[field] = saved; }
            }
            Verify();
            Drift(columnData.Rows.Cast<System.Data.DataRow>().Single(r => Equals(r[2], "ProviderSessionId")), 5, !applied);
            Drift(indexData.Rows[0], 6, applied ? DBNull.Value : "([ProviderSessionId] IS NOT NULL)");
            foreach (var field in new[] { 4, 5, 6 }) Drift(checkData.Rows[0], field, true);
            if (applied)
            {
                Drift(columnData.Rows.Cast<System.Data.DataRow>().Single(r => Equals(r[2], "BrowserOfferSha256")), 4, (short)126);
                Drift(checkData.Rows[checkData.Rows.Count - 1], 3, CandidateContract.BrowserCheckSql.Replace("64", "63"));
                Drift(checkData.Rows[checkData.Rows.Count - 1], 2, "UnexpectedCheck");
                Drift(indexData.Rows[indexData.Rows.Count - 1], 4, false);
            }
            else
            {
                checkData.Rows.Add("dbo", original.Name, "CK_ReceptionistCall_BrowserCreation", CandidateContract.BrowserCheckSql, false, false, false);
                try { Verify(); throw new Exception("Premature browser check accepted"); } catch (ProbeObservationFailure) { }
            }
            foreach (var data in snapshots.Values) data.Dispose();
        }
        Console.WriteLine("BROWSER_CATALOG_STATES_AND_DRIFT:PASS");
    }

    private static void Management(IReadOnlyList<MigrationOperation> operations, string path)
    {
        var entry = CandidateContract.Describe(RehearsalFixture.ManagementMigration, operations);
        Check(entry.Supported && RehearsalFixture.IsManagementFixture(entry), "exact receptionist management shape");
        Check(entry.Columns.Length == 0 && entry.Tables.Length == 3 && entry.Indexes.Length == 5 &&
            entry.ForeignKeys.Length == 0 && entry.Guards.Length == 0, "additive management tables only");
        File.WriteAllText(path, JsonSerializer.Serialize(new CandidateContract.Contract(3, [entry]), CandidateContract.Json));
        Check(RehearsalFixture.IsManagementFixture(CandidateContract.Read(path).Migrations.Single()), "management shape roundtrip");
        var table = entry.Tables[0];
        foreach (var altered in new[]
        {
            entry with { Id = "29990101000000_Unreviewed" }, entry with { Tables = entry.Tables[..^1] },
            entry with { Tables = [table with { ForeignKeys = [] }, ..entry.Tables.Skip(1)] },
            entry with { Tables = [table with { Checks = [] }, ..entry.Tables.Skip(1)] },
            entry with { Tables = [table with { Columns = table.Columns[..^1] }, ..entry.Tables.Skip(1)] }
        }) Check(!RehearsalFixture.IsManagementFixture(altered), "altered management contract rejected");
    }

    private static void Training(IReadOnlyList<MigrationOperation> operations)
    {
        var id = CandidateContract.TrainingMigration;
        var entry = CandidateContract.Describe(id, operations);
        Check(entry.Supported && RehearsalFixture.IsTrainingFixture(entry), "exact training transition");
        Check(entry.Columns.Length == 10 && entry.Indexes.Length == 5 && entry.ForeignKeys.Length == 4 &&
            entry.Tables.Length == 0 && entry.Guards.SequenceEqual(new[] { CandidateContract.TrainingIndexTransition }), "complete training shape");
        foreach (var operation in operations)
            Check(!CandidateContract.Describe(id, operations.Where(x => !ReferenceEquals(x, operation)).ToArray()).Supported,
                "training transition cannot omit any operation");
        Check(!CandidateContract.Describe(id, operations.Concat(new MigrationOperation[] {
            new AddColumnOperation { Table = "ClientSubscriptions", Name = "Unreviewed", ColumnType = "int", IsNullable = true }
        }).ToArray()).Supported, "training transition cannot append a column");
        var drop = operations.OfType<DropIndexOperation>().Single();
        var oldName = drop.Name;
        drop.Name = "IX_Unreviewed";
        Check(!CandidateContract.Describe(id, operations).Supported, "unreviewed drop rejected");
        drop.Name = oldName;
        var index = operations.OfType<CreateIndexOperation>().Single(x => x.IsUnique);
        index.IsUnique = false;
        Check(!CandidateContract.Describe(id, operations).Supported, "training uniqueness cannot change");
        index.IsUnique = true;
        var columns = index.Columns;
        index.Columns = columns.Reverse().ToArray();
        Check(!CandidateContract.Describe(id, operations).Supported, "training column order cannot change");
        index.Columns = columns;
        var filter = index.Filter;
        index.Filter = "[Status] <> 'Canceled'";
        Check(!CandidateContract.Describe(id, operations).Supported, "training filter cannot change");
        index.Filter = filter;
        var scope = operations.OfType<AddColumnOperation>().First(x => x.Name == "SubscriptionScope");
        scope.DefaultValue = "Training";
        Check(!CandidateContract.Describe(id, operations).Supported, "training legacy default cannot change");
        scope.DefaultValue = "LegendMembership";
        Check(CandidateContract.Describe(id, operations).Supported, "test mutations restored");
        Console.WriteLine("TRAINING_CONTRACT:PROVEN");
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
