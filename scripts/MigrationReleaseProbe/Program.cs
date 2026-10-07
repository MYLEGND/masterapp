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

    // The immutable historical production audit records one EF history stamp
    // for which no original migration source was ever committed:
    // Infrastructure/MigrationAudit/production-migrations-current.txt.
    // Preserve that exact already-applied marker without fabricating Up/Down SQL,
    // modifying __EFMigrationsHistory, or accepting any other unknown identifier.
    // The adjacent original EF migrations must both be registered and applied.
    const string auditedLegacy = "20260213015339_FinanceToolStates_ByClientProfile";
    const string preceding = "20260213015112_InitialBaseline";
    const string following = "20260217173126_20260217_ModelSync";
    var unknown = applied.Except(known, StringComparer.Ordinal).ToArray();
    var legacyApplied = unknown.Length == 1 &&
        string.Equals(unknown[0], auditedLegacy, StringComparison.Ordinal);
    if (unknown.Length != 0 && !legacyApplied)
        throw new ProbeObservationFailure("UNKNOWN_APPLIED_MIGRATION");
    if (applied.Length != applied.Distinct(StringComparer.Ordinal).Count() ||
        (legacyApplied && (!known.Contains(preceding, StringComparer.Ordinal) ||
                           !known.Contains(following, StringComparer.Ordinal) ||
                           !applied.Contains(preceding, StringComparer.Ordinal) ||
                           !applied.Contains(following, StringComparer.Ordinal))))
        throw new ProbeObservationFailure("HISTORY_SEQUENCE_DRIFT");
    var appliedRegistered = applied.Where(id =>
        !string.Equals(id, auditedLegacy, StringComparison.Ordinal)).ToArray();
    if (!appliedRegistered.SequenceEqual(known.Take(appliedRegistered.Length), StringComparer.Ordinal))
        throw new ProbeObservationFailure("HISTORY_SEQUENCE_DRIFT");
    var pending = known.Except(appliedRegistered, StringComparer.Ordinal).Count();
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        ready = pending == 0,
        // Counts include only an attested historical stamp when actually applied.
        // schemaIdentity still binds the exact registered EF assembly migrations.
        knownCount = known.Length + (legacyApplied ? 1 : 0),
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
