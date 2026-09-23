using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Controllers;
using AgentPortal.Models.Analytics;
using AgentPortal.Services;
using AgentPortal.Services.Analytics;
using AgentPortal.Services.Tracking;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public class WebsiteAnalyticsScopeTests
{
    [Fact]
    public async Task Summary_TeamTrue_NonFounder_RemainsAgentScoped()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var profileId = Guid.NewGuid();
        var trackingProfile = new AgentTrackingProfile
        {
            Id = profileId,
            AgentUserId = "agent-1",
            AgentUpn = "agent@example.com",
            Slug = "agent-1",
            Status = "Active",
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        var tracking = new Mock<IAgentTrackingService>();
        tracking.Setup(x => x.GetByUserIdAsync("agent-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(trackingProfile);

        ScopeContext? captured = null;
        var analytics = new Mock<IAnalyticsQueryService>();
        analytics.Setup(x => x.GetSummaryAsync(It.IsAny<TimeRangeRequest>(), It.IsAny<ScopeContext>(), It.IsAny<TrafficType>()))
            .Callback<TimeRangeRequest, ScopeContext, TrafficType>((_, scope, _) => captured = scope)
            .ReturnsAsync(new SummaryKpiDto());

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Founder:Upn"] = "founder@example.com" })
            .Build();

        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("oid", "agent-1"),
            new Claim("preferred_username", "agent@example.com")
        }, "TestAuth"));
        var http = new DefaultHttpContext { User = user };

        var accessor = new HttpContextAccessor { HttpContext = http };
        var effective = new EffectiveAgentContext(accessor, tracking.Object, NullLogger<EffectiveAgentContext>.Instance);
        var protector = new MetaCapiCredentialProtector(DataProtectionProvider.Create("AgentPortal.Tests"));
        var aiDataBuilder = new WebsiteAnalyticsAiDataBuilder(
            analytics.Object,
            Mock.Of<IMetaAdsService>(),
            Mock.Of<IMetaSignalAnalyticsService>(),
            NullLogger<WebsiteAnalyticsAiDataBuilder>.Instance);

        var metaConnections = new Mock<IMetaAdsConnectionStore>();
        analytics.Setup(x => x.LoadAttributedEventsAsync(It.IsAny<TimeRangeRequest>(), It.IsAny<ScopeContext>(), It.IsAny<TrafficType>(), It.IsAny<CancellationToken>()))
            .Callback<TimeRangeRequest, ScopeContext, TrafficType, CancellationToken>((_, scope, _, _) => captured = scope)
            .ReturnsAsync(new List<AnalyticsEvent>());
        analytics.Setup(x => x.LoadScopedMetaEventsAsync(It.IsAny<TimeRangeRequest>(), It.IsAny<ScopeContext>(), It.IsAny<IReadOnlyCollection<AnalyticsEvent>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MetaSignalEvent>());
        var controller = new WebsiteAnalyticsController(
            analytics.Object,
            Mock.Of<IMetaAdsService>(),
            Mock.Of<IMetaAdsOAuthService>(),
            metaConnections.Object,
            tracking.Object,
            Mock.Of<IMetaSignalAnalyticsService>(),
            Mock.Of<ILandingRouteDiscoveryService>(),
            aiDataBuilder,
            Mock.Of<IVisitorConcentrationService>(),
            Mock.Of<IKpiDetailBreakdownService>(),
            new VisitorTrustScoringService(),
            Mock.Of<IAnalyticsIncidentQueryService>(),
            NullLogger<WebsiteAnalyticsController>.Instance,
            db,
            config,
            effective,
            protector)
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };

        var result = await controller.Summary(null, null, null, null, team: true);

        Assert.IsType<JsonResult>(result);
        Assert.NotNull(captured);
        Assert.Equal(ScopeType.Agent, captured!.ScopeType);
        Assert.Equal(profileId, captured.AgentTrackingProfileId);
        var otherAgent = Guid.NewGuid();
        await controller.VisitorTimeline("shared-visitor", agentProfileId: otherAgent, team: true);
        Assert.Equal(profileId, captured.AgentTrackingProfileId);
        await controller.MetaDisconnect(otherAgent, team: true);
        metaConnections.Verify(x => x.DeleteAsync(profileId, It.IsAny<CancellationToken>()), Times.Once);
        metaConnections.Verify(x => x.DeleteAsync(otherAgent, It.IsAny<CancellationToken>()), Times.Never);
    }
}
