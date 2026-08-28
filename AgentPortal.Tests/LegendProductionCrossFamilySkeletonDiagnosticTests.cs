using System;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace AgentPortal.Tests;

[Collection("LegendConnectFounderEnvironment")]
public sealed class LegendProductionCrossFamilySkeletonDiagnosticTests
{
    private readonly ITestOutputHelper _output;
    public LegendProductionCrossFamilySkeletonDiagnosticTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task PrintCrossFamilyResultSkeletonsReadOnly()
    {
        var raw = Environment.GetEnvironmentVariable("LEGEND_PRODUCTION_READONLY_CONNECTION");
        if (string.IsNullOrWhiteSpace(raw)) return;
        var cs = new SqlConnectionStringBuilder(raw)
        {
            ApplicationName = "LEGEND cross-family articulation skeleton diagnostic",
            ApplicationIntent = ApplicationIntent.ReadOnly
        };
        var guard = new Guard();
        await using var db = new MasterAppDbContext(
            new DbContextOptionsBuilder<MasterAppDbContext>()
                .UseSqlServer(cs.ConnectionString)
                .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
                .AddInterceptors(guard).Options);

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

        foreach (var prompt in prompts)
        {
            var signatures = await (
                from evidence in db.Set<LegendSemanticTransitionEvidence>().AsNoTracking()
                join source in db.Set<LegendCurriculumExample>().AsNoTracking() on evidence.SourceCurriculumExampleId equals source.Id
                join unit in db.Set<LegendLanguageTextUnit>().AsNoTracking() on source.TextUnitId equals unit.Id
                where evidence.SupersededUtc == null && evidence.SourceLanguageCode == "en" && evidence.ResultLanguageCode == "en" &&
                      evidence.ContributionState == "Supported" && evidence.IsHumanVerifiedSupport && evidence.Provenance == "FounderApproved" &&
                      source.SupersededUtc == null && unit.LanguageCode == "en" && unit.IsTrainingEligible && unit.Text == prompt
                select evidence.TransitionSignature).Distinct().ToArrayAsync();

            _output.WriteLine("============================================================");
            _output.WriteLine("SOURCE: " + prompt);
            foreach (var signature in signatures)
            {
                var endpoints = await (
                    from evidence in db.Set<LegendSemanticTransitionEvidence>().AsNoTracking()
                    join result in db.Set<LegendCurriculumExample>().AsNoTracking() on evidence.ResultCurriculumExampleId equals result.Id
                    join unit in db.Set<LegendLanguageTextUnit>().AsNoTracking() on result.TextUnitId equals unit.Id
                    where evidence.TransitionSignature == signature && evidence.SupersededUtc == null &&
                          evidence.SourceLanguageCode == "en" && evidence.ResultLanguageCode == "en" &&
                          evidence.ContributionState == "Supported" && evidence.IsHumanVerifiedSupport && evidence.Provenance == "FounderApproved" &&
                          result.SupersededUtc == null && unit.LanguageCode == "en" && unit.IsTrainingEligible
                    select new { result.Id, result.CurriculumFamilyId, Text = unit.Text, evidence.ResultSemanticFrame })
                    .Distinct().ToListAsync();

                var ids = endpoints.Select(x => x.Id).Distinct().ToArray();
                var anchors = await db.Set<LegendLanguageCompositionalAnchor>().AsNoTracking()
                    .Where(x => ids.Contains(x.CurriculumExampleId) && x.LanguageCode == "en" && x.SupersededUtc == null &&
                                x.Provenance == "FounderApproved" && x.LexemeId != null && x.ComponentStartTokenIndex != null && x.ComponentLength != null)
                    .Select(x => new { x.CurriculumExampleId, x.Dimension, x.Value, Start = x.ComponentStartTokenIndex!.Value, Length = x.ComponentLength!.Value })
                    .ToListAsync();

                _output.WriteLine("TRANSITION: " + signature);
                _output.WriteLine("FAMILIES: " + endpoints.Select(x => x.CurriculumFamilyId).Distinct().Count());
                _output.WriteLine("ENDPOINTS: " + endpoints.Select(x => x.Id).Distinct().Count());
                foreach (var family in endpoints.GroupBy(x => x.CurriculumFamilyId).OrderBy(x => x.Key))
                {
                    _output.WriteLine("  FAMILY: " + family.Key);
                    foreach (var endpoint in family.GroupBy(x => x.Id).Select(x => x.First()).OrderBy(x => x.Text))
                    {
                        _output.WriteLine("    TEXT: " + endpoint.Text);
                        var frameDims = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string,string>>(endpoint.ResultSemanticFrame)!
                            .Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
                        var frameAnchors = anchors.Where(x => x.CurriculumExampleId == endpoint.Id && frameDims.Contains(x.Dimension))
                            .OrderBy(x => x.Start).ThenBy(x => x.Length).ToArray();
                        _output.WriteLine("    FRAME SLOTS: " + string.Join(" | ", frameAnchors.Select(x => x.Dimension + "=" + x.Value + "@" + x.Start + "+" + x.Length)));
                    }
                }
            }
        }

        _output.WriteLine("QUERY COMMANDS: " + guard.Queries);
        _output.WriteLine("WRITE COMMANDS: " + guard.Writes);
        Assert.Equal(0, guard.Writes);
    }

    private sealed class Guard : DbCommandInterceptor
    {
        public int Queries { get; private set; }
        public int Writes { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Queries++; Check(command); return ValueTask.FromResult(result);
        }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Writes++; throw new InvalidOperationException("Production write forbidden.");
        }
        private static void Check(DbCommand command)
        {
            var sql = command.CommandText.TrimStart();
            if (!sql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) && !sql.StartsWith("WITH", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Non-query SQL forbidden.");
        }
    }
}
