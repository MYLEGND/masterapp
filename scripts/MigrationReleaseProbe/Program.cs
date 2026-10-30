using System.Diagnostics;
using Microsoft.Data.SqlClient;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

// This executable ships beside the validated EF bundle. The same Infrastructure
// assembly owns both schema identities. No application rows or SQL are exposed.
// Fixed phase labels and bounded timings only; never provider text or SQL.
// Diagnostic evidence cannot authorize readiness or retries.
var observationClock = Stopwatch.StartNew();
var phaseClock = Stopwatch.StartNew();
var observationPhase = "setup";
CancellationToken observationCancellation = default;
void Phase(string name) { observationPhase = name; phaseClock.Restart(); }
void TimeoutDiagnostic()
{
    Console.Error.WriteLine($"LEGEND_SCHEMA_TIMING:{observationPhase}:{Math.Min(observationClock.ElapsedMilliseconds, 999999)}:{Math.Min(phaseClock.ElapsedMilliseconds, 999999)}");
}
void FailureDiagnostic(Exception error)
{
    // Actual type metadata and SQL number only; no message, stack, path or provider values.
    var types = new List<string>();
    int? sqlNumber = null;
    for (Exception? current = error; current is not null && types.Count < 4; current = current.InnerException)
    {
        var name = current.GetType().FullName ?? "UNKNOWN";
        types.Add(name.Length <= 160 && System.Text.RegularExpressions.Regex.IsMatch(name,
            @"^[A-Za-z_][A-Za-z0-9_.+`]*$") ? name : "UNKNOWN");
        if (current is SqlException sql) sqlNumber ??= sql.Number;
    }
    Console.Error.WriteLine($"LEGEND_SCHEMA_FAILURE:{observationPhase}:TYPES:{string.Join(",", types)}:SQL:{sqlNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NONE"}");
}
try
{
    var inspectActivity = args.Count(arg => arg == "--activity") == 1;
    var inspectSchemaOnly = args.Count(arg => arg == "--schema-only-activity") == 1;
    if (args.Count(arg => arg == "--activity") > 1 ||
        args.Count(arg => arg == "--schema-only-activity") > 1 ||
        (inspectActivity && inspectSchemaOnly))
        throw new ProbeObservationFailure("INPUT_UNAVAILABLE");
    args = args.Where(arg => arg != "--activity" && arg != "--schema-only-activity").ToArray();
    var export = args.SequenceEqual(new[] { "--export-contract" });
    var validate = args.Length == 2 && args[0] == "--validate-contract";
    var contractTests = args.SequenceEqual(new[] { "--test-contract-compatibility" });
    var offline = export || validate || contractTests;
    var contractPath = args.Length == 2 && args[0] is "--contract" or "--validate-contract" ? args[1] : null;
    if ((offline && (inspectActivity || inspectSchemaOnly)) ||
        (!export && !contractTests && contractPath is null && (args.Length > 1 || (args.Length == 1 && args[0] != "--inventory"))))
        throw new ProbeObservationFailure("INPUT_UNAVAILABLE");
    var connection = offline ? "Server=127.0.0.1;Database=OfflineContract;Integrated Security=true" : Environment.GetEnvironmentVariable("LEGEND_RELEASE_DB_CONNECTION");
    if (string.IsNullOrWhiteSpace(connection)) throw new ProbeObservationFailure("INPUT_UNAVAILABLE");
    var options = new DbContextOptionsBuilder<MasterAppDbContext>()
        .UseSqlServer(connection, sql => sql.CommandTimeout(30)).Options;
    await using var db = new MasterAppDbContext(options);
    if (export) { Console.WriteLine(CandidateContract.Export(db)); return; }
    if (contractTests) { TrustedCandidateContract.Test(CandidateContract.Current(db)); return; }
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
    observationCancellation = timeout.Token;
    Phase("contract");
    var trusted = CandidateContract.Current(db);
    var contract = contractPath is null ? trusted : TrustedCandidateContract.Read(contractPath);
    // One trusted owner is used before OIDC and again before production reads.
    // The candidate never supplies or replaces the authoritative old prefix.
    var contractRule = TrustedCandidateContract.Prove(trusted, contract);
    if (validate)
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            schemaVersion = 1, purpose = "trusted-schema-contract-compatibility", authorizesMutation = false,
            rule = contractRule, trustedContractSha256 = TrustedCandidateContract.Digest(trusted),
            candidateContractSha256 = TrustedCandidateContract.Digest(contract)
        }));
        return;
    }
    var known = contract.Migrations.Select(m => m.Id).ToArray();
    async Task VerifySettledActivity(bool schemaOnly = false)
    {
        Phase("activity-connection");
        await db.Database.OpenConnectionAsync(timeout.Token);
        // Only approved, fixed metadata queries. Never expose session identities,
        // SQL text, application rows or credentials. A restricted DMV view must
        // not turn invisible workers into a false quiet-database observation.
        await using var activityCommand = db.Database.GetDbConnection().CreateCommand();
        activityCommand.CommandTimeout = 30;
        activityCommand.CommandText = """
            SELECT CASE WHEN CONVERT(int,SERVERPROPERTY('EngineEdition')) = 5
              THEN COALESCE(HAS_PERMS_BY_NAME(DB_NAME(),'DATABASE','VIEW DATABASE STATE'),0)
              ELSE CASE WHEN COALESCE(HAS_PERMS_BY_NAME(NULL,NULL,'VIEW SERVER STATE'),0) = 1
                OR COALESCE(HAS_PERMS_BY_NAME(NULL,NULL,'VIEW SERVER PERFORMANCE STATE'),0) = 1
                THEN 1 ELSE 0 END END
            """;
        Phase("activity-permission");
        if (Convert.ToInt32(await activityCommand.ExecuteScalarAsync(timeout.Token)) != 1)
            throw new ProbeObservationFailure("MUTATION_ACTIVITY_UNPROVEN");
        // Observe availability only; do not acquire a production lock. EF's
        // session lock can remain held between transactions while its session
        // is sleeping. It is additional exclusion proof, never a replacement
        // for unknown/user activity checks below.
        activityCommand.CommandText = "SELECT APPLOCK_TEST('public','__EFMigrationsLock','Exclusive','Session')";
        Phase("activity-migration-lock");
        var migrationLock = await activityCommand.ExecuteScalarAsync(timeout.Token);
        if (migrationLock is null || migrationLock is DBNull)
            throw new ProbeObservationFailure("MUTATION_ACTIVITY_UNPROVEN");
        if (Convert.ToInt32(migrationLock) != 1)
            throw new ProbeObservationFailure("MUTATION_ACTIVITY_ACTIVE");
        if (schemaOnly)
        {
            // Source-only publication executes no SQL migration. Enforce the EF
            // exclusive migration lock and absence of schema modification locks,
            // while allowing independent live user requests/transactions. The
            // caller also requires two identical physical schema observations.
            // This is NEVER used for a release with pending EF/schema changes.
            activityCommand.CommandText = """
                SELECT COUNT_BIG(*) FROM sys.dm_tran_locks AS l
                WHERE l.resource_database_id = DB_ID()
                  AND l.request_session_id <> @@SPID
                  AND l.request_mode = 'Sch-M'
                  AND l.request_status IN ('GRANT','WAIT','CONVERT')
                """;
            Phase("activity-schema-locks");
            if (Convert.ToInt64(await activityCommand.ExecuteScalarAsync(timeout.Token)) != 0)
                throw new ProbeObservationFailure("MUTATION_ACTIVITY_ACTIVE");
            return;
        }
        // SQL-mutating releases retain the existing strict full quiet-database
        // evidence, including all user requests and open transactions.
        activityCommand.CommandText = """
            SELECT
              (SELECT COUNT_BIG(*) FROM sys.dm_exec_requests AS r
                LEFT JOIN sys.dm_exec_sessions AS s ON s.session_id = r.session_id
                WHERE r.database_id = DB_ID() AND r.session_id <> @@SPID
                  AND (s.is_user_process = 1 OR s.session_id IS NULL))
              + (SELECT COUNT_BIG(*) FROM sys.dm_exec_sessions
                WHERE database_id = DB_ID() AND session_id <> @@SPID AND is_user_process = 1
                  AND (open_transaction_count > 0 OR status IN ('running','rollback')))
              + (SELECT COUNT_BIG(*) FROM sys.dm_tran_database_transactions AS d
                LEFT JOIN sys.dm_tran_active_transactions AS a
                  ON a.transaction_id = d.transaction_id
                WHERE d.database_id = DB_ID() AND d.database_transaction_type = 1
                  AND d.database_transaction_state NOT IN (10,11)
                  AND (a.transaction_type <> 3 OR a.transaction_id IS NULL))
            """;
        Phase("activity-transactions");
        if (Convert.ToInt64(await activityCommand.ExecuteScalarAsync(timeout.Token)) != 0)
            throw new ProbeObservationFailure("MUTATION_ACTIVITY_ACTIVE");
    }
    if (inspectActivity) await VerifySettledActivity();
    Phase("history");
    var applied = (await db.Database.GetAppliedMigrationsAsync(timeout.Token))
        .Order(StringComparer.Ordinal).ToArray();
    if (known.Length == 0)
        throw new ProbeObservationFailure("MIGRATIONS_MISSING");

    // Observation only: a complete, bounded inventory is not a release receipt
    // and never runs EF migration commands or reads application row contents.
    Phase("history-validation");
    if (args.SequenceEqual(new[] { "--inventory" }))
    {
        var idPattern = new System.Text.RegularExpressions.Regex(
            @"^[0-9]{8,14}_[A-Za-z0-9_]{1,128}$");
        if (known.Length > 10000 || applied.Length > 10000 ||
            known.Concat(applied).Any(id => !idPattern.IsMatch(id)) ||
            known.Distinct(StringComparer.Ordinal).Count() != known.Length ||
            applied.Distinct(StringComparer.Ordinal).Count() != applied.Length)
            throw new ProbeObservationFailure("HISTORY_SEQUENCE_DRIFT");
        await db.Database.OpenConnectionAsync(timeout.Token);
        var history = new List<object>();
        var observedIds = new List<string>();
        await using (var command = db.Database.GetDbConnection().CreateCommand())
        {
            command.CommandText = "SELECT MigrationId, ProductVersion FROM dbo.__EFMigrationsHistory ORDER BY MigrationId";
            command.CommandTimeout = 30;
            await using var reader = await command.ExecuteReaderAsync(timeout.Token);
            while (await reader.ReadAsync(timeout.Token))
            {
                var id = reader.GetString(0);
                var version = reader.GetString(1);
                if (history.Count >= 10000 || !idPattern.IsMatch(id) ||
                    !System.Text.RegularExpressions.Regex.IsMatch(version, @"^[A-Za-z0-9.+_-]{1,128}$"))
                    throw new ProbeObservationFailure("HISTORY_SEQUENCE_DRIFT");
                observedIds.Add(id);
                history.Add(new { migrationId = id, productVersion = version });
            }
        }
        if (!observedIds.Order(StringComparer.Ordinal).SequenceEqual(applied, StringComparer.Ordinal))
            throw new ProbeObservationFailure("HISTORY_SEQUENCE_DRIFT");
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            purpose = "read-only-migration-inventory",
            authorizesMutation = false,
            observedUtc = DateTimeOffset.UtcNow,
            migrationHistory = history,
            appliedMigrationIds = applied,
            recognizedMigrationIds = known,
            pendingMigrationIds = known.Except(applied, StringComparer.Ordinal).ToArray(),
            unrecognizedAppliedMigrationIds = applied.Except(known, StringComparer.Ordinal).ToArray(),
            schemaIdentity = Convert.ToHexStringLower(SHA256.HashData(
                Encoding.UTF8.GetBytes(string.Join("\n", known))))
        }));
        return;
    }

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
    // Public migration identifiers (never application rows) are a bounded,
    // ordered schema fence for reconciling completed older EF bundle generations.
    // A read-only probe cannot authorize a new SQL write on its own.
    var pendingIds = known.Except(appliedRegistered, StringComparer.Ordinal).ToArray();
    var pending = pendingIds.Length;
    // Historical EF failures previously entered these migration IDs. Their
    // current applied history is necessary but not sufficient: physically
    // attest the named schema objects before admitting any NEW SQL write.
    // Only sys.* catalog metadata is read; no application rows/DDL/EF Migrate.
    Phase("catalog-engineering");
    if (appliedRegistered.Contains("20261001070000_AddLegendEngineeringControlPlane",
                                  StringComparer.Ordinal))
    {
        await db.Database.OpenConnectionAsync(timeout.Token);
        await using var engineering = db.Database.GetDbConnection().CreateCommand();
        engineering.CommandTimeout = 30;
        engineering.CommandText = """
            SELECT COUNT(*) FROM sys.tables t
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE s.name = N'dbo' AND t.name IN
              (N'LegendEngineeringControlLocks',
               N'LegendEngineeringChatGptPlanCredentials',
               N'LegendEngineeringWorkItems',
               N'LegendEngineeringContexts',
               N'LegendEngineeringUsage')
            """;
        var tableCount = Convert.ToInt32(await engineering.ExecuteScalarAsync(timeout.Token));
        if (tableCount != 5)
            throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
        engineering.CommandText = """
            SELECT COUNT(*) FROM sys.columns c
            JOIN sys.tables t ON t.object_id = c.object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            WHERE s.name = N'dbo' AND t.name = N'LegendEngineeringControlLocks'
              AND ((c.name = N'LockKey' AND ty.name = N'nvarchar'
                    AND c.max_length = 128 AND c.is_nullable = 0)
                OR (c.name = N'Revision' AND ty.name = N'bigint'
                    AND c.max_length = 8 AND c.is_nullable = 0))
            """;
        if (Convert.ToInt32(await engineering.ExecuteScalarAsync(timeout.Token)) != 2)
            throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
    }
    Phase("catalog-feeds");
    if (appliedRegistered.Contains("20260927090000_AddOpenAiProductFeedProjections",
                                  StringComparer.Ordinal))
    {
        await db.Database.OpenConnectionAsync(timeout.Token);
        await using var feeds = db.Database.GetDbConnection().CreateCommand();
        feeds.CommandTimeout = 30;
        feeds.CommandText = """
            SELECT COUNT(*) FROM sys.foreign_keys f
            JOIN sys.tables t ON t.object_id = f.parent_object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE s.name = N'dbo' AND t.name = N'OpenAiProductFeedProjections'
              AND f.name IN (
                  N'FK_OpenAiProductFeedProjections_CommerceBusinesses_CommerceBusinessId',
                  N'FK_OpenAiProductFeedProjections_CommerceProducts_CommerceProductId')
              AND f.delete_referential_action = 0
            """;
        if (Convert.ToInt32(await feeds.ExecuteScalarAsync(timeout.Token)) != 2)
            throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
        feeds.CommandText = """
            SELECT COUNT(*) FROM sys.indexes i
            JOIN sys.tables t ON t.object_id = i.object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE s.name = N'dbo' AND t.name = N'OpenAiProductFeedProjections'
              AND i.name = N'IX_OpenAiProductFeedProjections_CommerceBusinessId_CommerceProductId_Provider'
              AND i.is_unique = 1 AND i.is_disabled = 0
            """;
        if (Convert.ToInt32(await feeds.ExecuteScalarAsync(timeout.Token)) != 1)
            throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
    }

    // Physical SQL Server proof for the ONLY new Founder rules migration.
    // No application rows, UPDATEs, EF Migrate(), or schema DDL are executed.
    // The EF history alone cannot prove whether a column was partially added.
    // Require the table and its catalog metadata to agree with the known
    // pending/applied state before ANY migration bundle can be authorized.
    Phase("catalog-founder");
    const string founderMigration = "20261007134500_AddFounderAssistantRules";
    if (known.Contains(founderMigration, StringComparer.Ordinal))
    {
        await db.Database.OpenConnectionAsync(timeout.Token);
        await using var schemaCommand = db.Database.GetDbConnection().CreateCommand();
        schemaCommand.CommandTimeout = 30;
        schemaCommand.CommandText = """
            SELECT COUNT(*) FROM sys.tables t
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE s.name = N'dbo' AND t.name = N'MobileProfileSettings'
            """;
        var tableCount = Convert.ToInt32(await schemaCommand.ExecuteScalarAsync(timeout.Token));
        if (tableCount != 1)
            throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");

        schemaCommand.CommandText = """
            SELECT c.max_length, c.is_nullable, ty.name, dc.definition,
                (SELECT COUNT_BIG(*) FROM sys.index_columns ic
                 WHERE ic.object_id = t.object_id AND ic.column_id = c.column_id),
                (SELECT COUNT_BIG(*) FROM sys.foreign_key_columns fk
                 WHERE (fk.parent_object_id = t.object_id AND fk.parent_column_id = c.column_id)
                    OR (fk.referenced_object_id = t.object_id AND fk.referenced_column_id = c.column_id))
            FROM sys.tables t
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            JOIN sys.columns c ON c.object_id = t.object_id
            JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            LEFT JOIN sys.default_constraints dc
                ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
            WHERE s.name = N'dbo' AND t.name = N'MobileProfileSettings'
              AND c.name = N'FounderAssistantRulesJson'
            """;
        var found = false;
        var valid = false;
        await using (var columns = await schemaCommand.ExecuteReaderAsync(timeout.Token))
        {
            while (await columns.ReadAsync(timeout.Token))
            {
                if (found) throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
                found = true;
                var definition = columns.IsDBNull(3) ? "" : columns.GetString(3);
                var normalized = new string(definition.Where(c =>
                    !char.IsWhiteSpace(c) && c != '(' && c != ')').ToArray());
                valid = Convert.ToInt32(columns.GetValue(0)) == -1
                    && !columns.GetBoolean(1)
                    && columns.GetString(2) == "nvarchar"
                    && (normalized == "'[]'" || normalized == "N'[]'")
                    && Convert.ToInt64(columns.GetValue(4)) == 0
                    && Convert.ToInt64(columns.GetValue(5)) == 0;
            }
        }
        var rulePending = pendingIds.Contains(founderMigration, StringComparer.Ordinal);
        if (rulePending ? found : !found || !valid)
            throw new ProbeObservationFailure("PHYSICAL_SCHEMA_DRIFT");
    }

    Phase("catalog-contract");
    if (contract is not null) await CandidateContract.Verify(db, contract, appliedRegistered, timeout.Token);
    Phase("identity");
    await db.Database.OpenConnectionAsync(timeout.Token);
    await using var identityCommand = db.Database.GetDbConnection().CreateCommand();
    identityCommand.CommandTimeout = 30;
    identityCommand.CommandText = "SELECT CONCAT(CONVERT(nvarchar(256),SERVERPROPERTY('ServerName')),N'/',DB_NAME())";
    var databaseIdentity = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        Convert.ToString(await identityCommand.ExecuteScalarAsync(timeout.Token)) ?? throw new InvalidOperationException())));
    if (inspectActivity || inspectSchemaOnly) await VerifySettledActivity(inspectSchemaOnly);
    var mutationActivity = inspectActivity || inspectSchemaOnly ? "settled" : "not-observed";
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        mutationActivity,
        databaseIdentity,
        // Physical postconditions above have passed. Bind the observed applied
        // prefix and those exact expectations separately from catalog identity.
        baselineIdentity = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { applied, contract }, CandidateContract.Json)))),
        observedUtc = DateTimeOffset.UtcNow,
        ready = pending == 0,
        // Counts include only an attested historical stamp when actually applied.
        // schemaIdentity still binds the exact registered EF assembly migrations.
        knownCount = known.Length + unknown.Length,
        appliedCount = applied.Length,
        pendingCount = pending,
        lastAppliedMigrationId = appliedRegistered.LastOrDefault(),
        firstPendingMigrationId = pendingIds.FirstOrDefault(),
        schemaIdentity = Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(string.Join("\n", known))))
    }));
}
catch (SqlException) when (observationCancellation.IsCancellationRequested)
{
    // The observer's own deadline was actually canceled. SQL number zero alone
    // is not a timeout or transient classification and never permits a retry.
    Console.Error.WriteLine("LEGEND_SCHEMA_PROBE:SQL_READ_DEADLINE");
    TimeoutDiagnostic();
    Environment.ExitCode = 1;
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
catch (SqlException ex) when (ex.Number == -2)
{
    Console.Error.WriteLine("LEGEND_SCHEMA_PROBE:SQL_QUERY_TIMEOUT");
    TimeoutDiagnostic();
    Environment.ExitCode = 1;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("LEGEND_SCHEMA_PROBE:SQL_READ_DEADLINE");
    TimeoutDiagnostic();
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
catch (InvalidOperationException error)
{
    // Provider/model failures are not evidence of migration history drift.
    Console.Error.WriteLine("LEGEND_SCHEMA_PROBE:RUNTIME_INVALID_OPERATION");
    FailureDiagnostic(error);
    Environment.ExitCode = 1;
}
catch (Exception error)
{
    // Never emit SQL/provider exception messages or connection details.
    Console.Error.WriteLine("LEGEND_SCHEMA_PROBE:UNCLASSIFIED");
    FailureDiagnostic(error);
    Environment.ExitCode = 1;
}

// This immutable trusted executable owns compatibility; candidate metadata is
// data only. Its caller separately pins complete reviewed runtime/tool inputs.
internal static class TrustedCandidateContract
{
    private const string ReviewedMigration = "20261020160000_AddReceptionistCoordination";
    private const string ReviewedEntrySha256 = "d2da70a25230e14bf520e4129ac95a9fc558e9316385cedfcb172a5d82564fd6";

    private const string PaymentMigration = "20261026000134_AddCanonicalSubscriptionPaymentCompletion";
    private const string PaymentEntrySha256 = "6fe5eaa7f0070c282a5219075d4efe101dca16a7dacae605c87b58f0f9027975";

    private const string TrainingMigration = CandidateContract.TrainingMigration;
    private const string TrainingEntrySha256 = "5a543b02a2a1ae2f2bb35d5e2a8fb51f41e9601b1ef4de2707ec6db51029be10";

    private const string ManagementMigration = "20261026210738_AddReceptionistManagement";
    private const string ManagementEntrySha256 = "4505723c4487d382bd8bcde553d5e5d835dec499564c2b308469a4e2861c3d3d";

    private const string BrowserEntrySha256 = "da3529bf9f9a13b41ad6b342a2fc65d293f2e75a1fa282123bd1812bf6a4c4af";

    internal static string Digest<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(value, CandidateContract.Json)));

    internal static CandidateContract.Contract Read(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length > 4 * 1024 * 1024 || file.LinkTarget is not null ||
            (file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new ProbeObservationFailure("CANDIDATE_MIGRATION_CONTRACT_UNPROVEN");
        using var raw = JsonDocument.Parse(File.ReadAllText(path));
        NoDuplicateProperties(raw.RootElement);
        var contract = CandidateContract.Read(path);
        // Deserialization alone ignores unknown properties and supplies missing
        // defaults. Require the entire data tree to survive the typed boundary.
        if (!System.Text.Json.Nodes.JsonNode.DeepEquals(
            System.Text.Json.Nodes.JsonNode.Parse(raw.RootElement.GetRawText()),
            JsonSerializer.SerializeToNode(contract, CandidateContract.Json)))
            throw new ProbeObservationFailure("CANDIDATE_MIGRATION_CONTRACT_UNPROVEN");
        return contract;
    }

    private static void NoDuplicateProperties(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in node.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new ProbeObservationFailure("CANDIDATE_MIGRATION_CONTRACT_UNPROVEN");
                NoDuplicateProperties(property.Value);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var value in node.EnumerateArray()) NoDuplicateProperties(value);
    }

    internal static string Prove(CandidateContract.Contract trusted, CandidateContract.Contract candidate)
    {
        if (trusted.SchemaVersion != 3 || candidate.SchemaVersion != 3 ||
            trusted.Migrations is null || candidate.Migrations is null ||
            trusted.Migrations.Length is < 1 or > 10000 || candidate.Migrations.Length is < 1 or > 10000 ||
            trusted.Migrations.Any(entry => entry is null) || candidate.Migrations.Any(entry => entry is null))
            throw new ProbeObservationFailure("CANDIDATE_MIGRATION_CONTRACT_UNPROVEN");
        if (JsonSerializer.Serialize(trusted, CandidateContract.Json) == JsonSerializer.Serialize(candidate, CandidateContract.Json))
            return "approved-definitions-exact";
        // One independently reviewed current transition, not a generic append waiver.
        if (candidate.Migrations.Length == trusted.Migrations.Length + 1 &&
            trusted.Migrations[^1].Id == CandidateContract.BrowserMigration &&
            candidate.Migrations[^1].Id == CandidateContract.DiagnosticsMigration &&
            candidate.Migrations[^1].Supported && Digest(candidate.Migrations[^1]) == CandidateContract.DiagnosticsEntrySha256 &&
            candidate.Migrations.Take(trusted.Migrations.Length).Zip(trusted.Migrations).All(pair =>
                JsonSerializer.Serialize(pair.First, CandidateContract.Json) == JsonSerializer.Serialize(pair.Second, CandidateContract.Json)))
            return "approved-prefix-reviewed-diagnostics-entry";
        var added = candidate.Migrations.Length - trusted.Migrations.Length;
        if (added is < 1 or > 5 || candidate.Migrations.Length > 10000 ||
            !candidate.Migrations.Take(trusted.Migrations.Length).Zip(trusted.Migrations).All(pair =>
                JsonSerializer.Serialize(pair.First, CandidateContract.Json) == JsonSerializer.Serialize(pair.Second, CandidateContract.Json)))
            throw new ProbeObservationFailure("CANDIDATE_MIGRATION_CONTRACT_UNPROVEN");
        var suffix = candidate.Migrations.Skip(trusted.Migrations.Length).ToArray();
        var reviewed = new[] { (ReviewedMigration, ReviewedEntrySha256), (PaymentMigration, PaymentEntrySha256),
            (TrainingMigration, TrainingEntrySha256), (ManagementMigration, ManagementEntrySha256), (CandidateContract.BrowserMigration, BrowserEntrySha256) };
        var start = Array.FindIndex(reviewed, x => x.Item1 == suffix[0]?.Id);
        if (start < 0 || start + added > reviewed.Length)
            throw new ProbeObservationFailure("CANDIDATE_MIGRATION_CONTRACT_UNPROVEN");
        var expected = reviewed.Skip(start).Take(added).ToArray();
        var previous = trusted.Migrations[^1].Id;
        if (start > 0 && previous != reviewed[start - 1].Item1)
            throw new ProbeObservationFailure("CANDIDATE_MIGRATION_CONTRACT_UNPROVEN");
        for (var i = 0; i < suffix.Length; i++)
        {
            var entry = suffix[i];
            if (entry is null || !entry.Supported || entry.Id != expected[i].Item1 ||
                string.CompareOrdinal(previous, entry.Id) >= 0 || Digest(entry) != expected[i].Item2)
                throw new ProbeObservationFailure("CANDIDATE_MIGRATION_CONTRACT_UNPROVEN");
            previous = entry.Id;
        }
        if (suffix[^1].Id == CandidateContract.BrowserMigration) return "approved-prefix-reviewed-browser-entry";
        if (suffix[^1].Id == ManagementMigration) return "approved-prefix-reviewed-management-entry";
        if (suffix[^1].Id == TrainingMigration) return "approved-prefix-reviewed-training-entries";
        return suffix[^1].Id == PaymentMigration
            ? "approved-prefix-reviewed-canonical-payment-entries"
            : "approved-prefix-reviewed-receptionist-entry";
    }

    // Invoked by the existing credential-free SQL contract gate. No connection
    // is opened, and this mode emits no observation or release-admission receipt.
    internal static void Test(CandidateContract.Contract current)
    {
        void Require(bool condition)
        {
            if (!condition) throw new InvalidOperationException("Trusted contract regression failed.");
        }
        void Reject(Action action)
        {
            var refused = false;
            try { action(); }
            catch (ProbeObservationFailure) { refused = true; }
            catch (InvalidOperationException) { refused = true; }
            catch (JsonException) { refused = true; }
            Require(refused);
        }
        Require(Prove(current, current) == "approved-definitions-exact");
        Reject(() => Prove(current, current with { SchemaVersion = 2 }));
        Reject(() => Prove(current, current with { Migrations = current.Migrations.Skip(1).ToArray() }));
        var unknown = new CandidateContract.Entry("29990101000000_Unreviewed", false, [], [], [], [], []);
        Reject(() => Prove(current, current with { Migrations = [.. current.Migrations, unknown] }));
        var receptionistIndex = Array.FindIndex(current.Migrations, entry => entry.Id == ReviewedMigration);
        var complete = current;
        if (receptionistIndex >= 0)
            current = current with { Migrations = current.Migrations.Take(receptionistIndex + 1).ToArray() };
        if (current.Migrations[^1].Id == ReviewedMigration)
        {
            Require(Digest(current.Migrations[^1]) == ReviewedEntrySha256);
            var prefix = current with { Migrations = current.Migrations[..^1] };
            Require(Prove(prefix, current) == "approved-prefix-reviewed-receptionist-entry");
            Reject(() => Prove(prefix, current with { Migrations = [.. current.Migrations, unknown] }));
            var changedPrefix = current.Migrations.ToArray();
            changedPrefix[0] = changedPrefix[0] with { Supported = !changedPrefix[0].Supported };
            Reject(() => Prove(prefix, current with { Migrations = changedPrefix }));
            var changedEntry = current.Migrations.ToArray();
            changedEntry[^1] = changedEntry[^1] with { Supported = false };
            Reject(() => Prove(prefix, current with { Migrations = changedEntry }));
            changedEntry[^1] = current.Migrations[^1] with { ForeignKeys = [] };
            Reject(() => Prove(prefix, current with { Migrations = changedEntry }));
            Console.WriteLine("LEGEND_REVIEWED_APPEND_TEST:PROVEN");
        }
        else Console.WriteLine("LEGEND_REVIEWED_APPEND_TEST:NOT_PRESENT_IN_ASSEMBLY");
        current = complete;
        var paymentIndex = Array.FindIndex(current.Migrations, entry => entry.Id == PaymentMigration);
        if (paymentIndex >= 0) current = current with { Migrations = current.Migrations.Take(paymentIndex + 1).ToArray() };
        if (current.Migrations[^1].Id == PaymentMigration)
        {
            Require(Digest(current.Migrations[^1]) == PaymentEntrySha256);
            foreach (var count in new[] { 1, 2 })
            {
                var prefix = current with { Migrations = current.Migrations[..^count] };
                Require(Prove(prefix, current) == "approved-prefix-reviewed-canonical-payment-entries");
                var altered = current.Migrations.ToArray();
                altered[^1] = altered[^1] with { Indexes = [] };
                Reject(() => Prove(prefix, current with { Migrations = altered }));
                altered = current.Migrations.ToArray();
                altered[0] = altered[0] with { Supported = !altered[0].Supported };
                Reject(() => Prove(prefix, current with { Migrations = altered }));
                Reject(() => Prove(prefix, current with { Migrations = [.. current.Migrations, unknown] }));
            }
            var beforeBoth = current with { Migrations = current.Migrations[..^2] };
            Reject(() => Prove(beforeBoth, current with { Migrations = [.. current.Migrations[..^2], current.Migrations[^1]] }));
            Reject(() => Prove(beforeBoth, current with { Migrations = [.. current.Migrations[..^2], current.Migrations[^1], current.Migrations[^2]] }));
            Reject(() => Prove(beforeBoth, current with { Migrations = [.. current.Migrations[..^2], current.Migrations[^1], current.Migrations[^1]] }));
            Console.WriteLine("LEGEND_CANONICAL_PAYMENT_APPEND_TEST:PROVEN");
        }
        else Console.WriteLine("LEGEND_CANONICAL_PAYMENT_APPEND_TEST:NOT_PRESENT_IN_ASSEMBLY");
        current = complete;
        var trainingIndex = Array.FindIndex(current.Migrations, entry => entry.Id == TrainingMigration);
        if (trainingIndex >= 0) current = current with { Migrations = current.Migrations.Take(trainingIndex + 1).ToArray() };
        if (current.Migrations[^1].Id == TrainingMigration)
        {
            Require(Digest(current.Migrations[^1]) == TrainingEntrySha256);
            foreach (var count in new[] { 1, 2, 3 })
            {
                var prefix = current with { Migrations = current.Migrations[..^count] };
                Require(Prove(prefix, current) == "approved-prefix-reviewed-training-entries");
                foreach (var bad in new[] { current.Migrations[^1] with { Guards = [] },
                    current.Migrations[^1] with { Indexes = [] }, current.Migrations[^1] with { ForeignKeys = [] },
                    current.Migrations[^1] with { Columns = current.Migrations[^1].Columns[..^1] } })
                    Reject(() => Prove(prefix, current with { Migrations = [.. current.Migrations[..^1], bad] }));
            }
            Console.WriteLine("LEGEND_TRAINING_APPEND_TEST:PROVEN");
        }
        current = complete;
        var managementIndex = Array.FindIndex(current.Migrations, entry => entry.Id == ManagementMigration);
        if (managementIndex >= 0) current = current with { Migrations = current.Migrations.Take(managementIndex + 1).ToArray() };
        if (current.Migrations[^1].Id == ManagementMigration)
        {
            Require(Digest(current.Migrations[^1]) == ManagementEntrySha256);
            foreach (var count in new[] { 1, 2, 3, 4 })
            {
                var prefix = current with { Migrations = current.Migrations[..^count] };
                Require(Prove(prefix, current) == "approved-prefix-reviewed-management-entry");
                var entry = current.Migrations[^1];
                foreach (var bad in new[] { entry with { Tables = entry.Tables[..^1] }, entry with { Indexes = [] },
                    entry with { Supported = false }, entry with { Id = "29990101000000_Unreviewed" } })
                    Reject(() => Prove(prefix, current with { Migrations = [.. current.Migrations[..^1], bad] }));
                if (count > 1) Reject(() => Prove(prefix, current with { Migrations = [.. prefix.Migrations, entry] }));
            }
            Console.WriteLine("LEGEND_MANAGEMENT_APPEND_TEST:PROVEN");
        }
        current = complete;
        var browserIndex = Array.FindIndex(current.Migrations, entry => entry.Id == CandidateContract.BrowserMigration);
        if (browserIndex >= 0) current = current with { Migrations = current.Migrations.Take(browserIndex + 1).ToArray() };
        if (current.Migrations[^1].Id == CandidateContract.BrowserMigration)
        {
            Require(Digest(current.Migrations[^1]) == BrowserEntrySha256);
            foreach (var count in new[] { 1, 2, 3, 4, 5 })
            {
                var prefix = current with { Migrations = current.Migrations[..^count] };
                Require(Prove(prefix, current) == "approved-prefix-reviewed-browser-entry");
                var entry = current.Migrations[^1];
                foreach (var bad in new[] { entry with { Guards = [] }, entry with { Indexes = [] },
                    entry with { Columns = entry.Columns[..^1] }, entry with { Supported = false } })
                    Reject(() => Prove(prefix, current with { Migrations = [..current.Migrations[..^1], bad] }));
                if (count > 1) Reject(() => Prove(prefix, current with { Migrations = [..prefix.Migrations, entry] }));
            }
            Console.WriteLine("LEGEND_BROWSER_APPEND_TEST:PROVEN");
        }
        current = complete;
        if (current.Migrations[^1].Id == CandidateContract.DiagnosticsMigration)
        {
            var entry = current.Migrations[^1];
            Require(Digest(entry) == CandidateContract.DiagnosticsEntrySha256);
            var prefix = current with { Migrations = current.Migrations[..^1] };
            Require(Prove(prefix, current) == "approved-prefix-reviewed-diagnostics-entry");
            foreach (var bad in new[] { entry with { Supported = false }, entry with { Columns = [] },
                entry with { Columns = [entry.Columns[0] with { Nullable = false }] },
                entry with { Columns = [entry.Columns[0] with { Type = "nvarchar(4096)" }] },
                entry with { Columns = [entry.Columns[0] with { Name = "Unreviewed" }] },
                entry with { Columns = [entry.Columns[0], entry.Columns[0]] } })
                Reject(() => Prove(prefix, current with { Migrations = [..prefix.Migrations, bad] }));
            Reject(() => Prove(prefix with { Migrations = prefix.Migrations[..^1] }, current));
            Reject(() => Prove(prefix, current with { Migrations = [..current.Migrations, unknown] }));
            var changed = prefix.Migrations.ToArray(); changed[0] = changed[0] with { Supported = !changed[0].Supported };
            Reject(() => Prove(prefix, current with { Migrations = [..changed, entry] }));
            var oversized = prefix with { Migrations = [..Enumerable.Repeat(prefix.Migrations[0], 9999), prefix.Migrations[^1]] };
            Reject(() => Prove(oversized, oversized with { Migrations = [..oversized.Migrations, entry] }));
            Reject(() => Prove(prefix, current with { Migrations = [..prefix.Migrations, null!] }));
            Console.WriteLine("LEGEND_DIAGNOSTICS_APPEND_TEST:PROVEN");
        }
        var path = Path.Combine(Path.GetTempPath(), "legend-contract-test-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var json = JsonSerializer.Serialize(current, CandidateContract.Json);
            File.WriteAllText(path, json);
            Require(Digest(Read(path)) == Digest(current));
            foreach (var invalid in new[]
            {
                json.Replace("\"schemaVersion\":3", "\"schemaVersion\":3,\"schemaVersion\":3", StringComparison.Ordinal),
                json.Insert(1, "\"unknown\":false,"),
                json.Replace("\"schemaVersion\":3,", "", StringComparison.Ordinal),
                json.Replace("\"id\":", "\"unknown\":false,\"id\":", StringComparison.Ordinal),
                json.Replace("\"supported\":", "\"supported\":false,\"supported\":", StringComparison.Ordinal)
            })
            {
                Require(invalid != json);
                File.WriteAllText(path, invalid);
                Reject(() => Read(path));
            }
        }
        finally { File.Delete(path); }
        Console.WriteLine("LEGEND_TRUSTED_CONTRACT_TESTS:PASS");
    }
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
