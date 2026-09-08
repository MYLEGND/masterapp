using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Domain.Messaging;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using SqlEvidence = AgentPortal.Tests.LegendFounderCurriculumSqlServerE2ETests.ReadOnlyLegendDbCommandInterceptor;
using RuntimeEvidence = AgentPortal.Tests.LegendFounderCurriculumSqlServerE2ETests.ExceptionCapturingLoggerProvider;

namespace AgentPortal.Tests;

// These execute SQLite commands through real EF interception hooks. They prove
// diagnostic instrumentation, never production SQL Server or application recovery.
public sealed class LegendProductionDiagnosticEvidenceTests
{
    [Fact]
    public async Task CaughtDatabaseFailure_RemainsVisibleAfterTheNextCommandSucceeds()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var evidence = new SqlEvidence();
        await using var db = CreateDb(connection, evidence);
        const string privateMarker = "private_prompt_and_credential_marker";
        var caught = false;
        try
        {
            await db.Database.SqlQueryRaw<int>(
                "SELECT 1 AS Value FROM [private_prompt_and_credential_marker]").ToArrayAsync();
        }
        catch (SqliteException exception)
        {
            caught = true;
            Assert.Contains(privateMarker, exception.Message);
        }

        Assert.True(caught, "The negative case must reach and fail inside the database provider.");
        Assert.Equal(1, Assert.Single(await db.Database.SqlQueryRaw<int>("SELECT 1 AS Value").ToArrayAsync()));
        var snapshot = evidence.SnapshotDiagnostics();
        Assert.Equal(2L, snapshot.EventsObserved);
        Assert.Equal(1L, snapshot.FailedEvents);
        Assert.Equal(1L, snapshot.SucceededEvents);
        Assert.Equal(new[] { "failed", "succeeded" }, snapshot.Records.Select(record => record.Outcome));
        var failure = snapshot.Records[0];
        Assert.Equal(typeof(SqliteException).FullName, failure.ExceptionType);
        Assert.NotNull(failure.HResult);
        Assert.Null(failure.SqlErrorNumber); // SQLite cannot supply a SQL Server error number.
        Assert.All(snapshot.Records, record =>
        {
            Assert.Matches("^[0-9a-f]{64}$", record.QueryFingerprint);
            Assert.True(record.ElapsedMilliseconds >= 0);
        });
        var report = JsonSerializer.Serialize(snapshot);
        Assert.DoesNotContain(privateMarker, report, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SELECT", report, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("no such table", report, StringComparison.OrdinalIgnoreCase);

        evidence.ResetDiagnostics();
        Assert.Empty(evidence.SnapshotDiagnostics().Records);
        Assert.Equal(0L, evidence.SnapshotDiagnostics().EventsObserved);
        Assert.Equal(2, snapshot.Records.Count);
        Assert.Equal(1L, snapshot.FailedEvents);
        Assert.Equal(2, evidence.SelectCommands); // Reset must not erase cumulative boundary accounting.
    }

    [Fact]
    public async Task CaughtRowMaterializationFailure_IsNotErasedBySuccessfulReaderExecution()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var sqlEvidence = new SqlEvidence();
        using var runtimeEvidence = new RuntimeEvidence();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(runtimeEvidence));
        await using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseSqlite(connection).AddInterceptors(sqlEvidence).UseLoggerFactory(loggerFactory).Options);

        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.SqlQueryRaw<int>(
            "SELECT NULL AS Value").ToArrayAsync());
        Assert.Equal(1, Assert.Single(await db.Database.SqlQueryRaw<int>("SELECT 1 AS Value").ToArrayAsync()));

        var commands = sqlEvidence.SnapshotDiagnostics();
        Assert.Equal(2L, commands.SucceededEvents); // Both readers opened; the first row could not materialize.
        Assert.Equal(0L, commands.FailedEvents);
        var runtime = runtimeEvidence.SnapshotDiagnostics();
        Assert.True(runtime.ExceptionEvents > 0);
        Assert.True(runtime.SqlFailureEvents > 0,
            "EF query enumeration failure is evidence of a failed relational query even without a SQL Server error number.");
        var diagnosis = LegendFounderCurriculumSqlServerE2ETests.DiagnoseObservedFailure(
            "passed", "execution", null, true, runtime);
        Assert.Equal("observed_sql_failure", diagnosis.Classification);
        Assert.Equal("observed_failure_only", diagnosis.RootCauseStatus);
    }

    [Fact]
    public async Task QueryFingerprint_DoesNotDependOnLiteralParameterOrCommentContents()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var evidence = new SqlEvidence();
        await using var db = CreateDb(connection, evidence);
        Assert.Equal("private_literal_one", Assert.Single(await db.Database.SqlQueryRaw<string>(
            "SELECT 'private_literal_one' AS Value -- private_comment_one").ToArrayAsync()));
        Assert.Equal("private_literal_two", Assert.Single(await db.Database.SqlQueryRaw<string>(
            "SELECT 'private_literal_two' AS Value -- private_comment_two").ToArrayAsync()));
        Assert.Equal("private_parameter_one", Assert.Single(await db.Database.SqlQueryRaw<string>(
            "SELECT @private_name_one AS Value", new SqliteParameter("@private_name_one", "private_parameter_one")).ToArrayAsync()));
        Assert.Equal("private_parameter_two", Assert.Single(await db.Database.SqlQueryRaw<string>(
            "SELECT @private_name_two AS Value", new SqliteParameter("@private_name_two", "private_parameter_two")).ToArrayAsync()));

        var snapshot = evidence.SnapshotDiagnostics();
        Assert.Equal(4L, snapshot.SucceededEvents);
        Assert.Equal(snapshot.Records[0].QueryFingerprint, snapshot.Records[1].QueryFingerprint);
        Assert.Equal(snapshot.Records[2].QueryFingerprint, snapshot.Records[3].QueryFingerprint);
        Assert.DoesNotContain("private_", JsonSerializer.Serialize(snapshot), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DiagnosticCapacity_DropsRecordsExplicitlyWithoutDroppingFailureCounters()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var evidence = new SqlEvidence();
        await using var db = CreateDb(connection, evidence);
        for (var index = 0; index < 256; index++)
            Assert.Equal(1, Assert.Single(await db.Database.SqlQueryRaw<int>("SELECT 1 AS Value").ToArrayAsync()));
        var beforeOverflow = evidence.SnapshotDiagnostics();
        Assert.False(beforeOverflow.Truncated);
        Assert.Equal(0L, beforeOverflow.RecordsDropped);

        await Assert.ThrowsAsync<SqliteException>(() => db.Database.SqlQueryRaw<int>(
            "SELECT 1 AS Value FROM [missing_after_diagnostic_capacity]").ToArrayAsync());
        var afterOverflow = evidence.SnapshotDiagnostics();
        Assert.True(afterOverflow.Truncated);
        Assert.Equal(257L, afterOverflow.EventsObserved);
        Assert.Equal(256L, afterOverflow.SucceededEvents);
        Assert.Equal(1L, afterOverflow.FailedEvents);
        Assert.Equal(1L, afterOverflow.RecordsDropped);
        Assert.Equal(256, afterOverflow.Records.Count);
        Assert.Equal(Enumerable.Range(2, 256).Select(value => (long)value),
            afterOverflow.Records.Select(record => record.Ordinal));
        Assert.Equal("failed", afterOverflow.Records[^1].Outcome);
        Assert.False(beforeOverflow.Truncated);
        Assert.Equal(0L, beforeOverflow.FailedEvents);
    }

    [Fact]
    public async Task DeniedPhysicalTable_IsRecordedAndResetCannotEraseTheBoundaryViolation()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var evidence = new SqlEvidence(restrictPhysicalTables: true);
        await using var db = CreateDb(connection, evidence);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Database.SqlQueryRaw<int>(
            "SELECT 1 AS Value FROM [unapproved_private_table]").ToArrayAsync());
        var snapshot = evidence.SnapshotDiagnostics();
        Assert.True(snapshot.BlockedEvents > 0);
        Assert.Contains(snapshot.Records, record => record.Outcome == "blocked");
        Assert.Equal(0L, snapshot.SucceededEvents);
        Assert.Equal(0, evidence.SelectCommands);
        Assert.Equal(1, evidence.BlockedCommands);
        Assert.DoesNotContain("unapproved_private_table", JsonSerializer.Serialize(snapshot), StringComparison.OrdinalIgnoreCase);

        evidence.ResetDiagnostics();
        Assert.Empty(evidence.SnapshotDiagnostics().Records);
        Assert.Equal(1, evidence.BlockedCommands);
        Assert.Equal(1, Assert.Single(await db.Database.SqlQueryRaw<int>("SELECT 1 AS Value").ToArrayAsync()));
        Assert.Equal(1, evidence.BlockedCommands);
    }

    [Fact]
    public void CapturedRuntimeException_CannotKeepACompletedOutcomeOrLeakPrivateContent()
    {
        using var evidence = new RuntimeEvidence();
        var logger = evidence.CreateLogger("Infrastructure.Messaging.LegendConnectTranslationRouter");
        var fields = RuntimeFields("completed");
        fields["ReasonCode"] = "private prompt with token=private-secret";
        fields["Prompt"] = "private raw prompt";
        fields["ConnectionString"] = "Password=private-password";
        fields["LanguagesAnalyzed"] = 2;
        using var scope = logger.BeginScope("private scope password");
        logger.Log(LogLevel.Error, new EventId(0), fields,
            new InvalidOperationException("private exception credential"),
            (_, _) => throw new InvalidOperationException("The public collector must never invoke a log formatter."));

        var snapshot = evidence.SnapshotDiagnostics();
        Assert.Equal(1L, snapshot.ExceptionEvents);
        var recorded = Assert.Single(snapshot.Records);
        Assert.Equal("failed", recorded.Outcome);
        Assert.Equal("LegendConnectTranslationRouter.DetectLanguageAsync", recorded.AuthorityMethod);
        Assert.Equal("InvalidOperationException", recorded.ExceptionType);
        Assert.Equal(2L, recorded.Counts["LanguagesAnalyzed"]);
        Assert.False(string.IsNullOrWhiteSpace(recorded.Stage));
        Assert.DoesNotContain("private", JsonSerializer.Serialize(snapshot), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RuntimeEventWithoutOutcome_IsExplicitlyUnreportedAndNeverCompleted()
    {
        using var evidence = new RuntimeEvidence();
        var fields = RuntimeFields("completed");
        fields.Remove("Outcome");
        fields["AuthorityMethod"] = "private_unknown_authority.DetectLanguageAsync";
        fields["LanguagesAnalyzed"] = -2;
        var logger = evidence.CreateLogger("private_category_with_password");
        logger.Log(LogLevel.Information, new EventId(0), fields, null, (_, _) => "private rendered text");

        var snapshot = evidence.SnapshotDiagnostics();
        var recorded = Assert.Single(snapshot.Records);
        Assert.Equal("unreported", recorded.Outcome);
        Assert.Equal("unmapped_runtime_authority", recorded.SourcePath);
        Assert.Equal("unresolved_from_capture", recorded.AuthorityMethod);
        Assert.DoesNotContain("LanguagesAnalyzed", recorded.Counts.Keys);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(snapshot), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RuntimeCapacity_ReportsDroppedEventsAndRetainsFailureAccountingAfterReset()
    {
        using var evidence = new RuntimeEvidence(maxEvents: 2);
        var logger = evidence.CreateLogger("Infrastructure.Messaging.LegendConnectTranslationRouter");
        for (var index = 0; index < 2; index++)
            logger.Log(LogLevel.Information, new EventId(0), RuntimeFields("completed"), null, (_, _) => "unused");
        var beforeOverflow = evidence.SnapshotDiagnostics();
        logger.Log(LogLevel.Error, new EventId(0), RuntimeFields("failed"),
            new InvalidOperationException("private suppressed failure"), (_, _) => "unused");

        var snapshot = evidence.SnapshotDiagnostics();
        Assert.Equal(3L, snapshot.ObservedEvents);
        Assert.Equal(1L, snapshot.ExceptionEvents);
        Assert.True(snapshot.Truncated);
        Assert.Equal(1L, snapshot.RecordsDropped);
        Assert.Equal(2, snapshot.Records.Count);
        Assert.False(beforeOverflow.Truncated);
        Assert.Equal(0L, beforeOverflow.ExceptionEvents);
        evidence.ResetDiagnostics();
        Assert.Equal(0L, evidence.SnapshotDiagnostics().ObservedEvents);
        Assert.Equal(1L, snapshot.ExceptionEvents);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(snapshot), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RuntimeSnapshot_RecordListAndCountsCannotBeMutatedByItsConsumer()
    {
        using var evidence = new RuntimeEvidence();
        var fields = RuntimeFields("resolved");
        fields["LanguagesAnalyzed"] = 1;
        evidence.CreateLogger("Infrastructure.Messaging.LegendConnectTranslationRouter")
            .Log(LogLevel.Information, new EventId(0), fields, null, (_, _) => "unused");
        var snapshot = evidence.SnapshotDiagnostics();
        var records = Assert.IsAssignableFrom<IList<LegendFounderCurriculumSqlServerE2ETests.RuntimeDiagnosticEvent>>(snapshot.Records);
        Assert.Throws<NotSupportedException>(() => records[0] = records[0]);
        var counts = Assert.IsAssignableFrom<IDictionary<string, long>>(records[0].Counts);
        Assert.Throws<NotSupportedException>(() => counts["LanguagesAnalyzed"] = 999);
        Assert.Equal(1L, records[0].Counts["LanguagesAnalyzed"]);
    }

    [Fact]
    public void CurriculumDiagnostic_PreservesObservedBoundCountsAndFractionalElapsedTime()
    {
        using var evidence = new RuntimeEvidence();
        var fields = RuntimeFields("rejected");
        fields["Event"] = "stage_completed";
        fields["AuthorityMethod"] = "LegendConnectCurriculumService.ReadReusableMeaningCandidatesAsync";
        fields["Stage"] = "meaning_candidates";
        fields["ReasonCode"] = "meaning_graph_processing_bound_exceeded";
        fields["ElapsedMs"] = 2.25d;
        fields["TokenCount"] = 513;
        fields["CandidateCount"] = null;
        fields["Bound"] = 512;
        evidence.CreateLogger("Infrastructure.Messaging.LegendConnectCurriculumService")
            .Log(LogLevel.Information, new EventId(0), fields, null, (_, _) => "unused");

        var record = Assert.Single(evidence.SnapshotDiagnostics().Records);
        Assert.Equal("rejected", record.Outcome);
        Assert.Equal(513L, record.Counts["TokenCount"]);
        Assert.Equal(512L, record.Counts["Bound"]);
        Assert.DoesNotContain("CandidateCount", record.Counts.Keys);
        Assert.NotNull(record.ElapsedMs);
        Assert.InRange((double)record.ElapsedMs!.Value, 2.25d, 3d);
    }

    [Theory]
    [InlineData("passed")]
    [InlineData("failed")]
    public void ObservedSqlFailure_OverridesNominalCaseStatusWithoutClaimingAProvenRepair(string nominalStatus)
    {
        using var evidence = new RuntimeEvidence();
        var diagnosis = LegendFounderCurriculumSqlServerE2ETests.DiagnoseObservedFailure(
            nominalStatus, "execution", "case_execution_failed", true,
            evidence.SnapshotDiagnostics(), sqlFailureEvents: 1);

        Assert.Equal("observed_sql_failure", diagnosis.Classification);
        Assert.Equal("observed_failure_only", diagnosis.RootCauseStatus);
        Assert.Equal("unresolved_from_capture", diagnosis.AuthorityMethod);
        Assert.False(string.IsNullOrWhiteSpace(diagnosis.NextVerification));
        Assert.DoesNotContain("proven repair", diagnosis.NextVerification, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fixed", diagnosis.NextVerification, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("LegendConnectCurriculumService.AnalyzeReusableMeaningGraphAsync", "Infrastructure/Messaging/LegendConnectCurriculum.cs")]
    [InlineData("AzureTranslatorService.DetectLanguageAsync", "Infrastructure/Messaging/AzureTranslatorService.cs")]
    public void RouterTelemetry_PreservesTheActualCalledAuthorityInsteadOfInventingARouterMethod(
        string calledAuthority, string sourcePath)
    {
        using var evidence = new RuntimeEvidence();
        var fields = RuntimeFields("failed");
        fields["AuthorityMethod"] = calledAuthority;
        fields["ReasonCode"] = "authority_exception";
        evidence.CreateLogger("Infrastructure.Messaging.LegendConnectTranslationRouter")
            .Log(LogLevel.Information, new EventId(0), fields, null, (_, _) => "unused");

        var record = Assert.Single(evidence.SnapshotDiagnostics().Records);
        Assert.Equal(calledAuthority, record.AuthorityMethod);
        Assert.Equal(sourcePath, record.SourcePath);
    }

    [Theory]
    [InlineData("source_private_password")]
    [InlineData("native_private_token_123")]
    [InlineData("meaning_private_prompt_456")]
    [InlineData("private_marker")]
    [InlineData("meaning_graph_private_secret")]
    [InlineData("source_language_private_secret")]
    public void PublicDiagnostic_RejectsPrivateTokensEvenWhenTheyResembleAReasonCode(string privateReason)
    {
        using var evidence = new RuntimeEvidence();
        var fields = RuntimeFields("unresolved");
        fields["ReasonCode"] = privateReason;
        evidence.CreateLogger("Infrastructure.Messaging.LegendConnectTranslationRouter")
            .Log(LogLevel.Information, new EventId(0), fields, null, (_, _) => "unused");

        var snapshot = evidence.SnapshotDiagnostics();
        Assert.Equal("unclassified_reason", Assert.Single(snapshot.Records).ReasonCode);
        Assert.DoesNotContain(privateReason, JsonSerializer.Serialize(snapshot), StringComparison.Ordinal);
    }

    [Fact]
    public void RecoveredOptionalCandidateRead_DoesNotReplaceTheLaterFailedNativeBoundary()
    {
        using var evidence = new RuntimeEvidence();
        var logger = evidence.CreateLogger("Infrastructure.Messaging.LegendConnectTranslationRouter");
        var candidateRead = RuntimeFields("failed");
        candidateRead["Event"] = "LanguageCandidatesRead";
        candidateRead["AuthorityMethod"] = "LegendConnectCurriculumService.GetReusableMeaningLanguageCandidatesAsync";
        candidateRead["Stage"] = "language_candidate_prefilter";
        candidateRead["ReasonCode"] = "candidate_read_failed";
        logger.Log(LogLevel.Information, new EventId(0), candidateRead, null, (_, _) => "unused");
        var terminal = RuntimeFields("unresolved");
        terminal["Event"] = "NativeInferenceCompleted";
        terminal["AuthorityMethod"] = "LegendConnectOperations.TryInferConversationWithDiscourseAsync";
        terminal["Stage"] = "native_inference";
        terminal["ReasonCode"] = "meaning_graph_component_unknown";
        terminal["Supported"] = false;
        logger.Log(LogLevel.Information, new EventId(0), terminal, null, (_, _) => "unused");

        var diagnosis = LegendFounderCurriculumSqlServerE2ETests.DiagnoseObservedFailure(
            "failed", "execution", "case_execution_failed", true, evidence.SnapshotDiagnostics());
        Assert.Equal("native_inference", diagnosis.ObservedStage);
        Assert.Equal("meaning_graph_component_unknown", diagnosis.ObservedReason);
        Assert.Equal("LegendConnectOperations.TryInferConversationWithDiscourseAsync", diagnosis.AuthorityMethod);
    }

    [Fact]
    public async Task NativeLanguageFailure_ReportsItsActualBoundaryWithoutPromptOrProviderParticipation()
    {
        using var evidence = new RuntimeEvidence();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(evidence));
        var registry = new Mock<ILegendLanguageRegistry>(MockBehavior.Strict);
        registry.Setup(value => value.ListEnabledTranslationLanguagesReadOnlyAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new LegendLanguageDefinitionSnapshot("en", "en", "English", "English",
                true, true, true, "/en", "/en") });
        var graph = new Mock<ILegendConnectStructuralCompositionGate>(MockBehavior.Strict);
        graph.Setup(value => value.GetReusableMeaningLanguageCandidatesAsync(
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LegendConnectMeaningLanguageCandidates(false, ["en"], "prefilter_not_available"));
        graph.Setup(value => value.AnalyzeReusableMeaningGraphAsync("en", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LegendConnectUtteranceMeaningGraphSnapshot(false, [], [], [], "private reason with credential"));
        var provider = new Mock<ITranslationProvider>(MockBehavior.Strict);
        var router = new LegendConnectTranslationRouter(provider.Object, registry.Object,
            Mock.Of<ITranslationCapacityAuthority>(MockBehavior.Strict),
            loggerFactory.CreateLogger<LegendConnectTranslationRouter>(), structuralComposition: graph.Object);

        var result = await router.DetectLanguageAsync("private raw user prompt with password", CancellationToken.None,
            LegendConnectExternalProviderPolicy.NativeOnly);

        Assert.False(result.Succeeded);
        Assert.Equal("native_only_governed_source_language_undetermined", result.ErrorCode);
        Assert.Empty(provider.Invocations);
        var snapshot = evidence.SnapshotDiagnostics();
        var terminal = Assert.Single(snapshot.Records.Where(record => record.Event == "LanguageDetectionCompleted"));
        Assert.Equal("unresolved", terminal.Outcome);
        Assert.Equal(result.ErrorCode, terminal.ReasonCode);
        Assert.Equal("native_only", terminal.ProviderPolicy);
        var analyzed = Assert.Single(snapshot.Records.Where(record => record.Event == "LanguageGraphAnalyzed"));
        Assert.Equal("uncomposed", analyzed.Outcome);
        Assert.Equal("unclassified_reason", analyzed.ReasonCode);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(snapshot), StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, object?> RuntimeFields(string outcome) => new()
    {
        ["{OriginalFormat}"] = "LEGEND RuntimeDiagnostic Event={Event} AuthorityMethod={AuthorityMethod} Stage={Stage} Outcome={Outcome}",
        ["Event"] = "LanguageDetectionCompleted",
        ["AuthorityMethod"] = "LegendConnectTranslationRouter.DetectLanguageAsync",
        ["Stage"] = "source_language",
        ["Outcome"] = outcome,
        ["ReasonCode"] = "native_only_governed_source_language_undetermined",
        ["ProviderPolicy"] = "native_only"
    };

    private static MasterAppDbContext CreateDb(SqliteConnection connection, SqlEvidence evidence) =>
        new(new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseSqlite(connection).AddInterceptors(evidence).Options);
}
