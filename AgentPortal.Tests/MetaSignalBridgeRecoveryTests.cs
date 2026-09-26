using Infrastructure.Analytics;
using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Infrastructure.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public sealed class MetaSignalBridgeRecoveryTests
{
    [Fact]
    public async Task FailedWriteKeepsWatermarkAndNextPollPersistsExactlyOnce()
    {
        var failure = new FailFirstDerivedWrite();
        var database = Guid.NewGuid().ToString();
        await using var provider = new ServiceCollection().AddDbContext<MasterAppDbContext>(options =>
            options.UseInMemoryDatabase(database).AddInterceptors(failure)).BuildServiceProvider();
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MasterAppDbContext>();
            db.AnalyticsEvents.Add(new AnalyticsEvent
            {
                EventId = Guid.NewGuid(), EventType = "quote_landing_view", SessionId = "recovery-session",
                VisitorId = "recovery-visitor", EventUtc = DateTime.UtcNow, ReceivedUtc = DateTime.UtcNow,
                Environment = "production", Host = "protect.mylegnd.com", PageKey = "quote_life_landing"
            });
            await db.SaveChangesAsync();
        }
        var bridge = new MetaSignalAnalyticsBridge(provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new MetaSignalIntelligenceOptions
            { Enabled = true, PersistEvents = true, AnalyticsBridgeEnabled = true }),
            NullLogger<MetaSignalAnalyticsBridge>.Instance);
        var method = typeof(MetaSignalAnalyticsBridge).GetMethod("ProcessBatchAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task<bool>)method.Invoke(bridge, new object[] { CancellationToken.None })!;
        await using (var scope = provider.CreateAsyncScope())
            Assert.Empty(await scope.ServiceProvider.GetRequiredService<MasterAppDbContext>().MetaSignalEvents.ToListAsync());
        await (Task<bool>)method.Invoke(bridge, new object[] { CancellationToken.None })!;
        await (Task<bool>)method.Invoke(bridge, new object[] { CancellationToken.None })!;
        await using (var scope = provider.CreateAsyncScope())
            Assert.Single(await scope.ServiceProvider.GetRequiredService<MasterAppDbContext>().MetaSignalEvents.ToListAsync());
    }

    [Fact]
    public async Task RestartReplaysLookbackGapInsteadOfTrustingHighestGlobalBridgeId()
    {
        var database = Guid.NewGuid().ToString();
        await using var provider = new ServiceCollection().AddDbContext<MasterAppDbContext>(options =>
            options.UseInMemoryDatabase(database)).BuildServiceProvider();

        long gapId;
        long laterId;
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MasterAppDbContext>();
            var gap = new AnalyticsEvent
            {
                EventId = Guid.NewGuid(), EventType = "quote_landing_view", SessionId = "founder-gap-session",
                VisitorId = "founder-gap-visitor", EventUtc = DateTime.UtcNow, ReceivedUtc = DateTime.UtcNow,
                Environment = "production", Host = "protect.mylegnd.com", PageKey = "quote_life"
            };
            var later = new AnalyticsEvent
            {
                EventId = Guid.NewGuid(), EventType = "quote_landing_view", SessionId = "other-session",
                VisitorId = "other-visitor", EventUtc = DateTime.UtcNow, ReceivedUtc = DateTime.UtcNow,
                Environment = "production", Host = "example.test", PageKey = "quote_home"
            };
            db.AnalyticsEvents.AddRange(gap, later);
            await db.SaveChangesAsync();
            gapId = gap.Id;
            laterId = later.Id;

            db.MetaSignalEvents.Add(new MetaSignalEvent
            {
                EventId = "already-bridged-later",
                EventName = "ViewContent",
                EventCategory = "page",
                CreatedUtc = DateTime.UtcNow,
                SessionId = later.SessionId,
                VisitorId = later.VisitorId,
                MetadataJson = MetaSignalAnalyticsBridgeMetadata.Build(
                    later, "ViewContent", "later-dedup", TrafficType.NonPaid, null, null, null, null, null, null)
            });
            await db.SaveChangesAsync();
        }

        Assert.True(laterId > gapId);

        var bridge = new MetaSignalAnalyticsBridge(provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new MetaSignalIntelligenceOptions
            {
                Enabled = true,
                PersistEvents = true,
                AnalyticsBridgeEnabled = true,
                AnalyticsBridgeStartupLookbackHours = 24
            }),
            NullLogger<MetaSignalAnalyticsBridge>.Instance);
        var method = typeof(MetaSignalAnalyticsBridge).GetMethod("ProcessBatchAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task<bool>)method.Invoke(bridge, new object[] { CancellationToken.None })!;

        await using var verify = provider.CreateAsyncScope();
        var rows = await verify.ServiceProvider.GetRequiredService<MasterAppDbContext>().MetaSignalEvents.ToListAsync();
        Assert.Contains(rows, row =>
            row.MetadataJson != null &&
            MetaSignalAnalyticsBridgeMetadata.ReadInt64(row.MetadataJson, "sourceAnalyticsEventId") == gapId);
    }

    private sealed class FailFirstDerivedWrite : SaveChangesInterceptor
    {
        private bool _failed;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!_failed && eventData.Context!.ChangeTracker.Entries<MetaSignalEvent>().Any(x => x.State == EntityState.Added))
            {
                _failed = true;
                throw new DbUpdateException("Simulated transient persistence failure");
            }
            return ValueTask.FromResult(result);
        }
    }
}
