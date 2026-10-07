using Microsoft.Data.SqlClient;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

// This executable ships beside the validated EF bundle. The same Infrastructure
// assembly owns both schema identities. No application rows or SQL are exposed.
try
{
    var connection = Environment.GetEnvironmentVariable("LEGEND_RELEASE_DB_CONNECTION");
    if (string.IsNullOrWhiteSpace(connection)) throw new ProbeObservationFailure("INPUT_UNAVAILABLE");
    var options = new DbContextOptionsBuilder<MasterAppDbContext>()
        .UseSqlServer(connection, sql => sql.CommandTimeout(30)).Options;
    await using var db = new MasterAppDbContext(options);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
    var known = db.Database.GetMigrations().Order(StringComparer.Ordinal).ToArray();
    var applied = (await db.Database.GetAppliedMigrationsAsync(timeout.Token))
        .Order(StringComparer.Ordinal).ToArray();
    if (known.Length == 0)
        throw new ProbeObservationFailure("MIGRATIONS_MISSING");
    if (applied.Except(known, StringComparer.Ordinal).Any())
        throw new ProbeObservationFailure("UNKNOWN_APPLIED_MIGRATION");
    if (!applied.SequenceEqual(known.Take(applied.Length), StringComparer.Ordinal))
        throw new ProbeObservationFailure("HISTORY_SEQUENCE_DRIFT");
    var pending = known.Except(applied, StringComparer.Ordinal).Count();
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        ready = pending == 0,
        knownCount = known.Length,
        appliedCount = applied.Length,
        pendingCount = pending,
        schemaIdentity = Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(string.Join("\n", known))))
    }));
}
catch (SqlException ex) when (ex.Number is 40197 or 40501 or 40613 or 49918 or 49919 or 49920)
{
    // Only exact transient SQL availability/throttling numbers permit a bounded read retry.
    Console.Error.WriteLine("LEGEND_SCHEMA_PROBE:TRANSIENT_SQL_READ");
    Environment.ExitCode = 1;
}
catch (SqlException ex) when (ex.Number is 18456 or 4060)
{
    Console.Error.WriteLine("LEGEND_SCHEMA_PROBE:SQL_AUTH");
    Environment.ExitCode = 1;
}
catch (ProbeObservationFailure ex)
{
    // Fixed, non-secret classification from an explicit migration-history check.
    Console.Error.WriteLine("LEGEND_SCHEMA_PROBE:" + ex.Classification);
    Environment.ExitCode = 1;
}
catch (InvalidOperationException)
{
    // Provider/model failures are not evidence of migration history drift.
    Console.Error.WriteLine("LEGEND_SCHEMA_PROBE:RUNTIME_INVALID_OPERATION");
    Environment.ExitCode = 1;
}
catch
{
    // Never emit SQL/provider exception messages or connection details.
    Console.Error.WriteLine("LEGEND_SCHEMA_PROBE:UNCLASSIFIED");
    Environment.ExitCode = 1;
}

sealed class ProbeObservationFailure : Exception
{
    public string Classification { get; }
    public ProbeObservationFailure(string classification) => Classification = classification;
}
