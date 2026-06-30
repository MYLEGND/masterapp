using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentPortal.Models.Analytics;
using AgentPortal.Services.Analytics;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPortal.Tests;

public class AnalyticsQueryServiceDeviceIntelligenceTests
{
    private static AnalyticsQueryService BuildService(MasterAppDbContext db)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Analytics:EnvironmentFilter"] = "production",
                ["Analytics:ExcludeLocalHosts"] = "true"
            })
            .Build();

        return new AnalyticsQueryService(db, config);
    }

    private static TimeRangeRequest BuildRange(DateTime nowUtc, TrafficQualityMode qualityMode = TrafficQualityMode.RealHumanTraffic) => new()
    {
        FromUtc = nowUtc.AddHours(-1),
        ToUtc = nowUtc.AddHours(1),
        Grouping = TimeGrouping.Day,
        Label = "test",
        Preset = "custom",
        QualityMode = qualityMode
    };

    private static AnalyticsEvent E(
        string eventType,
        DateTime eventUtc,
        string? sessionId,
        string? visitorId,
        string? deviceType,
        string? browser,
        string? operatingSystem)
        => new()
        {
            EventId = Guid.NewGuid(),
            EventType = eventType,
            EventUtc = eventUtc,
            ReceivedUtc = eventUtc,
            SessionId = sessionId,
            VisitorId = visitorId,
            DeviceType = deviceType,
            Browser = browser,
            OperatingSystem = operatingSystem,
            Environment = "production",
            Host = "portal.mylegnd.com"
        };

    [Fact]
    public async Task GetDeviceIntelligenceAsync_UsesVisitorFallbackProfiles_WhenSessionIdMissing()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var now = DateTime.UtcNow;

        db.AnalyticsEvents.AddRange(
            E("page_view", now.AddMinutes(-10), "session-1", "visitor-1", "desktop", "chrome", "windows"),
            E("page_view", now.AddMinutes(-5), null, "visitor-only-1", "mobile", "safari", "ios")
        );
        await db.SaveChangesAsync();

        var service = BuildService(db);
        var dto = await service.GetDeviceIntelligenceAsync(
            BuildRange(now, TrafficQualityMode.AllTraffic),
            ScopeContext.Global);

        Assert.Equal(1, dto.Sessions);
        Assert.Equal(2, dto.IdentityProfiles);
        Assert.Equal(1, dto.VisitorFallbackProfiles);
        Assert.Contains(dto.Devices, row => row.Label == "Desktop" && row.Sessions == 1);
        Assert.Contains(dto.Devices, row => row.Label == "Mobile" && row.Sessions == 1);
    }
}
