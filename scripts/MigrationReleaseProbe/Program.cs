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

    // A historical, immutable production audit records these applied EF history
    // stamps even though their legacy source cannot be registered in the current
    // EF assembly. Frozen manual migrations may not be changed to repair this.
    // This allowlist is strictly read-only and tied to exact neighboring
    // registered+applied migrations. No fabricated Up/Down SQL, history update,
    // or unknown migration is authorized by this compatibility observation.
    // Evidence: Infrastructure/MigrationAudit/production-migrations-current.txt
    // and scripts/db-legacy-manual-migrations.txt.
    var auditedLegacy = new Dictionary<string, (string Before, string After)>(
        StringComparer.Ordinal)
    {
        ["20260213015339_FinanceToolStates_ByClientProfile"] =
            ("20260213015112_InitialBaseline", "20260217173126_20260217_ModelSync"),
        ["20260321020000_AddAgentAssistants"] =
            ("20260319141942_20260319_SnapshotSync", "20260321130615_AddAgentAssistantsRuntimeFix"),
        ["20260329093000_ExecutionMvp"] =
            ("20260328071506_AddAnalyticsScaleIndexes", "20260330000618_ExecutionMvp_Regen"),
        ["20260330094500_RepairAgentProfilesSqlite"] =
            ("20260330011403_ActionSurfaceSeparation", "20260331000000_CommitmentsMvp"),
    };
    var unknown = applied.Except(known, StringComparer.Ordinal).ToArray();
    var unregistered = unknown.Where(id => !auditedLegacy.ContainsKey(id)).ToArray();
    if (unregistered.Length > 0)
    {
        // Migration IDs are schema metadata, never data rows or provider text.
        // Report at most sixteen syntactically valid IDs, with a bounded count,
        // so an operator can reconcile exact history rather than guessing.
        var safeIds = unregistered.Take(16).Select(id =>
            System.Text.RegularExpressions.Regex.IsMatch(id,
                @"^[0-9]{8,14}_[A-Za-z0-9_]{1,128}$")
                ? id : "NONCANONICAL").ToArray();
        throw new ProbeObservationFailure("UNKNOWN_APPLIED_MIGRATION",
            safeIds, Math.Min(unregistered.Length, 9999));
    }
    if (applied.Length != applied.Distinct(StringComparer.Ordinal).Count())
        throw new ProbeObservationFailure("HISTORY_SEQUENCE_DRIFT");
    foreach (var (id, anchors) in auditedLegacy)
    {
        var beforeKnown = known.Contains(anchors.Before, StringComparer.Ordinal);
        var afterKnown = known.Contains(anchors.After, StringComparer.Ordinal);
        var beforeApplied = applied.Contains(anchors.Before, StringComparer.Ordinal);
        var afterApplied = applied.Contains(anchors.After, StringComparer.Ordinal);
        if (unknown.Contains(id, StringComparer.Ordinal))
        {
            if (!beforeKnown || !afterKnown || !beforeApplied || !afterApplied)
                throw new ProbeObservationFailure("HISTORY_SEQUENCE_DRIFT");
        }
        else if (!known.Contains(id, StringComparer.Ordinal) &&
                 beforeApplied && afterApplied)
        {
            // A previously required legacy stage cannot silently disappear
            // while both chronological neighbors are already applied.
            throw new ProbeObservationFailure("HISTORY_SEQUENCE_DRIFT");
        }
    }
    var appliedRegistered = applied.Where(id => !auditedLegacy.ContainsKey(id)).ToArray();
    if (!appliedRegistered.SequenceEqual(known.Take(appliedRegistered.Length), StringComparer.Ordinal))
        throw new ProbeObservationFailure("HISTORY_SEQUENCE_DRIFT");
    var pending = known.Except(appliedRegistered, StringComparer.Ordinal).Count();
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        ready = pending == 0,
        // Counts include only an attested historical stamp when actually applied.
        // schemaIdentity still binds the exact registered EF assembly migrations.
        knownCount = known.Length + unknown.Length,
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
    // Fixed classification and tightly filtered schema metadata only.
    // Never print SQL, database names, connection strings or provider output.
    Console.Error.WriteLine("LEGEND_SCHEMA_PROBE:" + ex.Classification);
    if (ex.Classification == "UNKNOWN_APPLIED_MIGRATION" &&
        ex.SafeMigrationIds is { Length: > 0 })
        Console.Error.WriteLine("LEGEND_SCHEMA_HISTORY:" + ex.UnknownCount +
            ":" + string.Join(",", ex.SafeMigrationIds));
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
    public string[]? SafeMigrationIds { get; }
    public int UnknownCount { get; }

    public ProbeObservationFailure(string classification, string[]? safeMigrationIds = null,
        int unknownCount = 0)
    {
        Classification = classification;
        SafeMigrationIds = safeMigrationIds;
        UnknownCount = unknownCount;
    }
}
