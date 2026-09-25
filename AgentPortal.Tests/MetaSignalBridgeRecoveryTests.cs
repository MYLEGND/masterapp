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
