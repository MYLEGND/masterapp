using System;
using AgentPortal.Services;
using Domain.Messaging;
using Infrastructure.Messaging;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendFounderFailureTruthTests
{
    [Theory]
    [InlineData("provider_http_400", "provider_http_400")]
    [InlineData("provider_http_429", "provider_http_429")]
    [InlineData("provider_http_503", "provider_http_503")]
    [InlineData("insufficient_evidence", "insufficient_evidence")]
    [InlineData("provider_http_private_customer_data", "unclassified_reason")]
    [InlineData("provider_http_999", "unclassified_reason")]
    public void DiagnosticVocabulary_PreservesFinitePublicCodesAndRejectsArbitraryPrefixes(string reason, string expected) =>
        Assert.Equal(expected, LegendConnectTelemetry.NormalizeDiagnosticReason(reason));

    [Theory]
    [InlineData(null, "native_inference")]
    [InlineData("provider_http_429", "provider_http")]
    [InlineData("provider_timeout", "timeout")]
    [InlineData("provider_transport_failure", "transport")]
    [InlineData("provider_invalid_json", "provider_json")]
    public void UnavailableNativeAndProviderOutcomes_AreFailuresWithSafeEvidence(string? providerReason, string failureKind)
    {
        var native = new LegendConnectNativeInferenceSnapshot(false, 0m, null,
            "semantic_transition_not_production_eligible", 9,
            "The transition has not crossed the production eligibility gate.", false);
        var result = LegendFounderAiConversationService.NativeInferenceUnavailableResponse(
            "legend", native, null, providerReason, "private customer data; Bearer secret-token");
        Assert.False(result.Succeeded);
        Assert.Equal(failureKind, result.FailureKind);
        Assert.Equal(result.Message, result.Error);
        Assert.Contains("EvidenceCount=9", result.Error);
        Assert.Contains("semantic_transition_not_production_eligible", result.Error);
        Assert.Contains("production eligibility gate", result.Error);
        Assert.DoesNotContain("secret-token", result.Error);
        Assert.DoesNotContain("private customer data", result.Error);
    }

    [Fact]
    public void UnavailableResponse_DoesNotEchoNativeExceptionDetail()
    {
        var result = LegendFounderAiConversationService.NativeInferenceUnavailableResponse(
            "legend", null, "Server=private-server; secret-password", "provider_transport_failure", "private-url");
        Assert.False(result.Succeeded);
        Assert.DoesNotContain("private-server", result.Error);
        Assert.DoesNotContain("secret-password", result.Error);
        Assert.DoesNotContain("private-url", result.Error);
    }

    [Theory]
    [InlineData(61, 1)]
    [InlineData(60, 0)]
    [InlineData(45, 0)]
    public void ReadBudget_PreservesEntireFinalSynthesisReserve(double remainingSeconds, double expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds),
            LegendFounderAiConversationService.ResolveReadOnlyToolBudget(TimeSpan.FromSeconds(remainingSeconds)));
    }

    [Theory]
    [InlineData("req_123-ABC", "req_123-ABC")]
    [InlineData("secret=Bearer customer-data", null)]
    [InlineData("req\nprivate-customer", null)]
    public void ProviderCorrelation_IsBoundedIdentifierOnly(string value, string? expected) =>
        Assert.Equal(expected, LegendFounderAiConversationService.SafeProviderCorrelation(value));
}
