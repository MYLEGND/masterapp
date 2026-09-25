using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Services;
using AgentPortal.Services.Analytics;
using AgentPortal.Services.Tracking;
using Domain.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

[Collection("Profile website Founder environment")]
public class WebsiteAnalyticsScopeResolverTests : IDisposable
{
    private const string Founder = "33333333-3333-3333-3333-333333333333";
    private readonly string? previousFounder = Environment.GetEnvironmentVariable("FOUNDER_OID");
    public WebsiteAnalyticsScopeResolverTests() => Environment.SetEnvironmentVariable("FOUNDER_OID", Founder);
    public void Dispose() => Environment.SetEnvironmentVariable("FOUNDER_OID", previousFounder);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FounderPersonalDefaultAndExplicitTeamUseSamePolicy(bool team)
    {
        using var db = ControllerTestHelpers.BuildDb();
        var profile = new AgentTrackingProfile { Id = Guid.NewGuid(), AgentUserId = Founder };
        var tracking = new Mock<IAgentTrackingService>();
        tracking.Setup(x => x.GetByUserIdAsync(Founder, It.IsAny<CancellationToken>())).ReturnsAsync(profile);
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("oid", Founder) }, "Test")) };
        var effective = new EffectiveAgentContext(new HttpContextAccessor { HttpContext = http }, tracking.Object, NullLogger<EffectiveAgentContext>.Instance);
        var resolver = new WebsiteAnalyticsScopeResolver(effective, tracking.Object, db, NullLogger.Instance);

        var result = await resolver.ResolveAsync(http, null, team);

        Assert.Equal(team ? ScopeType.Global : ScopeType.Agent, result.ScopeType);
        Assert.Equal(team ? (Guid?)null : profile.Id, result.AgentTrackingProfileId);
    }

    [Fact]
    public async Task MissingImpersonatedProfileNeverFallsBackToFounder()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var tracking = new Mock<IAgentTrackingService>();
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("oid", Founder) }, "Test")) };
        http.Items["EffectiveAgentOid"] = "another-agent";
        var effective = new EffectiveAgentContext(new HttpContextAccessor { HttpContext = http }, tracking.Object, NullLogger<EffectiveAgentContext>.Instance);
        var resolver = new WebsiteAnalyticsScopeResolver(effective, tracking.Object, db, NullLogger.Instance);

        var result = await resolver.ResolveAsync(http, null);

        Assert.Equal(ScopeType.Agent, result.ScopeType);
        Assert.Equal(Guid.Empty, result.AgentTrackingProfileId);
        Assert.Null(result.Workspace);
        tracking.Verify(x => x.GetByUserIdAsync(Founder, It.IsAny<CancellationToken>()), Times.Never);
    }
}
