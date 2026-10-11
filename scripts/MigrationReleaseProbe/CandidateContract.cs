using System.Text.Json;
using System.Text.RegularExpressions;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

// Declarative data crosses from the credential-free candidate builder into the
// approved observer. The production observer never loads a candidate assembly.
internal static partial class CandidateContract
{
    internal sealed record Column(string Schema, string Table, string Name, string Type,
        bool Nullable, JsonElement Default);
    internal sealed record Entry(string Id, bool Supported, Column[] Columns,
        Table[] Tables, Index[] Indexes, string[] Guards);
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
        return new Contract(2, entries);
    }

    internal static Contract Read(string path)
    {
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidOperationException();
        var contract = JsonSerializer.Deserialize<Contract>(File.ReadAllText(path), Json) ?? throw new InvalidOperationException();
        if (contract.SchemaVersion != 2 || contract.Migrations is null || contract.Migrations.Length is < 1 or > 10000 ||
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
            await VerifyStructures(db, migration, applied.Contains(migration.Id, StringComparer.Ordinal), cancellation);
            foreach (var column in migration.Columns.Concat(migration.Tables.SelectMany(t => t.Columns)))
            {
                if (new[] { column.Schema, column.Table, column.Name }.Any(n =>
                        string.IsNullOrWhiteSpace(n) || n.Length > 128) ||
                    !Regex.IsMatch(column.Type, @"^(?:nvarchar|varchar)\((?:max|[1-9][0-9]{0,4})\)$|^(?:int|bigint|bit|uniqueidentifier|datetime2)$"))
                    throw new ProbeObservationFailure("MIGRATION_COMPATIBILITY_UNPROVEN");
                await using var command = db.Database.GetDbConnection().CreateCommand();
                command.CommandTimeout = 30;
                command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
                command.CommandText = """
                    SELECT t.object_id, c.column_id, ty.name, c.max_length, c.is_nullable, dc.definition, c.scale, c.is_computed, c.is_identity
                    FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id
                    LEFT JOIN sys.columns c ON c.object_id=t.object_id AND c.name=@column
                    LEFT JOIN sys.types ty ON ty.user_type_id=c.user_type_id
                    LEFT JOIN sys.default_constraints dc ON dc.object_id=c.default_object_id
                    WHERE s.name=@schema AND t.name=@table
                    """;
                foreach (var (key, value) in new[] { ("@column", column.Name), ("@schema", column.Schema), ("@table", column.Table) })
                { var parameter = command.CreateParameter(); parameter.ParameterName = key; parameter.Value = value; command.Parameters.Add(parameter); }
                await using var reader = await command.ExecuteReaderAsync(cancellation);
                if (!await reader.ReadAsync(cancellation))
                {
                    if (!applied.Contains(migration.Id, StringComparer.Ordinal) &&
                        migration.Tables.Any(t => t.Schema == column.Schema && t.Name == column.Table)) continue;
                    throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
                }
                var exists = !reader.IsDBNull(1);
                if (!applied.Contains(migration.Id, StringComparer.Ordinal))
                { if (exists) throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT"); continue; }
                if (!exists) throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
                var type = reader.GetString(2);
                var length = Convert.ToInt32(reader.GetValue(3));
                var actual = type is "nvarchar" or "varchar" ? type + "(" + (length == -1 ? "max" : (length / (type == "nvarchar" ? 2 : 1)).ToString()) + ")" : type;
                if (actual != column.Type || reader.GetBoolean(4) != column.Nullable ||
                    (actual == "datetime2" && reader.GetByte(6) != 7) || reader.GetBoolean(7) || reader.GetBoolean(8))
                    throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
                if (column.Default.ValueKind == JsonValueKind.Null && !reader.IsDBNull(5))
                    throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
                if (column.Default.ValueKind != JsonValueKind.Null)
                {
                    var expected = column.Default.ValueKind switch {
                        JsonValueKind.String => "'" + column.Default.GetString()!.Replace("'", "''") + "'",
                        JsonValueKind.True => "1", JsonValueKind.False => "0",
                        JsonValueKind.Number => column.Default.GetRawText(), _ => throw new InvalidOperationException() };
                    var definition = reader.IsDBNull(5) ? "" : reader.GetString(5);
                    while (definition.StartsWith('(') && definition.EndsWith(')')) definition=definition[1..^1];
                    if (definition.StartsWith("N'")) definition=definition[1..];
                    if (definition != expected) throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
                }
            }
        }
    }
}
