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
}
