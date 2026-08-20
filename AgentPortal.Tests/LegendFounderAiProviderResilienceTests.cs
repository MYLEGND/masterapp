using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using AgentPortal.Controllers;
using AgentPortal.Services;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendFounderAiProviderResilienceTests
{
    [Theory]
    [InlineData(429, StatusCodes.Status429TooManyRequests)]
    [InlineData(503, StatusCodes.Status503ServiceUnavailable)]
    [InlineData(504, StatusCodes.Status504GatewayTimeout)]
    [InlineData(408, StatusCodes.Status504GatewayTimeout)]
    [InlineData(400, StatusCodes.Status502BadGateway)]
    public void ControllerMapsProviderFailuresTruthfully(int providerStatus, int expectedStatus)
    {
        var method = typeof(LegendFounderAiController).GetMethod("MapStatus", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var result = LegendFounderAiChatResponse.Failure("provider", "provider_http", providerStatus, "req_test");
        Assert.Equal(expectedStatus, Assert.IsType<int>(method!.Invoke(null, new object[] { result })));
    }

    [Fact]
    public void RetryPolicyHonorsProviderRetryAfterBeyondLegacyFiveSecondCap()
    {
        var method = typeof(LegendFounderAiConversationService).GetMethod("ResolveProviderRetryDelay", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(12));
        var delay = Assert.IsType<TimeSpan>(method!.Invoke(null, new object[] { response, 1 }));
        Assert.True(delay >= TimeSpan.FromSeconds(11.9));
    }

    [Theory]
    [InlineData("250ms", 0.25)]
    [InlineData("2s", 2.0)]
    [InlineData("1m30s", 90.0)]
    public void RetryPolicyParsesProviderResetDurations(string raw, double expectedSeconds)
    {
        var method = typeof(LegendFounderAiConversationService).GetMethod("TryParseProviderDuration", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var args = new object?[] { raw, null };
        Assert.True(Assert.IsType<bool>(method!.Invoke(null, args)));
        var duration = Assert.IsType<TimeSpan>(args[1]);
        Assert.InRange(duration.TotalSeconds, expectedSeconds - 0.001, expectedSeconds + 0.001);
    }

    [Theory]
    [InlineData("Hi")]
    [InlineData("Hello Legend® Ai, can you help me think through my day?")]
    [InlineData("I have three priorities today and I want help deciding the best order to attack them without overcomplicating the plan.")]
    public void CasualProviderBudgetDoesNotSelfTerminateHealthyProviderWork(string text)
    {
        var method = typeof(LegendFounderAiConversationService).GetMethod("ResolveProviderBudget", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        IReadOnlyList<LegendFounderAiChatMessage> conversation = [new("user", text)];
        var budget = Assert.IsType<TimeSpan>(method!.Invoke(null, new object[]
        {
            conversation,
            false,
            false,
            TimeSpan.FromSeconds(120)
        }));

        Assert.Equal(TimeSpan.FromSeconds(75), budget);
    }

    [Fact]
    public void GovernedProviderBudgetRetainsDeepInspectionWindow()
    {
        var method = typeof(LegendFounderAiConversationService).GetMethod("ResolveProviderBudget", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        IReadOnlyList<LegendFounderAiChatMessage> conversation = [new("user", "What is the current system state?")];
        var budget = Assert.IsType<TimeSpan>(method!.Invoke(null, new object[]
        {
            conversation,
            true,
            true,
            TimeSpan.FromSeconds(120)
        }));

        Assert.Equal(TimeSpan.FromSeconds(75), budget);
    }

    [Fact]
    public void CasualOutputBudgetScalesBelowGovernedConfiguredMaximum()
    {
        var method = typeof(LegendFounderAiConversationService).GetMethod("ResolveMaxOutputTokens", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        IReadOnlyList<LegendFounderAiChatMessage> conversation = [new("user", "Hi")];
        var casual = Assert.IsType<int>(method!.Invoke(null, new object[] { conversation, false, 8_000 }));
        var governed = Assert.IsType<int>(method.Invoke(null, new object[] { conversation, true, 8_000 }));

        Assert.InRange(casual, 256, 1_200);
        Assert.Equal(8_000, governed);
    }

    [Theory]
    [InlineData(null, "auto")]
    [InlineData("", "auto")]
    [InlineData("fast", "auto")]
    [InlineData("AUTO", "auto")]
    [InlineData("default", "default")]
    [InlineData("flex", "flex")]
    [InlineData("priority", "priority")]
    [InlineData("unsupported", "auto")]
    public void ServiceTierNormalizationMatchesResponsesApiContract(string? configured, string expected)
    {
        var method = typeof(LegendFounderAiConversationService).GetMethod("NormalizeServiceTier", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        Assert.Equal(expected, Assert.IsType<string>(method!.Invoke(null, new object?[] { configured })));
    }

    [Theory]
    [InlineData(false, 0, "none")]
    [InlineData(false, 3, "none")]
    [InlineData(true, 0, "low")]
    [InlineData(true, 1, "medium")]
    public void ReasoningEffortKeepsConversationLightAndGovernedInspectionDeep(bool governed, int round, string expected)
    {
        var method = typeof(LegendFounderAiConversationService).GetMethod("ResolveReasoningEffortForRound", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        Assert.Equal(expected, Assert.IsType<string>(method!.Invoke(null, new object[] { round, governed, "medium" })));
    }

    [Fact]
    public void RetainedKnowledgeQueryCarriesPriorFounderContextWhenAvailable()
    {
        var method = typeof(LegendFounderAiConversationService).GetMethod("BuildRetainedKnowledgeQuery", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        IReadOnlyList<LegendFounderAiChatMessage> conversation =
        [
            new("user", "Earlier context about Haitian Creole discourse markers."),
            new("assistant", "Understood."),
            new("user", "How should that affect the next contrast family?")
        ];

        var query = Assert.IsType<string>(method!.Invoke(null, new object[] { conversation }));
        Assert.Contains("How should that affect", query, StringComparison.Ordinal);
        Assert.Contains("Earlier context about Haitian Creole", query, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1_000, 2_000)]
    [InlineData(5_000, 8_000)]
    [InlineData(20_000, 16_000)]
    public void RetainedKnowledgeQueryBudgetScalesWithRequestSize(int queryLength, int expectedBudget)
    {
        var method = typeof(LegendFounderAiConversationService).GetMethod("ResolveRetainedKnowledgeQueryBudget", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        Assert.Equal(expectedBudget, Assert.IsType<int>(method!.Invoke(null, new object[] { queryLength })));
    }

    [Fact]
    public void ProviderConversationBudgetUsesAvailableConversationWithoutArtificialPadding()
    {
        var method = typeof(LegendFounderAiConversationService).GetMethod("ResolveProviderConversationBudget", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        IReadOnlyList<LegendFounderAiChatMessage> conversation =
        [
            new("user", new string('a', 70_000)),
            new("assistant", new string('b', 20_000)),
            new("user", new string('c', 20_000))
        ];

        var budget = Assert.IsType<int>(method!.Invoke(null, new object[] { conversation }));
        Assert.Equal(110_000, budget);
    }

    [Fact]
    public void RetainedKnowledgeTakeScalesButRemainsBounded()
    {
        var method = typeof(LegendFounderAiConversationService).GetMethod("ResolveRetainedKnowledgeTake", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var shortTake = Assert.IsType<int>(method!.Invoke(null, new object[] { "simple query" }));
        var longTake = Assert.IsType<int>(method.Invoke(null, new object[] { string.Join(' ', Enumerable.Range(0, 800).Select(index => $"term{index}")) }));

        Assert.InRange(shortTake, 12, 32);
        Assert.Equal(32, longTake);
    }
}
