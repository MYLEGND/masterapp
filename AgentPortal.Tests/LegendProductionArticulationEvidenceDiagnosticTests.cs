using System;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace AgentPortal.Tests;

[Collection("LegendConnectFounderEnvironment")]
public sealed class LegendProductionArticulationEvidenceDiagnosticTests
{
    private readonly ITestOutputHelper _output;
    public LegendProductionArticulationEvidenceDiagnosticTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task PrintLiveAdvancedResultAnchorStructureReadOnly()
    {
        var raw = Environment.GetEnvironmentVariable("LEGEND_PRODUCTION_READONLY_CONNECTION");
        if (string.IsNullOrWhiteSpace(raw))
        {
            _output.WriteLine("ARTICULATION DIAGNOSTIC SKIPPED: production read-only connection unavailable.");
            return;
        }

        var cs = new SqlConnectionStringBuilder(raw)
        {
            ApplicationName = "LEGEND unpublished articulation evidence diagnostic",
            ApplicationIntent = ApplicationIntent.ReadOnly
        };
        var guard = new ReadOnlyInterceptor();
        await using var db = new MasterAppDbContext(
            new DbContextOptionsBuilder<MasterAppDbContext>()
                .UseSqlServer(cs.ConnectionString)
                .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
                .AddInterceptors(guard)
                .Options);

        var prompts = new[]
        {
            "Before solving the research project task, determine what must be achieved, preserved, and produced.",
            "Build a plan for the data migration that respects dependencies and verification gates.",
            "Rank plausible explanations for the process delay using the available evidence.",
            "Classify the market assessment evidence before drawing a conclusion.",
            "Determine which technology choice option best satisfies the stated priorities and constraints.",
            "Project likely, best-case, and adverse outcomes for the learning iteration with explicit assumptions.",
            "A contradiction has been identified in the instruction execution; resolve it using the governing evidence.",
            "Synthesize the executive brief by combining compatible evidence without erasing disagreement."
        };

        _output.WriteLine("============================================================");
        _output.WriteLine("LEGEND® LIVE ADVANCED RESULT ARTICULATION EVIDENCE");
        _output.WriteLine("============================================================");

        foreach (var rawPrompt in prompts)
        {
            var prompt = LegendLanguageIdentity.NormalizeText(rawPrompt);
            var endpointRows = await (
                from evidence in db.LegendSemanticTransitionEvidence.AsNoTracking()
                join source in db.LegendCurriculumExamples.AsNoTracking()
                    on evidence.SourceCurriculumExampleId equals source.Id
                join sourceUnit in db.LegendLanguageTextUnits.AsNoTracking()
                    on source.TextUnitId equals sourceUnit.Id
                join result in db.LegendCurriculumExamples.AsNoTracking()
                    on evidence.ResultCurriculumExampleId equals result.Id
                join resultUnit in db.LegendLanguageTextUnits.AsNoTracking()
                    on result.TextUnitId equals resultUnit.Id
                where evidence.SupersededUtc == null &&
                    evidence.SourceLanguageCode == "en" &&
                    evidence.ResultLanguageCode == "en" &&
                    evidence.Provenance == "FounderApproved" &&
                    evidence.ContributionState == "Supported" &&
                    evidence.IsHumanVerifiedSupport &&
                    source.SupersededUtc == null &&
                    result.SupersededUtc == null &&
                    sourceUnit.LanguageCode == "en" &&
                    sourceUnit.IsTrainingEligible &&
                    sourceUnit.Text == prompt
                select new
                {
                    evidence.TransitionSignature,
                    evidence.ResultSemanticFrame,
                    evidence.ResultCurriculumExampleId,
                    result.CurriculumFamilyId,
                    ResultText = resultUnit.Text
                }).Distinct().ToListAsync();

            _output.WriteLine("------------------------------------------------------------");
            _output.WriteLine("SOURCE: " + rawPrompt);
            _output.WriteLine("TRANSITION COUNT: " + endpointRows.Select(x => x.TransitionSignature).Distinct().Count());
            foreach (var transition in endpointRows.GroupBy(x => new { x.TransitionSignature, x.ResultSemanticFrame }))
            {
                _output.WriteLine("TRANSITION: " + transition.Key.TransitionSignature);
                _output.WriteLine("RESULT FRAME: " + transition.Key.ResultSemanticFrame);
                var ids = transition.Select(x => x.ResultCurriculumExampleId).Distinct().ToArray();
                var variations = await db.LegendCurriculumExampleVariations.AsNoTracking()
                    .Where(x => ids.Contains(x.CurriculumExampleId))
                    .OrderBy(x => x.CurriculumExampleId)
                    .ThenBy(x => x.Dimension)
                    .Select(x => new { x.CurriculumExampleId, x.Dimension, x.Value })
                    .ToListAsync();
                var anchors = await db.Set<LegendLanguageCompositionalAnchor>().AsNoTracking()
                    .Where(x => ids.Contains(x.CurriculumExampleId) &&
                        x.LanguageCode == "en" &&
                        x.SupersededUtc == null &&
                        x.Provenance == "FounderApproved")
                    .OrderBy(x => x.CurriculumExampleId)
                    .ThenBy(x => x.ComponentStartTokenIndex)
                    .ThenBy(x => x.Dimension)
                    .Select(x => new
                    {
                        x.CurriculumExampleId,
                        x.LexemeId,
                        x.ComponentStartTokenIndex,
                        x.ComponentLength,
                        x.Dimension,
                        x.Value,
                        x.SemanticSignature
                    }).ToListAsync();

                foreach (var endpoint in transition.OrderBy(x => x.CurriculumFamilyId).ThenBy(x => x.ResultText))
                {
                    _output.WriteLine("  ENDPOINT FAMILY: " + endpoint.CurriculumFamilyId);
                    _output.WriteLine("  ENDPOINT TEXT: " + endpoint.ResultText);
                    var v = variations.Where(x => x.CurriculumExampleId == endpoint.ResultCurriculumExampleId).ToArray();
                    _output.WriteLine("  VARIATIONS: " + (v.Length == 0 ? "<NONE>" : string.Join(" | ", v.Select(x => x.Dimension + "=" + x.Value))));
                    var a = anchors.Where(x => x.CurriculumExampleId == endpoint.ResultCurriculumExampleId).ToArray();
                    _output.WriteLine("  ANCHOR COUNT: " + a.Length);
                    foreach (var anchor in a)
                    {
                        _output.WriteLine("    ANCHOR: " + anchor.Dimension + "=" + anchor.Value +
                            "; lexical=" + (anchor.LexemeId is null ? "no" : "yes") +
                            "; start=" + (anchor.ComponentStartTokenIndex?.ToString() ?? "null") +
                            "; len=" + (anchor.ComponentLength?.ToString() ?? "null") +
                            "; semantic=" + anchor.SemanticSignature);
                    }
                }
            }
        }

        _output.WriteLine("QUERY COMMANDS: " + guard.QueryCommands);
        _output.WriteLine("WRITE COMMANDS: " + guard.WriteCommands);
        Assert.Equal(0, guard.WriteCommands);
    }

    private sealed class ReadOnlyInterceptor : DbCommandInterceptor
    {
        public int QueryCommands { get; private set; }
        public int WriteCommands { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            QueryCommands++;
            Guard(command);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            WriteCommands++;
            throw new InvalidOperationException("Articulation diagnostic attempted a production write.");
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            WriteCommands++;
            throw new InvalidOperationException("Articulation diagnostic attempted a production write.");
        }

        private static void Guard(DbCommand command)
        {
            var sql = command.CommandText.TrimStart();
            if (sql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) || sql.StartsWith("WITH", StringComparison.OrdinalIgnoreCase))
                return;
            throw new InvalidOperationException("Articulation diagnostic attempted non-query SQL.");
        }
    }
}
