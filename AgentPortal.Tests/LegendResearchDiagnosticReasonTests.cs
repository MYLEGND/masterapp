using Infrastructure.Messaging;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendResearchDiagnosticReasonTests
{
    [Theory]
    [InlineData("research_evidence_standard_unmet")]
    [InlineData("computed_operator_result_structure_unproven")]
    [InlineData("research_claim_passage_entailment_failed")]
    [InlineData("research_source_publication_timestamp_missing")]
    [InlineData("research_claim_verified_by_controlling_evidence")]
    [InlineData("internet_research_page_content_oversized")]
    [InlineData("internet_research_page_timeout")]
    public void ActualAssessmentAndPageReasons_RemainObservable(string reason) =>
        Assert.Equal(reason, LegendConnectTelemetry.NormalizeDiagnosticReason(reason));

    [Theory]
    [InlineData("research_private_customer_record_1832")]
    [InlineData("internet_research_exception_password_secret")]
    [InlineData("research_evidence_standard_unmet: confidential customer detail")]
    [InlineData("https://private.example/document?token=secret")]
    public void ArbitraryProviderOrPrivateText_IsNeverPublishedAsAReason(string reason) =>
        Assert.Equal("unclassified_reason", LegendConnectTelemetry.NormalizeDiagnosticReason(reason));
}
