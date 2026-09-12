using System;
using Domain.Messaging;
using Infrastructure.Messaging;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendFoundationResearchDecisionTests
{
    [Fact]
    public void FoundationToolPlanningCanRequestVerificationWithoutCurriculumEscalation()
    {
        var decision = LegendConnectOperations.DecideResearchNeeded(
            "Explain the migration patterns of monarch butterflies.", "en", null,
            DateTime.UtcNow, foundationRequestedVerification: true);
        Assert.True(decision.ResearchRequired);
        Assert.Equal("foundation_requested_factual_verification", decision.ReasonCode);
        Assert.Equal(LegendConnectResearchAccessClass.PublicReadOnly, decision.AccessClass);
    }

    [Fact]
    public void FoundationVerificationDoesNotGrantPrivateSourceAccess()
    {
        var decision = LegendConnectOperations.DecideResearchNeeded(
            "Check a confidential private document about migration.", "en", null,
            DateTime.UtcNow, foundationRequestedVerification: true);
        Assert.True(decision.ResearchRequired);
        Assert.Equal(LegendConnectResearchAccessClass.PrivateReadOnly, decision.AccessClass);
    }

    [Fact]
    public void FoundationVerificationDoesNotReplaceInternalOperationalAuthority()
    {
        var decision = LegendConnectOperations.DecideResearchNeeded(
            "What is the current LEGEND training status?", "en", null,
            DateTime.UtcNow, foundationRequestedVerification: true);
        Assert.False(decision.ResearchRequired);
        Assert.Equal("internal_legend_state_requires_governed_tools", decision.ReasonCode);
    }

    [Fact]
    public void UnknownLanguageStillCannotAuthorizeResearch()
    {
        var decision = LegendConnectOperations.DecideResearchNeeded(
            "Explain the migration patterns of monarch butterflies.", "und", null,
            DateTime.UtcNow, languageGoverned: false, foundationRequestedVerification: true);
        Assert.False(decision.ResearchRequired);
        Assert.Equal("research_source_language_not_governed", decision.ReasonCode);
    }
}
