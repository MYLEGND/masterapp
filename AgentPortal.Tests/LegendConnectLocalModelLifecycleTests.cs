using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Infrastructure.Messaging;
using Domain.Entities;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendConnectLocalModelLifecycleTests
{
    [Fact]
    public async Task UnpromotedTrainingAndProcessingCurriculum_DoNotBlockConfiguredBaseOrActivateCandidate()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        db.Add(new LegendConnectRuntimePolicy { ScopeKey = "Global", LanguageIntelligenceReevaluationPhase = "Processing" });
        db.Add(new LegendConnectModelTrainingRun
        {
            RunKey = new string('b', 64), ScopeKey = "Global", TrainingProvider = "LocalMlx",
            State = "TrainingCompleted", EvaluationState = "Rejected", PromotionState = "NotEvaluated",
            ChallengerModelVersion = "local-mlx:" + new string('b', 64)
        });
        await db.SaveChangesAsync();
        var transport = new Mock<ILegendConnectModelInferenceTransport>(MockBehavior.Strict);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LegendConnect:Foundation:Enabled"] = "true", ["LegendConnect:Foundation:Model"] = "verified-configured-base"
        }).Build();
        var result = await new LegendConnectActiveModelInference(db, transport.Object, configuration).ResolveConversationModelAsync();
        Assert.True(result.Available);
        Assert.Equal("verified-configured-base", result.ModelVersion);
        Assert.Equal("Pretrained", result.ModelProvenance);
        Assert.Null(result.ModelTrainingRunId);
        Assert.Null(result.AdapterVersion);
        Assert.False(db.ChangeTracker.HasChanges());
        transport.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("expected", "wrong", true, 1, 0)]
    [InlineData("wrong", "expected", true, 0, 1)]
    [InlineData("expected", null, false, 0, 0)]
    public async Task LocalEvaluation_UsesExecutedBaselineAndIndependentExactReference(
        string candidate, string? baseline, bool succeeded, int candidateScore, int baselineScore)
    {
        var example = new LegendConnectTrainingDatasetExample("identity", "en:en", "en", "en", "input", "expected",
            "FounderApproved", 4, "source", "target", OutputContract: "executable_oracle_exact_v1");
        var result = await new LocalLegendConnectModelEvaluationBackend().JudgeAsync(
            new(example, candidate, "expected", BaselineModelText: baseline));
        Assert.Equal(succeeded, result.Succeeded);
        Assert.Equal(candidateScore, result.ChallengerScore);
        Assert.Equal(baselineScore, result.BaselineScore);
        if (succeeded && candidateScore < baselineScore) Assert.True(result.BlockingRegression);
    }

}
