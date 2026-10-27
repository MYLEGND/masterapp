using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

internal static partial class CandidateContract
{
    internal sealed record Key(string Name, string[] Columns);
    internal sealed record ForeignKey(string Name, string[] Columns, string PrincipalSchema,
        string PrincipalTable, string[] PrincipalColumns);
    internal sealed record TableForeignKey(string Schema, string Table, ForeignKey Constraint);
    internal sealed record CheckConstraint(string Name, string Sql);
    internal sealed record Table(string Schema, string Name, Column[] Columns, Key PrimaryKey,
        ForeignKey[] ForeignKeys, Key[] UniqueKeys, CheckConstraint[] Checks);
    internal sealed record Index(string Schema, string Table, string Name, string[] Columns, bool Unique, string? Filter);
    // Recognize precisely the reviewed read-only precondition. Never serialize or
    // execute arbitrary candidate SQL in the observer that has production access.
    internal const string CheckoutGuard = "commerce-checkout-identity-v1";
    internal const string CheckoutGuardSql = """
        IF EXISTS (SELECT 1 FROM [CommerceOrders] WHERE [CheckoutAttemptId] IS NOT NULL
            GROUP BY [CommerceBusinessId], [CheckoutAttemptId] HAVING COUNT_BIG(*) > 1)
            THROW 51001, 'MERCHANT_CHECKOUT_DUPLICATE_IDENTITY_REQUIRES_REVIEW', 1;
        """;
    private static string Normalize(string value) => Regex.Replace(value.Trim(), @"\s+", " ");
    private static Column DescribeColumn(AddColumnOperation c) => new(c.Schema ?? "dbo", c.Table,
        c.Name, c.ColumnType ?? "", c.IsNullable, JsonSerializer.SerializeToElement(c.DefaultValue),
        c.Precision, c.Scale, c.Collation);
    private static ForeignKey DescribeForeignKey(AddForeignKeyOperation f) => new(f.Name, f.Columns,
        f.PrincipalSchema ?? "dbo", f.PrincipalTable, f.PrincipalColumns ?? []);
    private static bool PlainColumn(AddColumnOperation c, bool newTable) =>
        c.ComputedColumnSql is null && c.DefaultValueSql is null &&
        c.Collation is null or "Latin1_General_100_BIN2" &&
        !c.IsRowVersion && !c.GetAnnotations().Any() &&
        (newTable || c.IsNullable || c.DefaultValue is string or int or long or bool || c.DefaultValue is Guid g && g == Guid.Empty);
    private static bool PlainForeignKey(AddForeignKeyOperation f) => !f.GetAnnotations().Any() &&
        f.OnDelete is ReferentialAction.NoAction or ReferentialAction.Restrict && f.OnUpdate == ReferentialAction.NoAction;

    internal static Entry Describe(string id, IReadOnlyList<MigrationOperation> operations)
    {
        if (id == BrowserMigration) return BrowserEntry(ExactBrowserOperations(operations));
        var columns = operations.OfType<AddColumnOperation>().Select(DescribeColumn).ToArray();
        var tables = operations.OfType<CreateTableOperation>().Select(t => new Table(t.Schema ?? "dbo", t.Name,
            t.Columns.Select(DescribeColumn).ToArray(), new Key(t.PrimaryKey?.Name ?? "", t.PrimaryKey?.Columns ?? []),
            t.ForeignKeys.Select(DescribeForeignKey).ToArray(),
            t.UniqueConstraints.Select(k => new Key(k.Name, k.Columns)).ToArray(),
            t.CheckConstraints.Select(c => new CheckConstraint(c.Name, c.Sql)).ToArray())).ToArray();
        var indexes = operations.OfType<CreateIndexOperation>().Select(i => new Index(i.Schema ?? "dbo",
            i.Table, i.Name, i.Columns, i.IsUnique, i.Filter)).ToArray();
        var foreignKeys = operations.OfType<AddForeignKeyOperation>().Select(f =>
            new TableForeignKey(f.Schema ?? "dbo", f.Table, DescribeForeignKey(f))).ToArray();
        var guards = operations.Where(op => op is SqlOperation or DropIndexOperation).Select(op => op switch {
            SqlOperation sql when !sql.SuppressTransaction && Normalize(sql.Sql) == Normalize(CheckoutGuardSql) => CheckoutGuard,
            DropIndexOperation drop when IsTrainingIndexDrop(id, drop) => TrainingIndexTransition,
            _ => "unknown"
        }).ToArray();
        var supported = operations.Count > 0 && operations.All(op => !op.GetAnnotations().Any() && op switch {
            AddColumnOperation c => PlainColumn(c, false),
            CreateTableOperation t => t.PrimaryKey is not null && !t.PrimaryKey.GetAnnotations().Any() &&
                t.UniqueConstraints.All(k => !k.GetAnnotations().Any()) && t.CheckConstraints.All(c => !c.GetAnnotations().Any()) &&
                t.Columns.All(c => PlainColumn(c, true)) && t.ForeignKeys.All(PlainForeignKey),
            CreateIndexOperation i => i.IsDescending is null || i.IsDescending.All(d => !d),
            AddForeignKeyOperation f => PlainForeignKey(f),
            DropIndexOperation drop => IsTrainingIndexDrop(id, drop),
            SqlOperation s => !s.SuppressTransaction && Normalize(s.Sql) == Normalize(CheckoutGuardSql),
            _ => false
        });
        var entry = new Entry(id, supported, columns, tables, indexes, guards, foreignKeys);
        return entry with { Supported = supported && ValidStructures(entry) };
    }

    private static bool Name(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128 && !value.Any(char.IsControl);
    private static bool Names(string[]? values) => values is { Length: > 0 and <= 128 } &&
        values.All(Name) && values.Distinct(StringComparer.Ordinal).Count() == values.Length;
    private static bool Type(string? value) => value is not null && Regex.IsMatch(value,
        @"^(?:nvarchar|varchar)\((?:max|[1-9][0-9]{0,4})\)$|^(?:int|bigint|bit|uniqueidentifier|datetime2|decimal\(20,6\))$");
    private static bool ValidColumn(Column? c) => c is not null && Name(c.Schema) && Name(c.Table) && Name(c.Name) && Type(c.Type) &&
        (c.Type == "decimal(20,6)" ? c.Precision == 20 && c.Scale == 6 : c.Precision is null && c.Scale is null) &&
        (c.Collation is null || c.Collation == "Latin1_General_100_BIN2" && (c.Type.StartsWith("nvarchar(") || c.Type.StartsWith("varchar("))) &&
        c.Default.ValueKind is JsonValueKind.Null or JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False;
    private static bool ValidForeignKey(ForeignKey? f) => f is not null && Name(f.Name) && Names(f.Columns) &&
        Name(f.PrincipalSchema) && Name(f.PrincipalTable) && Names(f.PrincipalColumns) && f.Columns.Length == f.PrincipalColumns.Length;
    private static bool AddedNullable(Entry m, string name, string type) => m.Columns.Any(c =>
        c.Schema == "dbo" && c.Table == "MarketingConnections" && c.Name == name && c.Type == type &&
        c.Nullable && c.Default.ValueKind == JsonValueKind.Null);
    private static bool MarketingIndex(Entry m, Index i)
    {
        if (i.Schema != "dbo" || i.Table != "MarketingConnections") return false;
        if (i.Name == "IX_MarketingConnections_Id_ReceptionistActiveConfigurationId")
            return !i.Unique && i.Filter is null && i.Columns.SequenceEqual(new[] { "Id", "ReceptionistActiveConfigurationId" }) &&
                AddedNullable(m, "ReceptionistActiveConfigurationId", "uniqueidentifier");
        foreach (var column in new[] { "ReceptionistIngressKey", "ReceptionistProviderProjectKey" })
            if (i.Name == "IX_MarketingConnections_Provider_" + column)
                return i.Unique && i.Columns.SequenceEqual(new[] { "Provider", column }) &&
                    i.Filter == "[" + column + "] IS NOT NULL" && AddedNullable(m, column, "nvarchar(200)") &&
                    m.Columns.Single(c => c.Schema == "dbo" && c.Table == i.Table && c.Name == column).Collation == "Latin1_General_100_BIN2";
        return false;
    }
    private static bool MarketingForeignKey(Entry m, TableForeignKey f)
    {
        var key = f.Constraint;
        var principal = m.Tables.SingleOrDefault(t => t.Schema == "dbo" && t.Name == "ReceptionistConfigurationVersions");
        return f.Schema == "dbo" && f.Table == "MarketingConnections" &&
            key.Name == "FK_MarketingConnections_ReceptionistConfigurationVersions_Id_ReceptionistActiveConfigurationId" &&
            key.Columns.SequenceEqual(new[] { "Id", "ReceptionistActiveConfigurationId" }) &&
            key.PrincipalSchema == "dbo" && key.PrincipalTable == "ReceptionistConfigurationVersions" &&
            key.PrincipalColumns.SequenceEqual(new[] { "MarketingConnectionId", "Id" }) &&
            AddedNullable(m, "ReceptionistActiveConfigurationId", "uniqueidentifier") &&
            principal is not null && principal.UniqueKeys.Any(k => k.Columns.SequenceEqual(key.PrincipalColumns));
    }
    private static bool ValidStructures(Entry m)
    {
        if (m.Tables is null || m.Indexes is null || m.Guards is null || m.ForeignKeys is null ||
            m.Tables.Length > 1000 || m.Indexes.Length > 10000 || m.Guards.Length > 10000 || m.ForeignKeys.Length > 1000) return false;
        if (m.Supported && m.Id == TrainingMigration && Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(m, Json))) !=
            "5a543b02a2a1ae2f2bb35d5e2a8fb51f41e9601b1ef4de2707ec6db51029be10") return false;
        if (m.Id == BrowserMigration && m.Supported) return ExactBrowserEntry(m);
        // Unsupported historical migrations remain opaque and cannot become pending supported operations.
        if (!m.Supported) return true;
        if (m.Guards.Length > 1 || m.Columns.Any(c => !ValidColumn(c)) || m.Guards.Any(g => g != CheckoutGuard && !(g == TrainingIndexTransition && m.Id == TrainingMigration)) ||
            m.Columns.Select(c => (c.Schema, c.Table, c.Name)).Distinct().Count() != m.Columns.Length ||
            m.Tables.Select(t => (t?.Schema, t?.Name)).Distinct().Count() != m.Tables.Length ||
            m.Indexes.Select(i => (i?.Schema, i?.Table, i?.Name)).Distinct().Count() != m.Indexes.Length ||
            m.ForeignKeys.Select(f => (f?.Schema, f?.Table, f?.Constraint?.Name)).Distinct().Count() != m.ForeignKeys.Length) return false;
        foreach (var t in m.Tables)
        {
            if (t is null || !Name(t.Schema) || !Name(t.Name) || t.Columns is not { Length: > 0 and <= 1000 } ||
                t.Columns.Any(c => !ValidColumn(c) || c.Schema != t.Schema || c.Table != t.Name) ||
                t.Columns.Select(c => c.Name).Distinct().Count() != t.Columns.Length ||
                t.PrimaryKey is null || !Name(t.PrimaryKey.Name) || !Names(t.PrimaryKey.Columns) ||
                t.PrimaryKey.Columns.Any(n => !t.Columns.Any(c => c.Name == n && !c.Nullable)) ||
                t.ForeignKeys is null || t.ForeignKeys.Length > 1000 || t.UniqueKeys is null || t.UniqueKeys.Length > 1000 ||
                t.Checks is null || t.Checks.Length > 1000 ||
                t.ForeignKeys.Select(f => f?.Name).Concat(t.UniqueKeys.Select(k => k?.Name)).Concat(t.Checks.Select(c => c?.Name))
                    .Append(t.PrimaryKey.Name).Distinct().Count() != t.ForeignKeys.Length + t.UniqueKeys.Length + t.Checks.Length + 1) return false;
            foreach (var f in t.ForeignKeys)
                if (!ValidForeignKey(f) || f.Columns.Any(n => !t.Columns.Any(c => c.Name == n))) return false;
            foreach (var k in t.UniqueKeys)
                if (k is null || !Name(k.Name) || !Names(k.Columns) ||
                    k.Columns.Any(n => !t.Columns.Any(c => c.Name == n && !c.Nullable))) return false;
            foreach (var c in t.Checks)
                if (c is null || !Name(c.Name) || CheckCanonical(c.Sql, t.Columns) is null) return false;
        }
        foreach (var f in m.ForeignKeys)
            if (f is null || !ValidForeignKey(f.Constraint) || !MarketingForeignKey(m, f) && !TrainingForeignKey(m, f)) return false;
        foreach (var i in m.Indexes)
        {
            if (i is null || !Name(i.Schema) || !Name(i.Table) || !Name(i.Name) || !Names(i.Columns)) return false;
            var newTable = m.Tables.SingleOrDefault(t => t.Schema == i.Schema && t.Name == i.Table);
            if (newTable is not null)
            {
                if (i.Columns.Any(n => !newTable.Columns.Any(c => c.Name == n)) ||
                    i.Filter is not null && !(i.Unique && i.Columns.Length == 1 &&
                        newTable.Columns.Any(c => c.Name == i.Columns[0] && c.Nullable) &&
                        i.Filter == "[" + i.Columns[0] + "] IS NOT NULL")) return false;
            }
            else if (!MarketingIndex(m, i) && !TrainingIndex(m, i) && !(i.Schema == "dbo" && i.Table == "CommerceOrders" && i.Unique &&
                i.Columns.SequenceEqual(new[] { "CommerceBusinessId", "CheckoutAttemptId" }) &&
                i.Filter == "[CheckoutAttemptId] IS NOT NULL" && m.Guards.Contains(CheckoutGuard))) return false;
        }
        return true;
    }

    internal const string TrainingMigration = "20261026063902_AddScopedTrainingSubscriptions";
    internal const string TrainingIndexTransition = "training-client-scope-index-v1";
    private const string TrainingActiveFilter = "[Status] <> 'Canceled' AND [Status] <> 'ActivationFailed'";
    private static readonly Index TrainingLegacyIndex = new("dbo", "ClientSubscriptions", "IX_ClientSubscriptions_ClientProfileId",
        ["ClientProfileId"], true, TrainingActiveFilter);
    private static bool IsTrainingIndexDrop(string id, DropIndexOperation drop) => id == TrainingMigration &&
        (drop.Schema ?? "dbo") == "dbo" && drop.Table == TrainingLegacyIndex.Table && drop.Name == TrainingLegacyIndex.Name;
    private static bool TrainingForeignKey(Entry m, TableForeignKey f)
    {
        if (m.Id != TrainingMigration || !m.Guards.SequenceEqual(new[] { TrainingIndexTransition }) || f.Schema != "dbo" ||
            f.Table is not ("ClientSubscriptions" or "ClientSubscriptionOffers")) return false;
        var key = f.Constraint;
        foreach (var pair in new[] { ("CommerceBusinessId", "CommerceBusinesses"), ("CommerceProductId", "CommerceProducts") })
            if (key.Name == $"FK_{f.Table}_{pair.Item2}_{pair.Item1}" && key.Columns.SequenceEqual(new[] { pair.Item1 }) &&
                key.PrincipalSchema == "dbo" && key.PrincipalTable == pair.Item2 && key.PrincipalColumns.SequenceEqual(new[] { "Id" }) &&
                m.Columns.Any(c => c.Schema == "dbo" && c.Table == f.Table && c.Name == pair.Item1 && c.Type == "uniqueidentifier" && c.Nullable))
                return true;
        return false;
    }
    private static bool TrainingIndex(Entry m, Index i)
    {
        if (m.Id != TrainingMigration || !m.Guards.SequenceEqual(new[] { TrainingIndexTransition }) || i.Schema != "dbo" ||
            i.Table is not ("ClientSubscriptions" or "ClientSubscriptionOffers")) return false;
        if (i.Table == "ClientSubscriptions" && i.Name == "IX_ClientSubscriptions_ClientProfileId_SubscriptionScope")
            return i.Unique && i.Columns.SequenceEqual(new[] { "ClientProfileId", "SubscriptionScope" }) && i.Filter == TrainingActiveFilter;
        return !i.Unique && i.Filter is null && i.Columns.Length == 1 && i.Columns[0] is "CommerceBusinessId" or "CommerceProductId" &&
            i.Name == $"IX_{i.Table}_{i.Columns[0]}";
    }
    private static bool IndexFilterMatches(Index index, string? observed)
    {
        if (index.Schema != "dbo" || index.Table != "ClientSubscriptions" ||
            index.Name is not ("IX_ClientSubscriptions_ClientProfileId" or "IX_ClientSubscriptions_ClientProfileId_SubscriptionScope"))
            return observed == index.Filter;
        // Use the existing bounded predicate parser, retaining column names and literal bytes.
        Column[] columns = [new("dbo", "ClientSubscriptions", "Status", "nvarchar(32)", false,
            JsonSerializer.SerializeToElement((string?)null), null, null, null)];
        var expected = CheckCanonical(TrainingActiveFilter, columns);
        return index.Filter == TrainingActiveFilter && expected is not null && CheckCanonical(observed, columns) == expected;
    }

    internal enum CatalogKind { Table, Columns, Index, ForeignKey, Checks }
    internal sealed class CatalogBatch
    {
        internal const int MaximumCommands = 32;
        internal sealed record Command(string Sql, (string Name, object Value)[] Parameters,
            Func<DbDataReader, Task> Validate, CatalogKind? Kind = null);
        internal readonly List<Command> Commands = [];
        internal void Add(string sql, Func<DbDataReader, Task> validate, params (string, object)[] parameters) =>
            Commands.Add(new(sql, parameters, validate));
        internal void AddCatalog(CatalogKind kind, Func<DbDataReader, Task> validate, params (string, object)[] parameters) =>
            Commands.Add(new("", parameters, validate, kind));

        internal async Task Execute(MasterAppDbContext db, CancellationToken cancellation)
        {
            Dictionary<CatalogKind, DataTable>? snapshot = null;
            try
            {
            foreach (var command in Commands)
            {
                cancellation.ThrowIfCancellationRequested();
                if (command.Kind is { } kind)
                {
                    // Per-observation only. No metadata survives the current paired read.
                    snapshot ??= await ReadCatalog(db, cancellation);
                    using var selected = Select(snapshot[kind], command, cancellation);
                    using var reader = selected.CreateDataReader();
                    await VerifyResults(reader, [command.Validate], cancellation);
                }
                else
                {
                    // The reviewed pending checkout guard retains its exact SQL
                    // and original execution order, without joining catalog rows.
                    await using var sql = db.Database.GetDbConnection().CreateCommand();
                    sql.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
                    sql.CommandTimeout = 30;
                    sql.CommandText = command.Sql;
                    foreach (var (name, value) in command.Parameters)
                        sql.Parameters.Add(new SqlParameter(name, value));
                    await using var reader = await sql.ExecuteReaderAsync(cancellation);
                    await VerifyResults(reader, [command.Validate], cancellation);
                }
            }
            }
            finally { if (snapshot is not null) foreach (var table in snapshot.Values) table.Dispose(); }
        }

        internal static DataTable Select(DataTable source, Command command, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            var schema = (string)command.Parameters.Single(p => p.Name == "@schema").Value;
            var table = (string)command.Parameters.Single(p => p.Name == "@table").Value;
            var name = command.Parameters.SingleOrDefault(p => p.Name == "@name").Value as string;
            var offset = command.Kind is CatalogKind.Index or CatalogKind.ForeignKey ? 3 : 2;
            var result = new DataTable();
            try
            {
            for (var i = offset; i < source.Columns.Count; i++)
                result.Columns.Add("c" + (i - offset), source.Columns[i].DataType);
            foreach (DataRow row in source.Rows)
            {
                cancellation.ThrowIfCancellationRequested();
                // Exact ordinal object names intentionally fail closed on a
                // case-only mismatch that a CI database collation could accept.
                if ((string)row[0] == schema && (string)row[1] == table &&
                    (offset == 2 || (string)row[2] == name))
                    result.Rows.Add(row.ItemArray.Skip(offset).ToArray());
            }
            return result;
            }
            catch { result.Dispose(); throw; }
        }

        private async Task<Dictionary<CatalogKind, DataTable>> ReadCatalog(MasterAppDbContext db,
            CancellationToken cancellation)
        {
            var scoped = Commands.Where(c => c.Kind is not null).ToArray();
            var targets = scoped.Select(c => (
                Schema: (string)c.Parameters.Single(p => p.Name == "@schema").Value,
                Table: (string)c.Parameters.Single(p => p.Name == "@table").Value)).Distinct().ToArray();
            var kinds = scoped.Select(c => c.Kind!.Value).Distinct().Order().ToArray();
            var result = new Dictionary<CatalogKind, DataTable>();
            if (targets.Length == 0 || kinds.Length == 0)
                throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
            try
            {
            foreach (var chunk in targets.Chunk(MaximumCommands))
            {
                await using var batch = new SqlBatch((SqlConnection)db.Database.GetDbConnection(),
                    (SqlTransaction?)db.Database.CurrentTransaction?.GetDbTransaction()) { Timeout = 30 };
                var values = string.Join(",", chunk.Select((_, i) => $"(@s{i},@t{i})"));
                foreach (var kind in kinds)
                {
                    var command = new SqlBatchCommand(Query(kind).Replace("/*TARGETS*/", values, StringComparison.Ordinal));
                    for (var i = 0; i < chunk.Length; i++)
                    {
                        command.Parameters.Add(new SqlParameter("@s" + i, chunk[i].Schema));
                        command.Parameters.Add(new SqlParameter("@t" + i, chunk[i].Table));
                    }
                    batch.BatchCommands.Add(command);
                }
                await using var reader = await batch.ExecuteReaderAsync(cancellation);
                await ReadResults(reader, kinds, chunk, result, cancellation);
            }
            return result;
            }
            catch { foreach (var table in result.Values) table.Dispose(); throw; }
        }

        internal static async Task ReadResults(DbDataReader reader, CatalogKind[] kinds,
            (string Schema, string Table)[] targets, Dictionary<CatalogKind, DataTable> result,
            CancellationToken cancellation)
        {
            if (kinds.Length is < 1 or > 5 || kinds.Distinct().Count() != kinds.Length ||
                targets.Length is < 1 or > MaximumCommands || targets.Distinct().Count() != targets.Length)
                throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
            var rows = result.Values.Sum(t => t.Rows.Count);
            for (var index = 0; index < kinds.Length; index++)
            {
                cancellation.ThrowIfCancellationRequested();
                if (index > 0 && !await reader.NextResultAsync(cancellation))
                    throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
                var kind = kinds[index];
                var types = Types(kind);
                if (reader.FieldCount != types.Length ||
                    types.Where((type, i) => reader.GetFieldType(i) != type).Any())
                    throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
                if (!result.TryGetValue(kind, out var data))
                {
                    data = new DataTable();
                    for (var i = 0; i < types.Length; i++) data.Columns.Add("c" + i, types[i]);
                    result.Add(kind, data);
                }
                if (data.Columns.Count != types.Length ||
                    types.Where((type, i) => data.Columns[i].DataType != type).Any())
                    throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
                var seenTables = new HashSet<(string, string)>();
                while (await reader.ReadAsync(cancellation))
                {
                    if (++rows > 100000 || reader.IsDBNull(0) || reader.IsDBNull(1) ||
                        !targets.Contains((reader.GetString(0), reader.GetString(1))))
                        throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
                    if (kind == CatalogKind.Table &&
                        !seenTables.Add((reader.GetString(0), reader.GetString(1))))
                        throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
                    var row = new object[reader.FieldCount];
                    reader.GetValues(row);
                    data.Rows.Add(row);
                }
                if (kind == CatalogKind.Table && seenTables.Count != targets.Length)
                    throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
            }
            if (await reader.NextResultAsync(cancellation))
                throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
        }

        internal static Type[] Types(CatalogKind kind) => kind switch
        {
            CatalogKind.Table => [typeof(string), typeof(string), typeof(int)],
            CatalogKind.Columns => [typeof(string), typeof(string), typeof(string), typeof(string), typeof(short),
                typeof(bool), typeof(string), typeof(byte), typeof(bool), typeof(bool), typeof(byte), typeof(string)],
            CatalogKind.Index => [typeof(string), typeof(string), typeof(string), typeof(string), typeof(bool),
                typeof(bool), typeof(string), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool)],
            CatalogKind.ForeignKey => [typeof(string), typeof(string), typeof(string), typeof(string), typeof(string),
                typeof(string), typeof(string), typeof(byte), typeof(byte), typeof(bool), typeof(bool), typeof(bool)],
            CatalogKind.Checks => [typeof(string), typeof(string), typeof(string), typeof(string), typeof(bool), typeof(bool), typeof(bool)],
            _ => throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT")
        };

        private static string Query(CatalogKind kind)
        {
            const string scope = "WITH requested(s,n) AS (SELECT * FROM (VALUES /*TARGETS*/) v(s,n)) ";
            const string tables = " FROM requested r JOIN sys.schemas s ON s.name=r.s JOIN sys.tables t ON t.schema_id=s.schema_id AND t.name=r.n ";
            return scope + (kind switch
            {
                CatalogKind.Table => "SELECT r.s,r.n,COUNT(t.object_id) FROM requested r LEFT JOIN sys.schemas s ON s.name=r.s LEFT JOIN sys.tables t ON t.schema_id=s.schema_id AND t.name=r.n GROUP BY r.s,r.n",
                CatalogKind.Columns => "SELECT r.s,r.n,c.name,ty.name,c.max_length,c.is_nullable,dc.definition,c.scale,c.is_computed,c.is_identity,c.precision,c.collation_name" + tables +
                    "LEFT JOIN sys.columns c ON c.object_id=t.object_id LEFT JOIN sys.types ty ON ty.user_type_id=c.user_type_id LEFT JOIN sys.default_constraints dc ON dc.object_id=c.default_object_id ORDER BY r.s,r.n,c.column_id",
                CatalogKind.Index => "SELECT r.s,r.n,ix.name,c.name,ix.is_unique,ix.is_primary_key,ix.filter_definition,ix.is_disabled,ic.is_descending_key,ic.is_included_column,ix.is_hypothetical,ix.is_unique_constraint" + tables +
                    "JOIN sys.indexes ix ON ix.object_id=t.object_id JOIN sys.index_columns ic ON ic.object_id=ix.object_id AND ic.index_id=ix.index_id JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id ORDER BY r.s,r.n,ix.name,ic.key_ordinal,ic.index_column_id",
                CatalogKind.ForeignKey => "SELECT r.s,r.n,fk.name,pc.name,rs.name,rt.name,rc.name,fk.delete_referential_action,fk.update_referential_action,fk.is_disabled,fk.is_not_trusted,fk.is_not_for_replication" + tables +
                    "JOIN sys.foreign_keys fk ON fk.parent_object_id=t.object_id JOIN sys.foreign_key_columns fc ON fc.constraint_object_id=fk.object_id JOIN sys.columns pc ON pc.object_id=t.object_id AND pc.column_id=fc.parent_column_id JOIN sys.tables rt ON rt.object_id=fk.referenced_object_id JOIN sys.schemas rs ON rs.schema_id=rt.schema_id JOIN sys.columns rc ON rc.object_id=rt.object_id AND rc.column_id=fc.referenced_column_id ORDER BY r.s,r.n,fk.name,fc.constraint_column_id",
                CatalogKind.Checks => "SELECT r.s,r.n,cc.name,cc.definition,cc.is_disabled,cc.is_not_trusted,cc.is_not_for_replication" + tables +
                    "JOIN sys.check_constraints cc ON cc.parent_object_id=t.object_id ORDER BY r.s,r.n,cc.name",
                _ => throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT")
            });
        }

        internal static async Task VerifyResults(DbDataReader reader,
            IReadOnlyList<Func<DbDataReader, Task>> validators, CancellationToken cancellation)
        {
            if (validators.Count is < 1 or > MaximumCommands || reader.FieldCount == 0)
                throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
            for (var index = 0; index < validators.Count; index++)
            {
                if (index > 0 && !await reader.NextResultAsync(cancellation))
                    throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
                await validators[index](reader);
                if (await reader.ReadAsync(cancellation))
                    throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
            }
            if (await reader.NextResultAsync(cancellation))
                throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
        }

    }

    private static async Task<object> Scalar(DbDataReader reader, CancellationToken cancellation)
    {
        if (reader.FieldCount != 1 || !await reader.ReadAsync(cancellation))
            throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
        var result = reader.GetValue(0);
        if (await reader.ReadAsync(cancellation)) throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
        return result;
    }
    private static void PlanStructures(CatalogBatch batch, Entry m, bool applied, CancellationToken cancellation)
    {
        // Plan validates original descriptors before deriving any reviewed schema overlay.
        if (!applied && m.Guards.Contains(CheckoutGuard))
        {
            batch.Add("""
                SELECT CASE WHEN EXISTS (SELECT 1 FROM dbo.CommerceOrders WHERE CheckoutAttemptId IS NOT NULL
                GROUP BY CommerceBusinessId, CheckoutAttemptId HAVING COUNT_BIG(*) > 1) THEN 1 ELSE 0 END
                """, async reader =>
            {
                if (Convert.ToInt32(await Scalar(reader, cancellation)) != 0)
                    throw new ProbeObservationFailure("MERCHANT_CHECKOUT_DUPLICATE_IDENTITY_REQUIRES_REVIEW");
            });
        }
        if (m.Guards.Contains(TrainingIndexTransition))
            PlanIndex(batch, TrainingLegacyIndex, !applied, false, false, cancellation);
        foreach (var t in m.Tables)
        {
            batch.AddCatalog(CatalogKind.Table,
                async reader =>
                {
                    if (Convert.ToInt32(await Scalar(reader, cancellation)) != (applied ? 1 : 0))
                        throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
                }, ("@schema", t.Schema), ("@table", t.Name));
            if (!applied) continue;
            PlanIndex(batch, new Index(t.Schema, t.Name, t.PrimaryKey.Name, t.PrimaryKey.Columns, true, null), true, true, false, cancellation);
            foreach (var key in t.UniqueKeys)
                PlanIndex(batch, new Index(t.Schema, t.Name, key.Name, key.Columns, true, null), true, false, true, cancellation);
            foreach (var f in t.ForeignKeys)
                PlanForeignKey(batch, new TableForeignKey(t.Schema, t.Name, f), true, cancellation);
            PlanChecks(batch, t, cancellation);
        }
        foreach (var f in m.ForeignKeys) PlanForeignKey(batch, f, applied, cancellation);
        foreach (var i in m.Indexes.Where(i => applied || !m.Tables.Any(t => t.Schema == i.Schema && t.Name == i.Table)))
            PlanIndex(batch, i, applied, false, false, cancellation);
    }

    private static void PlanForeignKey(CatalogBatch batch, TableForeignKey owner, bool applied, CancellationToken cancellation)
    {
        var f = owner.Constraint;
        batch.AddCatalog(CatalogKind.ForeignKey, async reader =>
        {
            var n = 0;
            while (await reader.ReadAsync(cancellation))
            {
                if (!applied || n >= f.Columns.Length || reader.GetString(0) != f.Columns[n] || reader.GetString(1) != f.PrincipalSchema ||
                    reader.GetString(2) != f.PrincipalTable || reader.GetString(3) != f.PrincipalColumns[n] ||
                    Convert.ToInt32(reader.GetValue(4)) != 0 || Convert.ToInt32(reader.GetValue(5)) != 0 ||
                    reader.GetBoolean(6) || reader.GetBoolean(7) || reader.GetBoolean(8))
                    throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
                n++;
            }
            if (applied && n != f.Columns.Length) throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
        }, ("@schema", owner.Schema), ("@table", owner.Table), ("@name", f.Name));
    }

    private static void PlanChecks(CatalogBatch batch, Table table, CancellationToken cancellation)
    {
        batch.AddCatalog(CatalogKind.Checks, async reader =>
        {
            var expected = table.Checks.ToDictionary(c => c.Name, StringComparer.Ordinal);
            var found = new HashSet<string>(StringComparer.Ordinal);
            while (await reader.ReadAsync(cancellation))
            {
                if (!expected.TryGetValue(reader.GetString(0), out var check) || !found.Add(check.Name) ||
                    reader.GetBoolean(2) || reader.GetBoolean(3) || reader.GetBoolean(4) ||
                    CheckCanonical(reader.GetString(1), table.Columns) is not { } actual ||
                    actual != CheckCanonical(check.Sql, table.Columns))
                    throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
            }
            if (found.Count != expected.Count) throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
        }, ("@schema", table.Schema), ("@table", table.Name));
    }

    private static void PlanIndex(CatalogBatch batch, Index i, bool applied, bool primary, bool constraint, CancellationToken cancellation)
    {
        batch.AddCatalog(CatalogKind.Index, async reader =>
        {
            var n = 0;
            while (await reader.ReadAsync(cancellation))
            {
                var filter = reader.IsDBNull(3) ? null : reader.GetString(3);
                // SQL Server encloses stored filter predicates in parentheses.
                if (filter is not null && filter.StartsWith('(') && filter.EndsWith(')')) filter = filter[1..^1];
                if (!applied || n >= i.Columns.Length || reader.GetString(0) != i.Columns[n] ||
                    reader.GetBoolean(1) != i.Unique || reader.GetBoolean(2) != primary || !IndexFilterMatches(i, filter) ||
                    reader.GetBoolean(4) || reader.GetBoolean(5) || reader.GetBoolean(6) || reader.GetBoolean(7) ||
                    reader.GetBoolean(8) != constraint) throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
                n++;
            }
            if (applied && n != i.Columns.Length) throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
        }, ("@schema", i.Schema), ("@table", i.Table), ("@name", i.Name));
    }

    // SQL Server rewrites IN/BETWEEN/CAST and parentheses in stored definitions.
    // Parse only bounded declarative predicates, never execute their SQL. Preserve
    // three-valued boolean logic, literal bytes, declared columns and operand groups.
    internal static string? CheckCanonical(string? sql, Column[] columns)
    {
        if (sql is null || sql.Length is < 1 or > 8192) return null;
        try { return new CheckParser(sql, columns).Read(); }
        catch (InvalidOperationException) { return null; }
    }
    private sealed class CheckParser
    {
        private sealed record Token(string Kind, string Text);
        private sealed record Expression(string Kind, string Value, Expression[] Children)
        {
            internal bool Boolean => Kind is "and" or "or" or "=" or "<>" or "<" or "<=" or "null" or "notnull";
            internal string Key => Kind + ":" + JsonSerializer.Serialize(Value) + "[" + string.Join(",", Children.Select(c => c.Key)) + "]";
        }
        private readonly List<Token> tokens = [];
        private readonly Dictionary<string, Column> columns;
        private int position;
        private int depth;
        internal CheckParser(string sql, Column[] declarations)
        {
            columns = declarations.ToDictionary(c => c.Name, StringComparer.Ordinal);
            for (var i = 0; i < sql.Length;)
            {
                if (char.IsWhiteSpace(sql[i])) { i++; continue; }
                if (tokens.Count >= 2048) throw new InvalidOperationException();
                var ch = sql[i];
                if (ch == '[')
                {
                    var value = new StringBuilder(); i++; var closed = false;
                    while (i < sql.Length)
                    {
                        ch = sql[i++];
                        if (ch == ']')
                        {
                            if (i < sql.Length && sql[i] == ']') { value.Append(']'); i++; }
                            else { closed = true; break; }
                        }
                        else value.Append(ch);
                    }
                    if (!closed) throw new InvalidOperationException();
                    tokens.Add(new("identifier", value.ToString()));
                }
                else if (ch == '\'' || ch is 'N' or 'n' && i + 1 < sql.Length && sql[i + 1] == '\'')
                {
                    var unicode = ch != '\'';
                    if (ch != '\'') i++;
                    i++; var value = new StringBuilder(); var closed = false;
                    while (i < sql.Length)
                    {
                        ch = sql[i++];
                        if (ch == '\'')
                        {
                            if (i < sql.Length && sql[i] == '\'') { value.Append('\''); i++; }
                            else { closed = true; break; }
                        }
                        else value.Append(ch);
                    }
                    if (!closed || value.Length > 512 || value.ToString().Any(char.IsControl)) throw new InvalidOperationException();
                    // ASCII has the same value with or without N. For other
                    // characters retain the prefix: code-page conversion is not
                    // equivalent to a Unicode literal and is never inferred.
                    tokens.Add(new(unicode && value.ToString().Any(c => c > 127) ? "nstring" : "string", value.ToString()));
                }
                else if (ch is >= '0' and <= '9')
                {
                    var start = i++;
                    while (i < sql.Length && sql[i] is >= '0' and <= '9') i++;
                    if (!long.TryParse(sql[start..i], NumberStyles.None, CultureInfo.InvariantCulture, out var number)) throw new InvalidOperationException();
                    tokens.Add(new("number", number.ToString(CultureInfo.InvariantCulture)));
                }
                else if (ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
                {
                    var start = i++;
                    while (i < sql.Length && char.IsAsciiLetter(sql[i])) i++;
                    tokens.Add(new("word", sql[start..i].ToUpperInvariant()));
                }
                else if (ch is '(' or ')' or ',' or '%') { tokens.Add(new(ch.ToString(), ch.ToString())); i++; }
                else if (ch is '=' or '<' or '>')
                {
                    var start = i++;
                    if (i < sql.Length && (sql[i] == '=' || ch == '<' && sql[i] == '>')) i++;
                    var value = sql[start..i];
                    if (value is not ("=" or "<>" or "<=" or ">=" or "<" or ">")) throw new InvalidOperationException();
                    tokens.Add(new("comparison", value));
                }
                else throw new InvalidOperationException();
            }
        }
        internal string Read()
        {
            var result = Or();
            if (position != tokens.Count || !result.Boolean) throw new InvalidOperationException();
            return result.Key;
        }
        private bool Take(string value)
        {
            if (position < tokens.Count && tokens[position].Text == value &&
                (tokens[position].Kind == "word" || tokens[position].Kind == value))
            { position++; return true; }
            return false;
        }
        private void Require(string value) { if (!Take(value)) throw new InvalidOperationException(); }
        private static Expression Atom(string kind, string value) => new(kind, value, []);
        private static Expression Boolean(string kind, params Expression[] expressions)
        {
            if (expressions.Any(e => !e.Boolean)) throw new InvalidOperationException();
            return new(kind, "", expressions.SelectMany(e => e.Kind == kind ? e.Children : new[] { e })
                .OrderBy(e => e.Key, StringComparer.Ordinal).ToArray());
        }
        private static Expression Compare(string op, Expression left, Expression right)
        {
            if (left.Boolean || right.Boolean) throw new InvalidOperationException();
            if (op is ">" or ">=") { (left, right) = (right, left); op = op == ">" ? "<" : "<="; }
            if (op is "=" or "<>" && StringComparer.Ordinal.Compare(left.Key, right.Key) > 0) (left, right) = (right, left);
            return new(op, "", [left, right]);
        }
        private Expression Or()
        {
            var value = And();
            while (Take("OR")) value = Boolean("or", value, And());
            return value;
        }
        private Expression And()
        {
            var value = Predicate();
            while (Take("AND")) value = Boolean("and", value, Predicate());
            return value;
        }
        private Expression Predicate()
        {
            var left = Modulo();
            if (Take("IS"))
            {
                var not = Take("NOT"); Require("NULL");
                if (left.Boolean) throw new InvalidOperationException();
                return new(not ? "notnull" : "null", "", [left]);
            }
            if (Take("IN"))
            {
                Require("("); var values = new List<Expression> { Compare("=", left, Modulo()) };
                while (Take(",")) values.Add(Compare("=", left, Modulo()));
                Require(")"); return Boolean("or", values.ToArray());
            }
            if (Take("BETWEEN"))
            {
                var low = Modulo(); Require("AND"); var high = Modulo();
                return Boolean("and", Compare(">=", left, low), Compare("<=", left, high));
            }
            if (position < tokens.Count && tokens[position].Kind == "comparison")
                return Compare(tokens[position++].Text, left, Modulo());
            return left;
        }
        private Expression Modulo()
        {
            var value = Primary();
            while (Take("%"))
            {
                var right = Primary();
                if (value.Kind != "number" && !(value.Kind == "column" && columns[value.Value].Type is "int" or "bigint") ||
                    right.Kind != "number" || right.Value == "0") throw new InvalidOperationException();
                value = new("mod", "", [value, right]);
            }
            return value;
        }
        private Expression Primary()
        {
            if (++depth > 64) throw new InvalidOperationException();
            try
            {
                if (Take("(")) { var value = Or(); Require(")"); return value; }
                if (Take("LEN"))
                {
                    Require("("); var value = Primary(); Require(")");
                    if (value.Kind != "column" || !(columns[value.Value].Type.StartsWith("nvarchar(", StringComparison.Ordinal) ||
                        columns[value.Value].Type.StartsWith("varchar(", StringComparison.Ordinal))) throw new InvalidOperationException();
                    return new("len", "", [value]);
                }
                if (Take("CAST"))
                {
                    Require("("); var value = Modulo(); Require("AS"); DecimalType(); Require(")");
                    return DecimalIdentity(value);
                }
                if (Take("CONVERT"))
                {
                    Require("("); DecimalType(); Require(","); var value = Modulo();
                    if (Take(","))
                    {
                        var style = Primary();
                        if (style.Kind != "number" || style.Value != "0") throw new InvalidOperationException();
                    }
                    Require(")"); return DecimalIdentity(value);
                }
                if (position >= tokens.Count) throw new InvalidOperationException();
                var token = tokens[position++];
                return token.Kind switch {
                    "identifier" when columns.ContainsKey(token.Text) => Atom("column", token.Text),
                    "number" or "string" or "nstring" => Atom(token.Kind, token.Text),
                    _ => throw new InvalidOperationException()
                };
            }
            finally { depth--; }
        }
        private void DecimalType()
        {
            if (position >= tokens.Count || !(tokens[position] is { Kind: "word", Text: "DECIMAL" } ||
                tokens[position] is { Kind: "identifier", Text: "decimal" })) throw new InvalidOperationException();
            position++; Require("(");
            if (position >= tokens.Count || tokens[position++] is not { Kind: "number", Text: "20" }) throw new InvalidOperationException();
            Require(",");
            if (position >= tokens.Count || tokens[position++] is not { Kind: "number", Text: "6" }) throw new InvalidOperationException();
            Require(")");
        }
        private Expression DecimalIdentity(Expression value)
        {
            if (value.Kind != "column" || columns[value.Value].Type != "decimal(20,6)") throw new InvalidOperationException();
            return value;
        }
    }
}
