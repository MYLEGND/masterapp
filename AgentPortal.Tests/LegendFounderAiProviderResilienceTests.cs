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
    public void ProviderConversationBudgetExpandsForLargeFounderConversations()
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
        Assert.Equal(120_000, budget);
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
