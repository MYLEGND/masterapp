using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProtectWebsite.Services.MetaSignal;
using Xunit;

namespace AgentPortal.Tests;

public class MetaSignalAnalyticsBridgeTests
{
    [Fact]
    public async Task ProcessBatch_BackfillsEngagementAndBounceSignalsFromAnalyticsEvents()
    {
        using var services = BuildServices();
        await SeedAnalyticsAsync(services);

        var bridge = new MetaSignalAnalyticsBridge(
            services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new MetaSignalIntelligenceOptions()),
            NullLogger<MetaSignalAnalyticsBridge>.Instance);

        await InvokeProcessBatchAsync(bridge);

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MasterAppDbContext>();

        var engaged = await db.MetaSignalEvents.SingleAsync(x => x.EventName == "SessionEngaged15s");
        var scroll = await db.MetaSignalEvents.SingleAsync(x => x.EventName == "MeaningfulScroll");
        var bounce = await db.MetaSignalEvents.SingleAsync(x => x.EventName == "RapidBounce");

        Assert.Equal(10, engaged.EngagementScore);
        Assert.Equal("session_engaged_15s", engaged.StepName);

        Assert.Equal(5, scroll.EngagementScore);
        Assert.Equal("meaningful_scroll", scroll.StepName);

        Assert.Equal(-15, bounce.FrictionScore);
        Assert.Equal("rapid_bounce", bounce.StepName);
    }

    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddDbContext<MasterAppDbContext>(options =>
            options.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        return services.BuildServiceProvider();
    }

    private static async Task SeedAnalyticsAsync(ServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MasterAppDbContext>();
        var now = DateTime.UtcNow;

        db.AnalyticsEvents.AddRange(
            new AnalyticsEvent
            {
                EventId = Guid.NewGuid(),
                EventType = "page_engaged_30s",
                SessionId = "session-engaged",
                VisitorId = "visitor-engaged",
                PageKey = "quote_life_landing",
                EventUtc = now,
                ReceivedUtc = now,
                EngagedMilliseconds = 30_000,
                DwellMilliseconds = 30_000,
                ScrollPercent = 65,
                Environment = "development",
                Host = "localhost:1111"
            },
            new AnalyticsEvent
            {
                EventId = Guid.NewGuid(),
                EventType = "scroll_depth_50",
                SessionId = "session-scroll",
                VisitorId = "visitor-scroll",
                PageKey = "quote_life_landing",
                EventUtc = now,
                ReceivedUtc = now,
                ScrollPercent = 50,
                Environment = "development",
                Host = "localhost:1111"
            },
            new AnalyticsEvent
            {
                EventId = Guid.NewGuid(),
                EventType = "page_exit",
                SessionId = "session-bounce",
                VisitorId = "visitor-bounce",
                PageKey = "quote_life_landing",
                EventUtc = now,
                ReceivedUtc = now,
                DwellMilliseconds = 2_000,
                EngagedMilliseconds = 500,
                ScrollPercent = 10,
                IsBounceCandidate = true,
                IsExitPage = true,
                Environment = "development",
                Host = "localhost:1111"
            });

        await db.SaveChangesAsync();
    }

    private static async Task<bool> InvokeProcessBatchAsync(MetaSignalAnalyticsBridge bridge, CancellationToken cancellationToken = default)
    {
        var method = typeof(MetaSignalAnalyticsBridge).GetMethod("ProcessBatchAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var task = method!.Invoke(bridge, [cancellationToken]);
        var typedTask = Assert.IsType<Task<bool>>(task);
        return await typedTask;
    }
}
