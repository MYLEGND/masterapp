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
            ApplicationName = "LEGEND unpublished articulation provenance diagnostic",
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
        _output.WriteLine("LEGEND® LIVE ADVANCED ARTICULATION PROVENANCE");
        _output.WriteLine("============================================================");

        foreach (var rawPrompt in prompts)
        {
            var prompt = rawPrompt.Trim();
            var evidenceRows = await (
                from evidence in db.Set<LegendSemanticTransitionEvidence>().AsNoTracking()
                join source in db.Set<LegendCurriculumExample>().AsNoTracking()
                    on evidence.SourceCurriculumExampleId equals source.Id
                join sourceUnit in db.Set<LegendLanguageTextUnit>().AsNoTracking()
                    on source.TextUnitId equals sourceUnit.Id
                join result in db.Set<LegendCurriculumExample>().AsNoTracking()
                    on evidence.ResultCurriculumExampleId equals result.Id
                join resultUnit in db.Set<LegendLanguageTextUnit>().AsNoTracking()
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
                    evidence.IndependentSourceIdentity,
                    evidence.SourceCurriculumExampleId,
                    SourceFamilyId = source.CurriculumFamilyId,
                    evidence.ResultCurriculumExampleId,
                    ResultFamilyId = result.CurriculumFamilyId,
                    ResultText = resultUnit.Text
                }).ToListAsync();

            _output.WriteLine("------------------------------------------------------------");
            _output.WriteLine("SOURCE: " + rawPrompt);
            foreach (var transition in evidenceRows.GroupBy(x => new { x.TransitionSignature, x.ResultSemanticFrame }))
            {
                _output.WriteLine("TRANSITION: " + transition.Key.TransitionSignature);
                _output.WriteLine("RESULT FRAME: " + transition.Key.ResultSemanticFrame);
                _output.WriteLine("TRANSITION EVIDENCE ROWS: " + transition.Count());
                _output.WriteLine("TRANSITION INDEPENDENT SOURCE IDENTITIES: " + transition.Select(x => x.IndependentSourceIdentity).Distinct().Count());
                _output.WriteLine("SOURCE FAMILIES: " + transition.Select(x => x.SourceFamilyId).Distinct().Count());
                _output.WriteLine("RESULT FAMILIES: " + transition.Select(x => x.ResultFamilyId).Distinct().Count());
                _output.WriteLine("SOURCE EXAMPLES: " + transition.Select(x => x.SourceCurriculumExampleId).Distinct().Count());
                _output.WriteLine("RESULT EXAMPLES: " + transition.Select(x => x.ResultCurriculumExampleId).Distinct().Count());
                foreach (var identity in transition.Select(x => x.IndependentSourceIdentity).Distinct().OrderBy(x => x))
                    _output.WriteLine("  INDEPENDENT TRANSITION SOURCE: " + identity);

                var resultIds = transition.Select(x => x.ResultCurriculumExampleId).Distinct().ToArray();
                var anchors = await db.Set<LegendLanguageCompositionalAnchor>().AsNoTracking()
                    .Where(x => resultIds.Contains(x.CurriculumExampleId) &&
                        x.LanguageCode == "en" && x.SupersededUtc == null &&
                        x.Provenance == "FounderApproved" && x.LexemeId != null &&
                        x.SemanticSignature != null)
                    .Select(x => new
                    {
                        x.Id,
                        x.CurriculumFamilyId,
                        x.CurriculumExampleId,
                        x.Dimension,
                        x.Value,
                        x.SemanticSignature,
                        x.ComponentStartTokenIndex,
                        x.ComponentLength
                    }).ToListAsync();

                var semanticSignatures = anchors.Select(x => x.SemanticSignature!).Distinct().ToArray();
                var primitives = await db.Set<LegendLanguageMeaningPrimitive>().AsNoTracking()
                    .Where(x => x.LanguageCode == "en" && semanticSignatures.Contains(x.SemanticSignature) && x.SupersededUtc == null)
                    .Select(x => new { x.Id, x.SemanticSignature, x.SemanticDimension, x.SemanticValue })
                    .ToListAsync();
                var primitiveIds = primitives.Select(x => x.Id).ToArray();
                var primitiveEvidence = await db.Set<LegendLanguageMeaningPrimitiveEvidence>().AsNoTracking()
                    .Where(x => primitiveIds.Contains(x.MeaningPrimitiveId) && x.SupersededUtc == null &&
                        x.Provenance == "FounderApproved" && x.ContributionState == "Supported" && x.IsHumanVerifiedSupport)
                    .Select(x => new
                    {
                        x.MeaningPrimitiveId,
                        x.CurriculumFamilyId,
                        x.CurriculumExampleId,
                        x.IndependentSourceIdentity
                    }).ToListAsync();

                foreach (var primitive in primitives.OrderBy(x => x.SemanticDimension).ThenBy(x => x.SemanticValue))
                {
                    var pe = primitiveEvidence.Where(x => x.MeaningPrimitiveId == primitive.Id).ToArray();
                    _output.WriteLine("  PRIMITIVE: " + primitive.SemanticDimension + "=" + primitive.SemanticValue +
                        "; independent=" + pe.Select(x => x.IndependentSourceIdentity).Distinct().Count() +
                        "; families=" + pe.Select(x => x.CurriculumFamilyId).Distinct().Count() +
                        "; examples=" + pe.Select(x => x.CurriculumExampleId).Distinct().Count());
                }

                foreach (var endpoint in transition.GroupBy(x => x.ResultCurriculumExampleId).Select(g => g.First()).OrderBy(x => x.ResultText))
                {
                    _output.WriteLine("  ENDPOINT FAMILY: " + endpoint.ResultFamilyId);
                    _output.WriteLine("  ENDPOINT: " + endpoint.ResultText);
                    foreach (var anchor in anchors.Where(x => x.CurriculumExampleId == endpoint.ResultCurriculumExampleId).OrderBy(x => x.ComponentStartTokenIndex))
                        _output.WriteLine("    SLOT: " + anchor.Dimension + "=" + anchor.Value +
                            "; semantic=" + anchor.SemanticSignature +
                            "; start=" + anchor.ComponentStartTokenIndex + "; len=" + anchor.ComponentLength);
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
            DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            QueryCommands++;
            Guard(command);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            WriteCommands++;
            throw new InvalidOperationException("Articulation diagnostic attempted a production write.");
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            WriteCommands++;
            throw new InvalidOperationException("Articulation diagnostic attempted a production write.");
        }

        private static void Guard(DbCommand command)
        {
            var sql = command.CommandText.TrimStart();
            if (sql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) || sql.StartsWith("WITH", StringComparison.OrdinalIgnoreCase)) return;
            throw new InvalidOperationException("Articulation diagnostic attempted non-query SQL.");
        }
    }
}
