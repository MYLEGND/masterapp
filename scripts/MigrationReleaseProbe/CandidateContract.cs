using System.Text.Json;
using System.Text.RegularExpressions;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

// Declarative data crosses from the credential-free candidate builder into the
// approved observer. The production observer never loads a candidate assembly.
internal static partial class CandidateContract
{
    internal sealed record Column(string Schema, string Table, string Name, string Type,
        bool Nullable, JsonElement Default, int? Precision, int? Scale, string? Collation);
    internal sealed record Entry(string Id, bool Supported, Column[] Columns,
        Table[] Tables, Index[] Indexes, string[] Guards, TableForeignKey[] ForeignKeys);
    internal sealed record Contract(int SchemaVersion, Entry[] Migrations);
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    internal static string Export(MasterAppDbContext db) => JsonSerializer.Serialize(Current(db), Json);

    internal static Contract Current(MasterAppDbContext db)
    {
        var assembly = db.GetService<IMigrationsAssembly>();
        var entries = assembly.Migrations.OrderBy(x => x.Key, StringComparer.Ordinal).Select(row =>
        {
            var operations = assembly.CreateMigration(row.Value, db.Database.ProviderName!).UpOperations;
            return Describe(row.Key, operations);
        }).ToArray();
        return new Contract(3, entries);
    }

    internal static Contract Read(string path)
    {
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidOperationException();
        var contract = JsonSerializer.Deserialize<Contract>(File.ReadAllText(path), Json) ?? throw new InvalidOperationException();
        if (contract.SchemaVersion != 3 || contract.Migrations is null || contract.Migrations.Length is < 1 or > 10000 ||
            contract.Migrations.Any(m => m is null || m.Id is null || !Regex.IsMatch(m.Id, @"^[0-9]{8,14}_[A-Za-z0-9_]{1,128}$") ||
                m.Columns is null || m.Columns.Length > 1000 || !ValidStructures(m) ||
                (m.Supported && m.Columns.Length + m.Tables.Length == 0) ||
                m.Columns.Any(c => c is null || new[] { c.Schema, c.Table, c.Name }.Any(n =>
                    string.IsNullOrWhiteSpace(n) || n.Length > 128 || n.Any(char.IsControl)) ||
                    c.Type is null || c.Type.Length > 128 ||
                    c.Default.ValueKind is not (JsonValueKind.Null or JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)) ||
                m.Columns.Select(c => (c.Schema, c.Table, c.Name)).Distinct().Count() != m.Columns.Length) ||
            !contract.Migrations.Select(m => m.Id).SequenceEqual(contract.Migrations.Select(m => m.Id).Distinct().Order(StringComparer.Ordinal)))
            throw new InvalidOperationException();
        return contract;
    }

    internal static async Task Verify(MasterAppDbContext db, Contract contract, string[] applied,
        CancellationToken cancellation)
    {
        // Pending operations must be independently additive and compatible with
        // the old application. Unknown/destructive operations require review.
        var pending = contract.Migrations.Where(m => !applied.Contains(m.Id, StringComparer.Ordinal)).ToArray();
        if (pending.Any(m => !m.Supported)) throw new ProbeObservationFailure("MIGRATION_COMPATIBILITY_UNPROVEN");
        // Verify the whole additive suffix, including earlier pending children
        // after a multi-migration bundle completes. Older operations may have
        // been deliberately superseded by a later non-additive migration.
        var relevant = contract.Migrations.Reverse().TakeWhile(m => m.Supported).Reverse();
        await db.Database.OpenConnectionAsync(cancellation);
        foreach (var migration in relevant)
        {
            var isApplied = applied.Contains(migration.Id, StringComparer.Ordinal);
            await VerifyStructures(db, migration, isApplied, cancellation);
            // Pending new tables were proven absent as whole objects. Querying
            // every column of a nonexistent table adds no evidence. Applied
            // columns are read once per table, not once per field/network trip.
            var columns = migration.Columns.Concat(isApplied
                ? migration.Tables.SelectMany(t => t.Columns) : Enumerable.Empty<Column>());
            foreach (var group in columns.GroupBy(c => (c.Schema, c.Table)))
                await VerifyColumns(db, group.ToArray(), isApplied, cancellation);
        }
    }

    private static async Task VerifyColumns(MasterAppDbContext db, Column[] columns,
        bool applied, CancellationToken cancellation)
    {
        if (columns.Length == 0 || columns.Any(c => !ValidColumn(c)))
            throw new ProbeObservationFailure("MIGRATION_COMPATIBILITY_UNPROVEN");
        var expected = columns.ToDictionary(c => c.Name, StringComparer.Ordinal);
        var found = new HashSet<string>(StringComparer.Ordinal);
        await using var command = Query(db, """
            SELECT c.name, ty.name, c.max_length, c.is_nullable, dc.definition, c.scale, c.is_computed, c.is_identity,
                   c.precision, c.collation_name
            FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id
            LEFT JOIN sys.columns c ON c.object_id=t.object_id
            LEFT JOIN sys.types ty ON ty.user_type_id=c.user_type_id
            LEFT JOIN sys.default_constraints dc ON dc.object_id=c.default_object_id
            WHERE s.name=@schema AND t.name=@table
            """, ("@schema", columns[0].Schema), ("@table", columns[0].Table));
        await using var reader = await command.ExecuteReaderAsync(cancellation);
        var tableFound = false;
        while (await reader.ReadAsync(cancellation))
        {
            tableFound = true;
            if (reader.IsDBNull(0) || !expected.TryGetValue(reader.GetString(0), out var column)) continue;
            if (!applied || !found.Add(column.Name)) throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
            var type = reader.GetString(1);
            var length = Convert.ToInt32(reader.GetValue(2));
            var actual = type is "nvarchar" or "varchar" ? type + "(" + (length == -1 ? "max" :
                (length / (type == "nvarchar" ? 2 : 1)).ToString()) + ")" : type == "decimal"
                ? $"decimal({reader.GetByte(8)},{reader.GetByte(5)})" : type;
            if (actual != column.Type || reader.GetBoolean(3) != column.Nullable ||
                (actual == "datetime2" && reader.GetByte(5) != 7) || reader.GetBoolean(6) || reader.GetBoolean(7) ||
                (column.Precision is not null && reader.GetByte(8) != column.Precision) ||
                (column.Scale is not null && reader.GetByte(5) != column.Scale) ||
                (column.Collation is not null && (reader.IsDBNull(9) || reader.GetString(9) != column.Collation)))
                throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
            if (column.Default.ValueKind == JsonValueKind.Null && !reader.IsDBNull(4))
                throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
            if (column.Default.ValueKind != JsonValueKind.Null)
            {
                var definition = reader.IsDBNull(4) ? "" : reader.GetString(4);
                if (!DefaultMatches(column, definition)) throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
            }
        }
        if (!tableFound || (applied && found.Count != columns.Length))
            throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
    }
    internal static bool DefaultMatches(Column column, string definition)
    {
        var defaultValue = column.Default.ValueKind switch {
            JsonValueKind.String => "'" + column.Default.GetString()!.Replace("'", "''") + "'",
            JsonValueKind.True => "1", JsonValueKind.False => "0",
            JsonValueKind.Number => column.Default.GetRawText(), _ => throw new InvalidOperationException() };
        while (definition.StartsWith('(') && definition.EndsWith(')')) definition = definition[1..^1];
        if (definition.StartsWith("N'")) definition = definition[1..];
        // SQL Server stores EF's CAST(0/1 AS bit) as this exact literal conversion.
        // Do not normalize other expressions, types, styles or default kinds.
        if (column.Type == "bit" && (column.Default.ValueKind is JsonValueKind.True or JsonValueKind.False) &&
            definition == "CONVERT([bit],(" + defaultValue + "))")
            definition = defaultValue;
        return definition == defaultValue;
    }
}
