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
        analytics.Setup(x => x.GetSummaryAsync(It.IsAny<TimeRangeRequest>(), It.IsAny<ScopeContext>()))
            .Callback<TimeRangeRequest, ScopeContext>((_, scope) => captured = scope)
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

        var controller = new WebsiteAnalyticsController(
            analytics.Object,
            tracking.Object,
            NullLogger<WebsiteAnalyticsController>.Instance,
            db,
            config,
            effective)
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };

        var result = await controller.Summary(null, null, null, null, team: true);

        Assert.IsType<JsonResult>(result);
        Assert.NotNull(captured);
        Assert.Equal(ScopeType.Agent, captured!.ScopeType);
        Assert.Equal(profileId, captured.AgentTrackingProfileId);
    }
}
