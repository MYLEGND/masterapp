#:project ../Infrastructure/Infrastructure.csproj

using Microsoft.Data.SqlClient;
using System.Data;

var connectionString = Environment.GetEnvironmentVariable(
    "LEGEND_PRODUCTION_READONLY_CONNECTION");
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException(
        "LEGEND_PRODUCTION_READONLY_CONNECTION is required.");

var timeoutMinutes = int.TryParse(
    Environment.GetEnvironmentVariable("LEGEND_CONVERGENCE_TIMEOUT_MINUTES"),
    out var configuredTimeout)
        ? Math.Clamp(configuredTimeout, 1, 35)
        : 30;
var deadline = DateTime.UtcNow.AddMinutes(timeoutMinutes);
var verificationStartedUtc = DateTime.UtcNow;
var observedPostDeployHeartbeat = false;
var builder = new SqlConnectionStringBuilder(connectionString)
{
    ApplicationName = "LEGEND unified deployment convergence gate",
    ApplicationIntent = ApplicationIntent.ReadOnly
};

await using var connection = new SqlConnection(builder.ConnectionString);
await connection.OpenAsync();

while (true)
{
    await using var command = new SqlCommand("""
        SELECT
            p.[TargetLanguageIntelligenceEvaluatorVersion],
            p.[CompletedLanguageIntelligenceEvaluatorVersion],
            p.[LanguageIntelligenceReevaluationPhase],
            p.[LastLearningWorkerHeartbeatUtc],
            c.[State],
            c.[EarliestAffectedPhase],
            (SELECT COUNT_BIG(*)
             FROM [LegendHistoricalReevaluationWorkItems] w
             WHERE w.[EvaluatorVersion] = p.[TargetLanguageIntelligenceEvaluatorVersion]
               AND w.[ProcessingState] IN ('Pending', 'Processing', 'Failed')) AS ActiveOrFailedWork,
            (SELECT COUNT_BIG(*)
             FROM [LegendHistoricalReevaluationWorkItems] w
             WHERE w.[EvaluatorVersion] = p.[TargetLanguageIntelligenceEvaluatorVersion]
               AND w.[ProcessingState] = 'Retired'
               AND COALESCE(w.[LastErrorCode], '') <> 'historical_reevaluation_contract_superseded') AS TerminalCanonicalFailures,
            (SELECT COUNT_BIG(*)
             FROM [LegendCurriculumManifestWorkItems] m
             WHERE m.[ProcessingState] <> 'Completed'
                OR m.[CompletedLanguageIntelligenceEvaluatorVersion] <
                   p.[TargetLanguageIntelligenceEvaluatorVersion]) AS IncompleteManifests
        FROM [LegendConnectRuntimePolicies] p
        LEFT JOIN [LegendLanguageDerivationConvergences] c
          ON c.[TargetEvaluatorVersion] =
             p.[TargetLanguageIntelligenceEvaluatorVersion]
        WHERE p.[ScopeKey] = 'Global';
        """, connection)
    {
        CommandTimeout = 30
    };

    await using var reader = await command.ExecuteReaderAsync(
        CommandBehavior.SingleRow);
    if (!await reader.ReadAsync())
        throw new InvalidOperationException(
            "The canonical LEGEND runtime policy is missing.");

    var target = reader.GetInt32(0);
    var completed = reader.GetInt32(1);
    var phase = reader.GetString(2);
    var heartbeat = reader.IsDBNull(3)
        ? (DateTime?)null
        : reader.GetDateTime(3);
    var convergenceState = reader.IsDBNull(4)
        ? null
        : reader.GetString(4);
    var earliestAffectedPhase = reader.IsDBNull(5)
        ? null
        : reader.GetString(5);
    var activeOrFailedWork = reader.GetInt64(6);
    var terminalCanonicalFailures = reader.GetInt64(7);
    var incompleteManifests = reader.GetInt64(8);

    Console.WriteLine(
        $"{DateTime.UtcNow:O} target=v{target} completed=v{completed} " +
        $"phase={phase} convergence={convergenceState ?? "<missing>"} " +
        $"earliest={earliestAffectedPhase ?? "<none>"} " +
        $"active_or_failed={activeOrFailedWork} " +
        $"terminal_failures={terminalCanonicalFailures} " +
        $"incomplete_manifests={incompleteManifests} " +
        $"heartbeat={(heartbeat.HasValue ? heartbeat.Value.ToString("O") : "<missing>")}");

    observedPostDeployHeartbeat |= heartbeat is not null &&
        heartbeat.Value >= verificationStartedUtc.AddMinutes(-5);

    if (terminalCanonicalFailures > 0)
        throw new InvalidOperationException(
            "Canonical convergence contains terminal non-superseded failures.");

    var complete = target > 0 &&
        completed == target &&
        string.Equals(phase, "Complete", StringComparison.Ordinal) &&
        convergenceState is "Completed" or "Reused" &&
        activeOrFailedWork == 0 &&
        incompleteManifests == 0 &&
        observedPostDeployHeartbeat;
    if (complete)
    {
        Console.WriteLine("LEGEND PRODUCTION CONVERGENCE GATE: PASSED");
        Console.WriteLine("PRODUCTION WRITE COMMANDS: 0");
        break;
    }

    if (DateTime.UtcNow >= deadline)
        throw new TimeoutException(
            "LEGEND did not reach complete canonical convergence within the deployment verification window.");

    await Task.Delay(TimeSpan.FromSeconds(15));
}
