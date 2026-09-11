using System;
using System.Net.Http;
using System.Text.Json;
using AgentPortal.Services;
using Domain.Messaging;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendFounderDiagnosticFollowupTests
{
    [Fact]
    public void NativeStage_DomainFailureRetainsOriginalDetectionReason()
    {
        var (outcome, reason) = LegendFounderAiConversationService.DescribeNativeStageResult(
            new TranslationDetectionResult(false, null, "translation_language_ambiguous"));
        Assert.Equal("failed", outcome);
        Assert.Equal("translation_language_ambiguous", reason);
    }

    [Theory]
    [InlineData("{\"stages\":[{\"state\":\"failed\"},{\"state\":\"timed_out\"}]}", false)]
    [InlineData("{\"stages\":[]}", false)]
    [InlineData("{\"stages\":[{\"state\":\"available\"},{\"state\":\"failed\"}]}", true)]
    [InlineData("{\"ok\":false,\"stages\":[{\"state\":\"available\"}]}", false)]
    [InlineData("{\"succeeded\":false}", false)]
    [InlineData("{\"error\":\"read_failed\"}", false)]
    public void DiagnosticOutcome_RequiresAnAvailableStageAndNoDomainFailure(string output, bool expected) =>
        Assert.Equal(expected, LegendFounderAiConversationService.IsSuccessfulFounderToolOutput(output));

    [Theory]
    [InlineData("ok")]
    [InlineData("succeeded")]
    public void Receipt_RejectsExplicitDomainFailureDespiteAvailableStage(string failureFlag)
    {
        var output = "{\"" + failureFlag + "\":false,\"runtimePolicy\":{\"count\":7},\"stages\":[{\"name\":\"runtime_policy\",\"state\":\"available\"}]}";
        var request = new LegendConnectReadOnlyContentBindingRequest(
            "request", "transition", "frame", "legend_operational_diagnostics", "{}",
            "runtimePolicy.count", null, 60, "$count", "count");
        Assert.False(LegendFounderToolAuthority.TryCreateReadOnlyContentBindingReceipt(
            request, output, DateTime.UtcNow, out var receipt, out var reason));
        Assert.Null(receipt);
        Assert.Equal("read_only_content_binding_tool_error", reason);
    }

    [Fact]
    public void DiagnosticScope_DefaultArgumentsMatchStrictNullsButPreserveSelectedScope()
    {
        var aggregate = LegendFounderAiConversationService.ReadScopeIdentity("legend_operational_diagnostics", "{}");
        Assert.Equal(aggregate, LegendFounderAiConversationService.ReadScopeIdentity(
            "legend_operational_diagnostics", "{\"language\":null,\"section\":null}"));
        Assert.NotEqual(aggregate, LegendFounderAiConversationService.ReadScopeIdentity(
            "legend_operational_diagnostics", "{\"section\":\"machine-learning-lifecycle\",\"language\":\"en\"}"));
        Assert.NotEqual(
            LegendFounderAiConversationService.ReadScopeIdentity("legend_search_retained_knowledge", "{}"),
            LegendFounderAiConversationService.ReadScopeIdentity("legend_search_retained_knowledge", "{\"language\":null}"));
    }

    [Theory]
    [InlineData(false, "unknown")]
    [InlineData(true, "denied")]
    public void FailureDetail_DoesNotExposeExceptionPayloadOrInferAuthorization(bool denied, string decision)
    {
        const string secret = "Bearer private-token; Server=private-host; arbitrary customer content";
        Exception exception = denied ? new UnauthorizedAccessException(secret) : new HttpRequestException(secret);
        var output = LegendFounderAiConversationService.BuildReadOnlyToolFailureOutput("legend_operational_diagnostics", exception);
        Assert.DoesNotContain("private-token", output);
        Assert.DoesNotContain("private-host", output);
        Assert.DoesNotContain("customer content", output);
        using var document = JsonDocument.Parse(output);
        Assert.Equal(decision, document.RootElement.GetProperty("authorizationDecision").GetString());
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("correlationId").GetString()));
    }

    [Theory]
    [InlineData("runtimePolicy.observedUtc", "available", true)]
    [InlineData("runtimePolicy.observedUtc", "failed", false)]
    [InlineData("providerCapacity.observedUtc", "available", false)]
    [InlineData("providerCapacity.observedUtc", "failed", false)]
    public void Receipt_FreshnessMustComeFromTheAvailableValueStage(string observedPath, string valueState, bool expected)
    {
        var now = DateTime.UtcNow;
        var output = JsonSerializer.Serialize(new
        {
            runtimePolicy = new { count = 7, observedUtc = now.AddSeconds(-1) },
            providerCapacity = new { observedUtc = now.AddSeconds(-1) },
            stages = new[]
            {
                new { name = "runtime_policy", state = valueState },
                new { name = "provider_capacity", state = "available" }
            }
        });
        var request = new LegendConnectReadOnlyContentBindingRequest(
            "request", "transition", "frame", "legend_operational_diagnostics", "{}",
            "runtimePolicy.count", observedPath, 60, "$count", "count");
        var result = LegendFounderToolAuthority.TryCreateReadOnlyContentBindingReceipt(
            request, output, now, out var receipt, out var reason);
        Assert.Equal(expected, result);
        if (expected)
            Assert.NotNull(receipt);
        else
        {
            Assert.Null(receipt);
            Assert.Equal("read_only_content_binding_source_unavailable", reason);
        }
    }
}
