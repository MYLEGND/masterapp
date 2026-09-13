using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Domain.Messaging;
using Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendConnectCurriculumRuntimeDiagnosticTests
{
    [Fact]
    public async Task UnknownMeaning_ReportsReturnedReasonAndObservedCountsWithoutRequestContent()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        ControllerTestHelpers.SeedGovernedLanguageBaseline(db);
        var logger = new CapturingLogger();
        var configuration = new ConfigurationBuilder().Build();
        var registry = new LegendLanguageRegistry(db, configuration);
        var corpus = new LegendConnectCorpusService(db, registry, NullLogger<LegendConnectCorpusService>.Instance);
        var curriculum = new LegendConnectCurriculumService(db, registry, corpus, logger: logger);
        const string request = "privateunobservedcontent";

        var graph = await curriculum.AnalyzeReusableMeaningGraphAsync("en", request);

        Assert.False(graph.IsComposed);
        var observed = Assert.Single(logger.Events.Where(item => Equals(item["Stage"], "meaning_graph")));
        Assert.Equal("stage_completed", observed["Event"]);
        Assert.Equal("LegendConnectCurriculumService.AnalyzeReusableMeaningGraphAsync", observed["AuthorityMethod"]);
        Assert.Equal("rejected", observed["Outcome"]);
        Assert.Equal(graph.ReasonCode, observed["ReasonCode"]);
        Assert.Equal(1, observed["TokenCount"]);
        Assert.Equal(graph.Nodes.Count, observed["NodeCount"]);
        Assert.Equal(graph.Relations.Count, observed["RelationCount"]);
        Assert.Equal(graph.UnknownSurfaceComponents.Count, observed["UnknownCount"]);
        Assert.True((double)observed["ElapsedMs"]! >= 0);
        var sourceSlots = Assert.Single(logger.Events.Where(item => Equals(item["Stage"], "source_slots")));
        Assert.Equal(0, sourceSlots["DeclarationCount"]);
        Assert.Null(sourceSlots["TemplateCount"]); // Not evaluated, rather than an invented zero.
        var serialized = JsonSerializer.Serialize(logger.Events);
        Assert.DoesNotContain(request, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(LegendLanguageIdentity.TextHash(request), serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessingBound_ReportsTheRejectingCandidateAuthorityAndAppliedLimit()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        ControllerTestHelpers.SeedGovernedLanguageBaseline(db);
        var logger = new CapturingLogger();
        var registry = new LegendLanguageRegistry(db, new ConfigurationBuilder().Build());
        var corpus = new LegendConnectCorpusService(db, registry, NullLogger<LegendConnectCorpusService>.Instance);
        var curriculum = new LegendConnectCurriculumService(db, registry, corpus, logger: logger);
        var graph = await curriculum.AnalyzeReusableMeaningGraphAsync("en", string.Join(' ', Enumerable.Repeat("private", 513)));
        var firstRejection = logger.Events.First(item => Equals(item["Outcome"], "rejected"));
        Assert.Equal("meaning_graph_processing_bound_exceeded", graph.ReasonCode);
        Assert.Equal(graph.ReasonCode, firstRejection["ReasonCode"]);
        Assert.Equal("meaning_candidates", firstRejection["Stage"]);
        Assert.Equal("LegendConnectCurriculumService.ReadReusableMeaningCandidatesAsync", firstRejection["AuthorityMethod"]);
        Assert.Equal(513, firstRejection["TokenCount"]);
        Assert.Equal(512, firstRejection["Bound"]);
        Assert.DoesNotContain(logger.Events, item => Equals(item["Stage"], "source_slots"));
    }

    [Theory]
    [InlineData(false, "failed", "authority_call_failed")]
    [InlineData(true, "cancelled", "operation_cancelled")]
    public async Task FailedAuthorityCall_PreservesItsExceptionWithoutLoggingSensitiveDetails(
        bool cancelled, string outcome, string reason)
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var logger = new CapturingLogger();
        var registry = new Mock<ILegendLanguageRegistry>();
        Exception failure = cancelled ? new OperationCanceledException("privateexceptiondetail") :
            new InvalidOperationException("privateexceptiondetail");
        registry.Setup(item => item.NormalizeEnabledTranslationLanguageReadOnlyAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(failure);
        var corpus = new LegendConnectCorpusService(db, registry.Object, NullLogger<LegendConnectCorpusService>.Instance);
        var curriculum = new LegendConnectCurriculumService(db, registry.Object, corpus, logger: logger);
        var actual = await Record.ExceptionAsync(() => curriculum.AnalyzeReusableMeaningGraphAsync("en", "privateinput"));
        Assert.Same(failure, actual);
        var observed = Assert.Single(logger.Events);
        Assert.Equal(outcome, observed["Outcome"]);
        Assert.Equal(reason, observed["ReasonCode"]);
        Assert.Equal("meaning_graph", observed["Stage"]);
        Assert.Null(observed["TokenCount"]);
        var serialized = JsonSerializer.Serialize(logger.Events);
        Assert.DoesNotContain("privateexceptiondetail", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("privateinput", serialized, StringComparison.Ordinal);
    }

    private sealed class CapturingLogger : ILogger<LegendConnectCurriculumService>
    {
        internal List<Dictionary<string, object?>> Events { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> fields)
                Events.Add(fields.ToDictionary(item => item.Key, item => item.Value));
        }
    }
}
