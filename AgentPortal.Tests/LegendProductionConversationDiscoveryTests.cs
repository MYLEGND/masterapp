using System;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace AgentPortal.Tests;

[Collection("LegendConnectFounderEnvironment")]
public sealed class LegendProductionConversationDiscoveryTests
{
    private readonly ITestOutputHelper _output;
    public LegendProductionConversationDiscoveryTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task PrintStrongNonGreetingProductionConversationCapabilities()
    {
        var raw = Environment.GetEnvironmentVariable("LEGEND_PRODUCTION_READONLY_CONNECTION");
        if (string.IsNullOrWhiteSpace(raw))
        {
            _output.WriteLine("DISCOVERY SKIPPED: production read-only connection unavailable.");
            return;
        }

        var cs = new SqlConnectionStringBuilder(raw)
        {
            ApplicationName = "LEGEND unpublished conversation discovery",
            ApplicationIntent = ApplicationIntent.ReadOnly
        };
        var guard = new DiscoveryReadOnlyInterceptor();
        await using var db = new MasterAppDbContext(
            new DbContextOptionsBuilder<MasterAppDbContext>()
                .UseSqlServer(cs.ConnectionString)
                .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
                .AddInterceptors(guard)
                .Options);

        var rows = await (
            from e in db.LegendSemanticTransitionEvidence.AsNoTracking()
            join s in db.LegendCurriculumExamples.AsNoTracking() on e.SourceCurriculumExampleId equals s.Id
            join r in db.LegendCurriculumExamples.AsNoTracking() on e.ResultCurriculumExampleId equals r.Id
            join su in db.LegendLanguageTextUnits.AsNoTracking() on s.TextUnitId equals su.Id
            join ru in db.LegendLanguageTextUnits.AsNoTracking() on r.TextUnitId equals ru.Id
            where e.SupersededUtc == null && e.SourceLanguageCode == "en" && e.ResultLanguageCode == "en" &&
                  e.Provenance == "FounderApproved" &&
                  e.ContributionState == "Supported" && e.IsHumanVerifiedSupport &&
                  s.SupersededUtc == null && r.SupersededUtc == null &&
                  su.IsTrainingEligible && ru.IsTrainingEligible
            select new
            {
                e.TransitionSignature,
                e.SourceSemanticFrame,
                e.ResultSemanticFrame,
                e.IndependentSourceIdentity,
                SourceText = su.Text,
                ResultText = ru.Text
            }).ToListAsync();

        var strong = rows
            .GroupBy(x => x.TransitionSignature)
            .Select(g => new
            {
                Signature = g.Key,
                SourceFrame = g.First().SourceSemanticFrame,
                ResultFrame = g.First().ResultSemanticFrame,
                Independent = g.Select(x => x.IndependentSourceIdentity).Distinct().Count(),
                Sources = g.Select(x => x.SourceText).Distinct().Take(6).ToArray(),
                Results = g.Select(x => x.ResultText).Distinct().Take(6).ToArray()
            })
            .Where(x => x.Independent >= 3)
            .Where(x => !x.SourceFrame.Contains("conversation_opening", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.Independent)
            .ThenBy(x => x.Signature)
            .ToArray();

        _output.WriteLine("============================================================");
        _output.WriteLine("LEGEND® LIVE PRODUCTION CURRICULUM — NON-GREETING CAPABILITY DISCOVERY");
        _output.WriteLine("============================================================");
        _output.WriteLine($"STRONG TRANSITION CLASSES: {strong.Length}");
        foreach (var item in strong.Take(40))
        {
            _output.WriteLine("---");
            _output.WriteLine($"SIGNATURE: {item.Signature}");
            _output.WriteLine($"INDEPENDENT SUPPORT: {item.Independent}");
            _output.WriteLine($"SOURCE FRAME: {item.SourceFrame}");
            _output.WriteLine($"RESULT FRAME: {item.ResultFrame}");
            foreach (var source in item.Sources) _output.WriteLine($"SOURCE: {source}");
            foreach (var result in item.Results) _output.WriteLine($"RESULT: {result}");
        }

        var chainCount = 0;
        foreach (var a in strong)
        {
            foreach (var b in strong)
            {
                if (a.Signature == b.Signature) continue;
                if (!string.Equals(a.ResultFrame, b.SourceFrame, StringComparison.Ordinal)) continue;
                chainCount++;
                _output.WriteLine("=== EXACT FRAME CHAIN ===");
                _output.WriteLine($"A: {a.Signature} :: {a.SourceFrame} -> {a.ResultFrame}");
                _output.WriteLine($"B: {b.Signature} :: {b.SourceFrame} -> {b.ResultFrame}");
                _output.WriteLine($"A SOURCE SAMPLE: {a.Sources.FirstOrDefault()}");
                _output.WriteLine($"B SOURCE SAMPLE: {b.Sources.FirstOrDefault()}");
                if (chainCount >= 20) break;
            }
            if (chainCount >= 20) break;
        }
        _output.WriteLine($"EXACT FRAME CHAINS FOUND: {chainCount}");
        _output.WriteLine($"QUERY COMMANDS: {guard.QueryCommands}");
        _output.WriteLine($"WRITE COMMANDS: {guard.WriteCommands}");
        Assert.Equal(0, guard.WriteCommands);
    }

    private sealed class DiscoveryReadOnlyInterceptor : DbCommandInterceptor
    {
        public int QueryCommands { get; private set; }
        public int WriteCommands { get; private set; }

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            QueryCommands++;
            Guard(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            QueryCommands++;
            Guard(command);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            WriteCommands++;
            throw new InvalidOperationException("Production conversation discovery attempted a write command.");
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            WriteCommands++;
            throw new InvalidOperationException("Production conversation discovery attempted a write command.");
        }

        private static void Guard(DbCommand command)
        {
            var sql = command.CommandText.TrimStart();
            if (sql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) || sql.StartsWith("WITH", StringComparison.OrdinalIgnoreCase)) return;
            throw new InvalidOperationException("Production conversation discovery attempted non-query SQL: " + sql[..Math.Min(sql.Length, 80)]);
        }
    }
}
