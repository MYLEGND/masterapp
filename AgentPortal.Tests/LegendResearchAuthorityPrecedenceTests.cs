using System;
using Domain.Messaging;
using Infrastructure.Messaging;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendResearchAuthorityPrecedenceTests
{
    private static readonly DateTime DecisionUtc = new(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("Please cite sources for this record.")]
    [InlineData("Check this record against https://example.com/evidence.")]
    public void EstablishedOwnedRecordAuthority_SurvivesResearchWording(string question)
    {
        var inference = Unsupported() with
        {
            OwnedRecordIntent = new(LegendConnectOwnedRecordIntent.OwnedRecordStateInspection, true, null)
        };
        var decision = LegendConnectOperations.DecideResearchNeeded(question, "en", inference, DecisionUtc);

        Assert.False(decision.ResearchRequired);
        Assert.Equal(LegendConnectResearchNeed.NotResearchable, decision.Need);
        Assert.Equal("internal_operational_data_requires_governed_tools", decision.ReasonCode);
    }

    [Theory]
    [InlineData("bound", "Please cite sources for that choice.")]
    [InlineData("unresolved", "Please cite sources for that choice.")]
    [InlineData("bound", "Check that choice against https://example.com/evidence.")]
    [InlineData("unresolved", "Check that choice against https://example.com/evidence.")]
    public void CurrentTurnDiscourseAuthority_SurvivesResearchWording(string state, string question)
    {
        var discourse = new LegendConnectDiscourseStateSnapshot([
            new LegendConnectDiscourseTurnStateSnapshot(2, "user", false, [], [], [
                new LegendConnectDiscourseReferenceBindingSnapshot(
                    state, "reference_test", "choice", null, null, null, null,
                    false, "current-choice-selector", "choice-reference-rule")])]);

        var decision = LegendConnectOperations.DecideResearchNeeded(
            question, "en", Unsupported(), DecisionUtc, discourseState: discourse);

        Assert.False(decision.ResearchRequired);
        Assert.Equal("conversation_context_is_not_external_research", decision.ReasonCode);
    }

    [Theory]
    [InlineData("Please cite sources for the deepest ocean dive record.", "explicit_verification_requires_research")]
    [InlineData("Check the published record at https://example.com/evidence.", "named_external_source_requires_research")]
    public void ExplicitPublicResearch_RemainsAvailableWithoutEstablishedInternalAuthority(string question, string reason)
    {
        var decision = LegendConnectOperations.DecideResearchNeeded(
            question, "en", Unsupported(), DecisionUtc,
            discourseState: new LegendConnectDiscourseStateSnapshot([]));

        Assert.True(decision.ResearchRequired);
        Assert.Equal(reason, decision.ReasonCode);
        Assert.Equal(LegendConnectResearchAccessClass.PublicReadOnly, decision.AccessClass);
    }

    private static LegendConnectNativeInferenceSnapshot Unsupported() =>
        new(false, 0m, null, "meaning_graph_component_unknown", 0, "No admitted answer.", true,
            OwnedRecordIntent: new(LegendConnectOwnedRecordIntent.Unknown, false, "owned_record_state_inspection"));
}
