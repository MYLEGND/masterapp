using System.Data.Common;
using System.Text.Json;
using System.Text.RegularExpressions;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

internal static partial class CandidateContract
{
    internal sealed record Key(string Name, string[] Columns);
    internal sealed record ForeignKey(string Name, string[] Columns, string PrincipalSchema,
        string PrincipalTable, string[] PrincipalColumns);
    internal sealed record Table(string Schema, string Name, Column[] Columns, Key PrimaryKey, ForeignKey[] ForeignKeys);
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
        c.Name, c.ColumnType ?? "", c.IsNullable, JsonSerializer.SerializeToElement(c.DefaultValue));
    private static bool PlainColumn(AddColumnOperation c, bool newTable) =>
        c.ComputedColumnSql is null && c.DefaultValueSql is null && c.Collation is null &&
        !c.IsRowVersion && !c.GetAnnotations().Any() &&
        (newTable || c.IsNullable || c.DefaultValue is string or int or long or bool);

    internal static Entry Describe(string id, IReadOnlyList<MigrationOperation> operations)
    {
        var columns = operations.OfType<AddColumnOperation>().Select(DescribeColumn).ToArray();
        var tables = operations.OfType<CreateTableOperation>().Select(t => new Table(t.Schema ?? "dbo", t.Name,
            t.Columns.Select(DescribeColumn).ToArray(),
            new Key(t.PrimaryKey?.Name ?? "", t.PrimaryKey?.Columns ?? []),
            t.ForeignKeys.Select(f => new ForeignKey(f.Name, f.Columns, f.PrincipalSchema ?? "dbo",
                f.PrincipalTable, f.PrincipalColumns ?? [])).ToArray())).ToArray();
        var indexes = operations.OfType<CreateIndexOperation>().Select(i => new Index(i.Schema ?? "dbo",
            i.Table, i.Name, i.Columns, i.IsUnique, i.Filter)).ToArray();
        var guards = operations.OfType<SqlOperation>().Select(s =>
            !s.SuppressTransaction && Normalize(s.Sql) == Normalize(CheckoutGuardSql) ? CheckoutGuard : "unknown").ToArray();
        var supported = operations.Count > 0 && operations.All(op => !op.GetAnnotations().Any() && op switch {
            AddColumnOperation c => PlainColumn(c, false),
            CreateTableOperation t => t.PrimaryKey is not null && !t.PrimaryKey.GetAnnotations().Any() &&
                t.UniqueConstraints.Count == 0 && t.CheckConstraints.Count == 0 &&
                t.Columns.All(c => PlainColumn(c, true)) &&
                t.ForeignKeys.All(f => !f.GetAnnotations().Any() &&
                    f.OnDelete is ReferentialAction.NoAction or ReferentialAction.Restrict && f.OnUpdate == ReferentialAction.NoAction),
            CreateIndexOperation i => i.IsDescending is null || i.IsDescending.All(d => !d),
            SqlOperation s => !s.SuppressTransaction && Normalize(s.Sql) == Normalize(CheckoutGuardSql),
            _ => false
        });
        var entry = new Entry(id, supported, columns, tables, indexes, guards);
        return entry with { Supported = supported && ValidStructures(entry) };
    }

    private static bool Name(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128 && !value.Any(char.IsControl);
    private static bool Names(string[]? values) => values is { Length: > 0 and <= 128 } &&
        values.All(Name) && values.Distinct(StringComparer.Ordinal).Count() == values.Length;
    private static bool Type(string? value) => value is not null && Regex.IsMatch(value,
        @"^(?:nvarchar|varchar)\((?:max|[1-9][0-9]{0,4})\)$|^(?:int|bigint|bit|uniqueidentifier|datetime2)$");
    private static bool ValidColumn(Column? c) => c is not null && Name(c.Schema) && Name(c.Table) && Name(c.Name) && Type(c.Type) &&
        c.Default.ValueKind is JsonValueKind.Null or JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False;
    private static bool ValidStructures(Entry m)
    {
        if (m.Tables is null || m.Indexes is null || m.Guards is null ||
            m.Tables.Length > 1000 || m.Indexes.Length > 10000 || m.Guards.Length > 10000) return false;
        // Unsupported historical migrations may have complex structures. They
        // remain opaque and cannot become pending supported operations.
        if (!m.Supported) return true;
        if (m.Guards.Length > 1 || m.Columns.Any(c => !ValidColumn(c)) || m.Guards.Any(g => g != CheckoutGuard) ||
            m.Tables.Select(t => (t?.Schema, t?.Name)).Distinct().Count() != m.Tables.Length ||
            m.Indexes.Select(i => (i?.Schema, i?.Table, i?.Name)).Distinct().Count() != m.Indexes.Length) return false;
        foreach (var t in m.Tables)
        {
            if (t is null || !Name(t.Schema) || !Name(t.Name) || t.Columns is not { Length: > 0 and <= 1000 } ||
                t.Columns.Any(c => !ValidColumn(c) || c.Schema != t.Schema || c.Table != t.Name) ||
                t.Columns.Select(c => c.Name).Distinct().Count() != t.Columns.Length ||
                t.PrimaryKey is null || !Name(t.PrimaryKey.Name) || !Names(t.PrimaryKey.Columns) ||
                t.PrimaryKey.Columns.Any(n => !t.Columns.Any(c => c.Name == n && !c.Nullable)) ||
                t.ForeignKeys is null || t.ForeignKeys.Length > 1000 ||
                t.ForeignKeys.Select(f => f?.Name).Distinct().Count() != t.ForeignKeys.Length) return false;
            foreach (var f in t.ForeignKeys)
                if (f is null || !Name(f.Name) || !Names(f.Columns) || !Name(f.PrincipalSchema) ||
                    !Name(f.PrincipalTable) || !Names(f.PrincipalColumns) || f.Columns.Length != f.PrincipalColumns.Length ||
                    f.Columns.Any(n => !t.Columns.Any(c => c.Name == n))) return false;
        }
        foreach (var i in m.Indexes)
        {
            if (i is null || !Name(i.Schema) || !Name(i.Table) || !Name(i.Name) || !Names(i.Columns)) return false;
            var newTable = m.Tables.SingleOrDefault(t => t.Schema == i.Schema && t.Name == i.Table);
            if (newTable is not null)
            {
                if (i.Filter is not null || i.Columns.Any(n => !newTable.Columns.Any(c => c.Name == n))) return false;
            }
            else if (!(i.Schema == "dbo" && i.Table == "CommerceOrders" && i.Unique &&
                i.Columns.SequenceEqual(new[] { "CommerceBusinessId", "CheckoutAttemptId" }) &&
                i.Filter == "[CheckoutAttemptId] IS NOT NULL" && m.Guards.Contains(CheckoutGuard))) return false;
        }
        return true;
    }

    private static DbCommand Query(MasterAppDbContext db, string sql, params (string, object)[] parameters)
    {
        var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 30;
        foreach (var (name, value) in parameters)
        { var p = command.CreateParameter(); p.ParameterName = name; p.Value = value; command.Parameters.Add(p); }
        return command;
    }
    private static async Task VerifyStructures(MasterAppDbContext db, Entry m, bool applied, CancellationToken cancellation)
    {
        if (!ValidStructures(m)) throw new ProbeObservationFailure("MIGRATION_COMPATIBILITY_UNPROVEN");
        if (!applied && m.Guards.Contains(CheckoutGuard))
        {
            await using var guard = Query(db, """
                SELECT CASE WHEN EXISTS (SELECT 1 FROM dbo.CommerceOrders WHERE CheckoutAttemptId IS NOT NULL
                GROUP BY CommerceBusinessId, CheckoutAttemptId HAVING COUNT_BIG(*) > 1) THEN 1 ELSE 0 END
                """);
            if (Convert.ToInt32(await guard.ExecuteScalarAsync(cancellation)) != 0)
                throw new ProbeObservationFailure("MERCHANT_CHECKOUT_DUPLICATE_IDENTITY_REQUIRES_REVIEW");
        }
        foreach (var t in m.Tables)
        {
            await using var existence = Query(db, "SELECT COUNT(*) FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id WHERE s.name=@schema AND t.name=@table",
                ("@schema", t.Schema), ("@table", t.Name));
            if (Convert.ToInt32(await existence.ExecuteScalarAsync(cancellation)) != (applied ? 1 : 0))
                throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
            if (!applied) continue;
            await VerifyIndex(db, new Index(t.Schema, t.Name, t.PrimaryKey.Name, t.PrimaryKey.Columns, true, null), true, true, cancellation);
            foreach (var f in t.ForeignKeys)
            {
                await using var command = Query(db, """
                    SELECT pc.name, rs.name, rt.name, rc.name, fk.delete_referential_action, fk.update_referential_action,
                           fk.is_disabled, fk.is_not_trusted
                    FROM sys.foreign_keys fk JOIN sys.tables pt ON pt.object_id=fk.parent_object_id
                    JOIN sys.schemas ps ON ps.schema_id=pt.schema_id
                    JOIN sys.foreign_key_columns fc ON fc.constraint_object_id=fk.object_id
                    JOIN sys.columns pc ON pc.object_id=pt.object_id AND pc.column_id=fc.parent_column_id
                    JOIN sys.tables rt ON rt.object_id=fk.referenced_object_id JOIN sys.schemas rs ON rs.schema_id=rt.schema_id
                    JOIN sys.columns rc ON rc.object_id=rt.object_id AND rc.column_id=fc.referenced_column_id
                    WHERE ps.name=@schema AND pt.name=@table AND fk.name=@name ORDER BY fc.constraint_column_id
                    """, ("@schema", t.Schema), ("@table", t.Name), ("@name", f.Name));
                await using var reader = await command.ExecuteReaderAsync(cancellation);
                var n = 0;
                while (await reader.ReadAsync(cancellation))
                {
                    if (n >= f.Columns.Length || reader.GetString(0) != f.Columns[n] || reader.GetString(1) != f.PrincipalSchema ||
                        reader.GetString(2) != f.PrincipalTable || reader.GetString(3) != f.PrincipalColumns[n] ||
                        Convert.ToInt32(reader.GetValue(4)) != 0 || Convert.ToInt32(reader.GetValue(5)) != 0 ||
                        reader.GetBoolean(6) || reader.GetBoolean(7)) throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
                    n++;
                }
                if (n != f.Columns.Length) throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
            }
        }
        foreach (var i in m.Indexes) await VerifyIndex(db, i, applied, false, cancellation);
    }

    private static async Task VerifyIndex(MasterAppDbContext db, Index i, bool applied, bool primary, CancellationToken cancellation)
    {
        await using var command = Query(db, """
            SELECT c.name, ix.is_unique, ix.is_primary_key, ix.filter_definition, ix.is_disabled,
                   ic.is_descending_key, ic.is_included_column, ix.is_hypothetical
            FROM sys.indexes ix JOIN sys.tables t ON t.object_id=ix.object_id JOIN sys.schemas s ON s.schema_id=t.schema_id
            JOIN sys.index_columns ic ON ic.object_id=ix.object_id AND ic.index_id=ix.index_id
            JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
            WHERE s.name=@schema AND t.name=@table AND ix.name=@name ORDER BY ic.key_ordinal, ic.index_column_id
            """, ("@schema", i.Schema), ("@table", i.Table), ("@name", i.Name));
        await using var reader = await command.ExecuteReaderAsync(cancellation);
        var n = 0;
        while (await reader.ReadAsync(cancellation))
        {
            var filter = reader.IsDBNull(3) ? null : reader.GetString(3);
            // SQL Server encloses stored filter predicates in parentheses.
            if (filter is not null && filter.StartsWith('(') && filter.EndsWith(')')) filter = filter[1..^1];
            if (!applied || n >= i.Columns.Length || reader.GetString(0) != i.Columns[n] ||
                reader.GetBoolean(1) != i.Unique || reader.GetBoolean(2) != primary || filter != i.Filter ||
                reader.GetBoolean(4) || reader.GetBoolean(5) || reader.GetBoolean(6) || reader.GetBoolean(7))
                throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
            n++;
        }
        if (applied && n != i.Columns.Length) throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
    }
}
