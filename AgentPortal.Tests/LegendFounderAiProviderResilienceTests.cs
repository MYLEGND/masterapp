using System.Net;
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
}
