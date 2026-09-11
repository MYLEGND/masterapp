using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Domain.Messaging;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendComputedStructureSqliteTests
{
    [Fact]
    public async Task CurrentGraphQualification_ExecutesBoundedRelationalQueriesAndRejectsWithdrawal()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var observer = new QueryObserver();
        await using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseSqlite(connection).AddInterceptors(observer).Options);
        await db.Database.EnsureCreatedAsync();
        ControllerTestHelpers.SeedGovernedLanguageBaseline(db);
        await db.SaveChangesAsync();
        var registry = new LegendLanguageRegistry(db, new ConfigurationBuilder().Build());
        var curriculum = new LegendConnectCurriculumService(db, registry,
            new LegendConnectCorpusService(db, registry, NullLogger<LegendConnectCorpusService>.Instance));
        var teaching = typeof(LegendConnectCompositionalArithmeticAdmissionTests)
            .GetMethod("Teaching", BindingFlags.NonPublic | BindingFlags.Static)!;
        foreach (var family in new[] { "amber", "copper", "silver" })
        {
            Assert.True((await curriculum.SubmitFounderBatchAsync(
                (LegendConnectCurriculumBatchSubmission)teaching.Invoke(null, [family])!)).Succeeded);
            foreach (var sample in new[] { "first", "second" })
                await curriculum.PersistFounderCrossExampleSemanticRelationAsync(
                    new("chain-input-" + family + "-" + sample, "reasoning.arithmetic.multiply.measurement-chain",
                        "chain-subtotal-" + family + "-" + sample), LegendConnectLanguageIntelligenceEvaluatorVersion.Current);
        }
        var projections = await db.LegendSemanticTransitionEvidence
            .Where(item => item.SupersededUtc == null && item.FounderSemanticExampleRelationEvidenceId != null)
            .ToArrayAsync();
        var signature = Assert.Single(projections.Select(item => item.TransitionSignature).Distinct());
        observer.Commands.Clear();
        Assert.Contains(signature, await ReadOperators(curriculum, signature));
        Assert.Equal(3, observer.Commands.Count);
        Assert.All(observer.Commands, command => Assert.Contains("LIMIT", command, StringComparison.OrdinalIgnoreCase));
        var resultIds = projections.Select(item => item.ResultCurriculumExampleId).Distinct().ToArray();
        var relations = await db.LegendLanguageMeaningRelationEvidence
            .Where(item => resultIds.Contains(item.CurriculumExampleId)).ToArrayAsync();
        Assert.NotEmpty(relations);
        foreach (var relation in relations) relation.SupersededUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        observer.Commands.Clear();
        Assert.DoesNotContain(signature, await ReadOperators(curriculum, signature));
        Assert.Equal(3, observer.Commands.Count);
    }

    private static async Task<string[]> ReadOperators(LegendConnectCurriculumService curriculum, string signature)
    {
        var method = typeof(LegendConnectCurriculumService).GetMethod(
            "LoadActiveGovernedReasoningOperatorsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = (Task)method.Invoke(curriculum, ["en", new[] { signature }, CancellationToken.None])!;
        await task;
        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        return ((IReadOnlyDictionary<string, string>)result.GetType().GetProperty("Operators")!.GetValue(result)!).Keys.ToArray();
    }

    private sealed class QueryObserver : DbCommandInterceptor
    {
        internal List<string> Commands { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("-- LEGEND_QUERY:computed_structure_", StringComparison.Ordinal))
                Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
