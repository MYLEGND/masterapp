using Infrastructure.Messaging;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendSemanticTransitionEvidenceRankingTests
{
    private const string Source = "{\"conversation_function\":\"request\"}";
    private const string Result = "{\"conversation_function\":\"response\"}";

    [Fact]
    public void Assessment_RetainsFounderObservedKnowledgeWithoutCallingItProductionEligible()
    {
        var assessment = LegendSemanticTransitionProductionEligibility.Assess(
        [
            Observation("family-1")
        ]);

        Assert.Equal(LegendSemanticTransitionEvidenceTier.FounderObserved, assessment.Tier);
        Assert.Equal(1, assessment.IndependentSourceCount);
        Assert.False(LegendSemanticTransitionProductionEligibility.IsEligible([Observation("family-1")]));
    }

    [Fact]
    public void Assessment_PromotesThreeIndependentFounderSourcesToThePreferredTier()
    {
        var observations = new[]
        {
            Observation("family-1"),
            Observation("family-2"),
            Observation("family-3")
        };

        var assessment = LegendSemanticTransitionProductionEligibility.Assess(observations);

        Assert.Equal(LegendSemanticTransitionEvidenceTier.ProductionEligible, assessment.Tier);
        Assert.Equal(3, assessment.IndependentSourceCount);
        Assert.True(LegendSemanticTransitionProductionEligibility.IsEligible(observations));
    }

    [Fact]
    public void Assessment_FailsClosedForContradictionOrFrameDriftAtEveryTier()
    {
        var contradicted = new[]
        {
            Observation("family-1"),
            Observation("family-2") with { ContributionState = "Contradictory" }
        };
        var drifted = new[]
        {
            Observation("family-1"),
            Observation("family-2") with { ResultFrame = "{\"conversation_function\":\"other\"}" }
        };

        Assert.Equal(
            LegendSemanticTransitionEvidenceTier.None,
            LegendSemanticTransitionProductionEligibility.Assess(contradicted).Tier);
        Assert.Equal(
            LegendSemanticTransitionEvidenceTier.None,
            LegendSemanticTransitionProductionEligibility.Assess(drifted).Tier);
    }

    private static LegendSemanticTransitionEligibilityObservation Observation(string family) =>
        new(Source, Result, family, "Supported", true);
}
