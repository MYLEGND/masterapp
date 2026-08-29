#:project ../Infrastructure/Infrastructure.csproj

using Microsoft.Data.SqlClient;
using System.Data;

var connectionString = Environment.GetEnvironmentVariable("LEGEND_PRODUCTION_READONLY_CONNECTION");
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException("LEGEND_PRODUCTION_READONLY_CONNECTION is required.");

var builder = new SqlConnectionStringBuilder(connectionString)
{
    ApplicationName = "LEGEND production replay status read-only diagnostic",
    ApplicationIntent = ApplicationIntent.ReadOnly
};

await using var connection = new SqlConnection(builder.ConnectionString);
await connection.OpenAsync();

static async Task PrintQueryAsync(SqlConnection connection, string title, string sql)
{
    Console.WriteLine($"=== {title} ===");
    await using var command = new SqlCommand(sql, connection)
    {
        // Exact production aggregates can exceed the provider default while
        // the application is serving traffic. Keep this diagnostic read-only
        // and bounded without treating a 30-second reporting timeout as an
        // architectural failure.
        CommandTimeout = 180
    };
    await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess);
    var rowCount = 0;
    while (await reader.ReadAsync())
    {
        rowCount++;
        for (var i = 0; i < reader.FieldCount; i++)
        {
            if (i > 0) Console.Write(" | ");
            var value = await reader.IsDBNullAsync(i) ? "<NULL>" : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture);
            Console.Write($"{reader.GetName(i)}={value}");
        }
        Console.WriteLine();
    }
    if (rowCount == 0) Console.WriteLine("<NONE>");
    Console.WriteLine();
}

await PrintQueryAsync(connection, "RUNTIME REEVALUATION POLICY", """
SELECT
    [TargetLanguageIntelligenceEvaluatorVersion] AS TargetEvaluator,
    [CompletedLanguageIntelligenceEvaluatorVersion] AS CompletedEvaluator,
    [LanguageIntelligenceReevaluationPhase] AS CurrentPhase,
    [LanguageIntelligenceReevaluationStartedUtc] AS ReplayStartedUtc,
    [LanguageIntelligenceReevaluationCompletedUtc] AS ReplayCompletedUtc,
    [CursorReplayCompatibilityEvaluatorVersion] AS CursorCompatibilityEvaluator
FROM [LegendConnectRuntimePolicies]
WHERE [ScopeKey] = 'Global';
""");

// Report the exact existing scheduler boundary first. This query exposes no
// payload or identity and performs no write; it distinguishes an executable
// receipt from retained fail-closed or retired audit history.
await PrintQueryAsync(connection, "SCHEDULER-ELIGIBLE FOUNDER MANIFESTS", """
SELECT
    m.[TargetLanguageIntelligenceEvaluatorVersion] AS TargetEvaluator,
    m.[CompletedLanguageIntelligenceEvaluatorVersion] AS CompletedEvaluator,
    m.[ProcessingState],
    m.[LastErrorCode],
    m.[AttemptCount],
    m.[FamilyCount],
    m.[NextFamilyIndex],
    m.[CreatedUtc],
    m.[UpdatedUtc],
    (SELECT COUNT_BIG(*)
     FROM [LegendHistoricalReevaluationWorkItems] w
     WHERE w.[EvaluatorVersion] = p.[TargetLanguageIntelligenceEvaluatorVersion]
       AND w.[Phase] = 'FounderCurriculum'
       AND w.[SubjectId] = m.[Id]
       AND w.[ProcessingState] = 'Pending') AS PendingChildren,
    (SELECT COUNT_BIG(*)
     FROM [LegendHistoricalReevaluationWorkItems] w
     WHERE w.[EvaluatorVersion] = p.[TargetLanguageIntelligenceEvaluatorVersion]
       AND w.[Phase] = 'FounderCurriculum'
       AND w.[SubjectId] = m.[Id]
       AND w.[ProcessingState] = 'Processing') AS ProcessingChildren,
    (SELECT COUNT_BIG(*)
     FROM [LegendHistoricalReevaluationWorkItems] w
     WHERE w.[EvaluatorVersion] = p.[TargetLanguageIntelligenceEvaluatorVersion]
       AND w.[Phase] = 'FounderCurriculum'
       AND w.[SubjectId] = m.[Id]
       AND w.[ProcessingState] = 'Completed') AS CompletedChildren,
    (SELECT COUNT_BIG(*)
     FROM [LegendHistoricalReevaluationWorkItems] w
     WHERE w.[EvaluatorVersion] = p.[TargetLanguageIntelligenceEvaluatorVersion]
       AND w.[Phase] = 'FounderCurriculum'
       AND w.[SubjectId] = m.[Id]
       AND w.[ProcessingState] IN ('Failed', 'Retired')) AS TerminalChildren
FROM [LegendCurriculumManifestWorkItems] m
CROSS JOIN [LegendConnectRuntimePolicies] p
WHERE p.[ScopeKey] = 'Global'
  AND m.[ProcessingState] <> 'Retired'
  AND (
      m.[ProcessingState] <> 'Failed'
      OR (
          COALESCE(m.[LastErrorCode], '') NOT IN (
              'curriculum_manifest_payload_invalid',
              'curriculum_manifest_payload_mismatch')
          AND m.[TargetLanguageIntelligenceEvaluatorVersion] <
              p.[TargetLanguageIntelligenceEvaluatorVersion]))
  AND (
      m.[ProcessingState] <> 'Completed'
      OR m.[CompletedLanguageIntelligenceEvaluatorVersion] <
          p.[TargetLanguageIntelligenceEvaluatorVersion])
ORDER BY m.[CreatedUtc], m.[UpdatedUtc];
""");

await PrintQueryAsync(connection, "CURRENT-EVALUATOR HISTORICAL WORK BY STATE", """
SELECT
    w.[EvaluatorVersion],
    w.[Phase],
    w.[WorkKind],
    w.[ProcessingState],
    COUNT_BIG(*) AS WorkCount,
    MIN(w.[CreatedUtc]) AS OldestCreatedUtc,
    MAX(w.[UpdatedUtc]) AS LatestUpdatedUtc,
    SUM(CASE WHEN w.[LeaseExpiresUtc] IS NOT NULL
                  AND w.[LeaseExpiresUtc] < SYSUTCDATETIME()
                  AND w.[ProcessingState] = 'Processing'
             THEN 1 ELSE 0 END) AS ExpiredProcessingLeases
FROM [LegendHistoricalReevaluationWorkItems] w
CROSS JOIN [LegendConnectRuntimePolicies] p
WHERE p.[ScopeKey] = 'Global'
  AND w.[EvaluatorVersion] = p.[TargetLanguageIntelligenceEvaluatorVersion]
GROUP BY w.[EvaluatorVersion], w.[Phase], w.[WorkKind], w.[ProcessingState]
ORDER BY w.[Phase], w.[WorkKind], w.[ProcessingState];
""");

await PrintQueryAsync(connection, "CURRENT-EVALUATOR FAILED HISTORICAL WORK CODES", """
SELECT TOP (50)
    w.[EvaluatorVersion],
    w.[Phase],
    w.[WorkKind],
    w.[LastErrorCode],
    COUNT_BIG(*) AS FailureCount,
    MAX(w.[UpdatedUtc]) AS LatestUpdatedUtc
FROM [LegendHistoricalReevaluationWorkItems] w
CROSS JOIN [LegendConnectRuntimePolicies] p
WHERE p.[ScopeKey] = 'Global'
  AND w.[EvaluatorVersion] = p.[TargetLanguageIntelligenceEvaluatorVersion]
  AND w.[ProcessingState] = 'Failed'
GROUP BY w.[EvaluatorVersion], w.[Phase], w.[WorkKind], w.[LastErrorCode]
ORDER BY COUNT_BIG(*) DESC, MAX(w.[UpdatedUtc]) DESC;
""");

await PrintQueryAsync(connection, "FOUNDER CURRICULUM MANIFEST STATUS", """
SELECT
    [TargetLanguageIntelligenceEvaluatorVersion] AS TargetEvaluator,
    [CompletedLanguageIntelligenceEvaluatorVersion] AS CompletedEvaluator,
    [ProcessingState],
    [LastErrorCode],
    COUNT_BIG(*) AS ManifestCount,
    SUM(CAST([FamilyCount] AS bigint)) AS FamilyCount,
    MIN([CreatedUtc]) AS OldestCreatedUtc,
    MAX([UpdatedUtc]) AS LatestUpdatedUtc
FROM [LegendCurriculumManifestWorkItems]
GROUP BY [TargetLanguageIntelligenceEvaluatorVersion],
         [CompletedLanguageIntelligenceEvaluatorVersion],
         [ProcessingState],
         [LastErrorCode]
ORDER BY [TargetLanguageIntelligenceEvaluatorVersion] DESC,
         [CompletedLanguageIntelligenceEvaluatorVersion] DESC,
         [ProcessingState],
         [LastErrorCode];
""");

await PrintQueryAsync(connection, "FOUNDER RAW SUBMISSION REPLAY STATUS", """
SELECT
    [CompletedLanguageIntelligenceEvaluatorVersion] AS CompletedEvaluator,
    [ProcessingState],
    COUNT_BIG(*) AS SubmissionCount,
    MIN([CreatedUtc]) AS OldestCreatedUtc,
    MAX(COALESCE([ProcessedUtc], [CreatedUtc])) AS LatestProcessedOrCreatedUtc
FROM [LegendFounderTrainingSubmissions]
GROUP BY [CompletedLanguageIntelligenceEvaluatorVersion], [ProcessingState]
ORDER BY [CompletedLanguageIntelligenceEvaluatorVersion] DESC, [ProcessingState];
""");

await PrintQueryAsync(connection, "ACTIVE V21 TRANSITION PROJECTION", """
SELECT
    [DerivationEvaluatorVersion],
    [ContributionState],
    [IsHumanVerifiedSupport],
    COUNT_BIG(*) AS TransitionEvidenceCount
FROM [LegendSemanticTransitionEvidence]
WHERE [SupersededUtc] IS NULL
GROUP BY [DerivationEvaluatorVersion], [ContributionState], [IsHumanVerifiedSupport]
ORDER BY [DerivationEvaluatorVersion] DESC, [ContributionState], [IsHumanVerifiedSupport] DESC;
""");

Console.WriteLine("PRODUCTION WRITE COMMANDS: 0");
