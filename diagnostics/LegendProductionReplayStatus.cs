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
        CommandTimeout = 30
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
FROM [LegendConnectRuntimePolicies];
""");

await PrintQueryAsync(connection, "HISTORICAL WORK BY STATE", """
SELECT
    [EvaluatorVersion],
    [Phase],
    [WorkKind],
    [ProcessingState],
    COUNT_BIG(*) AS WorkCount,
    MIN([CreatedUtc]) AS OldestCreatedUtc,
    MAX([UpdatedUtc]) AS LatestUpdatedUtc,
    SUM(CASE WHEN [LeaseExpiresUtc] IS NOT NULL AND [LeaseExpiresUtc] < SYSUTCDATETIME() AND [ProcessingState] = 'Processing' THEN 1 ELSE 0 END) AS ExpiredProcessingLeases
FROM [LegendHistoricalReevaluationWorkItems]
GROUP BY [EvaluatorVersion], [Phase], [WorkKind], [ProcessingState]
ORDER BY [EvaluatorVersion] DESC, [Phase], [WorkKind], [ProcessingState];
""");

await PrintQueryAsync(connection, "FAILED HISTORICAL WORK CODES", """
SELECT TOP (50)
    [EvaluatorVersion],
    [Phase],
    [WorkKind],
    [LastErrorCode],
    COUNT_BIG(*) AS FailureCount,
    MAX([UpdatedUtc]) AS LatestUpdatedUtc
FROM [LegendHistoricalReevaluationWorkItems]
WHERE [ProcessingState] = 'Failed'
GROUP BY [EvaluatorVersion], [Phase], [WorkKind], [LastErrorCode]
ORDER BY COUNT_BIG(*) DESC, MAX([UpdatedUtc]) DESC;
""");

await PrintQueryAsync(connection, "FOUNDER CURRICULUM MANIFEST STATUS", """
SELECT
    [TargetLanguageIntelligenceEvaluatorVersion] AS TargetEvaluator,
    [CompletedLanguageIntelligenceEvaluatorVersion] AS CompletedEvaluator,
    [ProcessingState],
    COUNT_BIG(*) AS ManifestCount,
    SUM(CAST([FamilyCount] AS bigint)) AS FamilyCount,
    MIN([CreatedUtc]) AS OldestCreatedUtc,
    MAX([UpdatedUtc]) AS LatestUpdatedUtc
FROM [LegendCurriculumManifestWorkItems]
GROUP BY [TargetLanguageIntelligenceEvaluatorVersion], [CompletedLanguageIntelligenceEvaluatorVersion], [ProcessingState]
ORDER BY [TargetLanguageIntelligenceEvaluatorVersion] DESC, [CompletedLanguageIntelligenceEvaluatorVersion] DESC, [ProcessingState];
""");

await PrintQueryAsync(connection, "FOUNDER RAW SUBMISSION REPLAY STATUS", """
SELECT
    [CompletedLanguageIntelligenceEvaluatorVersion] AS CompletedEvaluator,
    [ProcessingState],
    COUNT_BIG(*) AS SubmissionCount,
    MIN([CreatedUtc]) AS OldestCreatedUtc,
    MAX([UpdatedUtc]) AS LatestUpdatedUtc
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
