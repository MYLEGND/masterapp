using System;
using System.IO;
using AgentPortal.Services;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace AgentPortal.Tests;

public sealed class RealtimeHubDeploymentContractTests
{
    [Theory]
    [InlineData("/livesync")]
    [InlineData("/livesync/negotiate")]
    [InlineData("/leadbridgehub")]
    [InlineData("/leadbridgehub/connection")]
    [InlineData("/messaginghub")]
    [InlineData("/messaginghub/negotiate")]
    public void GlobalRateLimiter_ExemptsEveryMappedSignalRHub(string path)
    {
        Assert.True(RealtimeHubRateLimitAuthority.IsHubPath(new PathString(path)));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/api/messages")]
    [InlineData("/livesynchronization")]
    [InlineData("/leadbridgehub-admin")]
    [InlineData("/messaginghubris")]
    public void GlobalRateLimiter_DoesNotExemptOrdinaryOrPrefixCollisionRoutes(string path)
    {
        Assert.False(RealtimeHubRateLimitAuthority.IsHubPath(new PathString(path)));
    }

    [Fact]
    public void ProductionDeploy_ProbesBothUnauthenticatedNegotiateChallengesAndRejectsServerFailures()
    {
        var workflow = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "agentportal-production-deploy.yml"));

        Assert.Contains("Verify unauthenticated SignalR negotiate challenges", workflow, StringComparison.Ordinal);
        Assert.Contains("/livesync/negotiate?negotiateVersion=1", workflow, StringComparison.Ordinal);
        Assert.Contains("-ExpectedStatus '302'", workflow, StringComparison.Ordinal);
        Assert.Contains("/messaginghub/negotiate?negotiateVersion=1", workflow, StringComparison.Ordinal);
        Assert.Contains("-ExpectedStatus '401'", workflow, StringComparison.Ordinal);
        Assert.Contains("if ($status -match '^5')", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("--location", workflow, StringComparison.Ordinal);
    }
}
