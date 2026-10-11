using System;
using System.Threading.Tasks;
using System.Reflection;
using System.Collections;
using System.Linq;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPortal.Tests;

public class MetaSendAuthorityTests
{
    [Fact]
    public async Task AdversarialThirtyTwoConcurrentDispatchersGetOnlyOneDurableClaim()
    {
        var database = System.IO.Path.Combine(System.IO.Path.GetTempPath(),"legend-meta-stress-"+Guid.NewGuid().ToString("N")+".db");
        var options = new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite("Data Source="+database+";Pooling=False").Options;
        var eventId="stress-"+Guid.NewGuid().ToString("N");
        var key="Lead:"+Guid.NewGuid().ToString("N");
        try
        {
            await using(var seed=new MasterAppDbContext(options))
            {
                await seed.Database.EnsureCreatedAsync();
                seed.MetaSignalEvents.Add(new Domain.Entities.MetaSignalEvent {
                    EventId=eventId,EventName="Lead",CreatedUtc=DateTime.UtcNow,MetaDeduplicationKey=key
                });
                await seed.SaveChangesAsync();
            }
            var start=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var tasks=Enumerable.Range(0,32).Select(async _=>{
                await start.Task;
                await using var db=new MasterAppDbContext(options);
                return await new MetaSendAuthority(db,NullLogger<MetaSendAuthority>.Instance).TrySendAsync(new MetaSendAuthorityRequest {
                    EventType="Lead",EventId=eventId,DeduplicationKey=key,EventUtc=DateTime.UtcNow,
                    Source=MetaSendAuthoritySources.MetaSignalOutcomeDispatcherHostedService
                });
            }).ToArray();
            start.SetResult();
            var results=await Task.WhenAll(tasks);
            Assert.Single(results.Where(result=>result.Allowed));
            await using var verify=new MasterAppDbContext(options);
            var row=await verify.MetaSignalEvents.SingleAsync();
            Assert.Equal(results.Single(result=>result.Allowed).ReservationToken,row.MetaDispatchClaimToken);
            Assert.False(row.MetaServerSent);
        }
        finally {System.IO.File.Delete(database);}
    }

    [Fact]
    public async Task BusinessOwnersDoNotShareMetaDeduplicationReservations()
    {
        var services = new ServiceCollection();
        services.AddDbContext<MasterAppDbContext>(options =>
            options.UseInMemoryDatabase(Guid.NewGuid().ToString()));

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MasterAppDbContext>();
        var authority = new MetaSendAuthority(db, NullLogger<MetaSendAuthority>.Instance);
        var now = DateTime.UtcNow;
        var firstOwner = Guid.NewGuid();
        var secondOwner = Guid.NewGuid();
        const string eventKey = "Lead:shared-browser-event";

        var first = await authority.TrySendAsync(new MetaSendAuthorityRequest
        {
            EventType = "Lead",
            CommerceBusinessId = firstOwner,
            SessionId = "same-session",
            EventUtc = now,
            DeduplicationKey = eventKey,
            Source = MetaSendAuthoritySources.MetaSignalAnalyticsBridge
        });
        var second = await authority.TrySendAsync(new MetaSendAuthorityRequest
        {
            EventType = "Lead",
            CommerceBusinessId = secondOwner,
            SessionId = "same-session",
            EventUtc = now,
            DeduplicationKey = eventKey,
            Source = MetaSendAuthoritySources.MetaSignalAnalyticsBridge
        });

        Assert.True(first.Allowed);
        Assert.True(second.Allowed);
        Assert.NotEqual(first.DedupeKey, second.DedupeKey);
        Assert.StartsWith("business:" + firstOwner.ToString("N") + ":", first.DedupeKey);
        Assert.StartsWith("business:" + secondOwner.ToString("N") + ":", second.DedupeKey);
    }

    [Fact]
    public async Task TrySendAsync_UsesExplicitDeduplicationKeyBeforeLeadAndSessionIdentity()
    {
        var services = new ServiceCollection();
        services.AddDbContext<MasterAppDbContext>(options =>
            options.UseInMemoryDatabase(Guid.NewGuid().ToString()));

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MasterAppDbContext>();
        var authority = new MetaSendAuthority(db, NullLogger<MetaSendAuthority>.Instance);
        var now = DateTime.UtcNow;
        const string eventKey = "Lead:shared-authority-key";

        var first = await authority.TrySendAsync(new MetaSendAuthorityRequest
        {
            EventType = "Lead",
            LeadId = Guid.NewGuid(),
            SessionId = "session-a",
            EventUtc = now,
            DeduplicationKey = eventKey,
            Source = MetaSendAuthoritySources.MetaSignalAnalyticsBridge
        });

        var second = await authority.TrySendAsync(new MetaSendAuthorityRequest
        {
            EventType = "Lead",
            LeadId = Guid.NewGuid(),
            SessionId = "session-b",
            EventUtc = now,
            DeduplicationKey = eventKey,
            Source = MetaSendAuthoritySources.MetaSignalAnalyticsBridge
        });

        Assert.True(first.Allowed);
        Assert.Equal(eventKey, first.DedupeKey);

        Assert.False(second.Allowed);
        Assert.Equal(eventKey, second.DedupeKey);
    }


    [Fact]
    public async Task SeparateAuthorityInstancesCannotClaimTheSamePersistedSignalConcurrently()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContext<MasterAppDbContext>(options => options.UseSqlite(connection));

        await using var provider = services.BuildServiceProvider();
        await using (var migrationScope = provider.CreateAsyncScope())
        {
            var migrationDb = migrationScope.ServiceProvider.GetRequiredService<MasterAppDbContext>();
            await migrationDb.Database.EnsureCreatedAsync();
        }
        var eventId = "durable-claim-" + Guid.NewGuid().ToString("N");
        var dedupeKey = "Lead:" + Guid.NewGuid().ToString("N");

        await using (var seedScope = provider.CreateAsyncScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<MasterAppDbContext>();
            db.MetaSignalEvents.Add(new Domain.Entities.MetaSignalEvent
            {
                CreatedUtc = DateTime.UtcNow,
                EventId = eventId,
                EventName = "Lead",
                EventCategory = "conversion",
                MetaDeduplicationKey = dedupeKey,
                TrafficType = "crm",
                MetadataJson = Shared.Analytics.MetaSignalSingleTruthPolicy.BuildMetadataJson(
                    "Lead", null, null, new { }, false, true, true, true, "test")
            });
            await db.SaveChangesAsync();
        }

        MetaSendAuthorityDecision first;
        await using (var firstScope = provider.CreateAsyncScope())
        {
            var db = firstScope.ServiceProvider.GetRequiredService<MasterAppDbContext>();
            var authority = new MetaSendAuthority(db, NullLogger<MetaSendAuthority>.Instance);
            first = await authority.TrySendAsync(new MetaSendAuthorityRequest
            {
                EventType = "Lead",
                EventId = eventId,
                DeduplicationKey = dedupeKey,
                EventUtc = DateTime.UtcNow,
                Source = MetaSendAuthoritySources.MetaSignalOutcomeDispatcherHostedService
            });
            Assert.True(first.Allowed);

            // Simulate a different app process. The production in-memory reservation
            // cannot cross processes, so only the persisted lease should remain.
            var reservationsField = typeof(MetaSendAuthority)
                .GetField("Reservations", BindingFlags.NonPublic | BindingFlags.Static);
            var reservations = Assert.IsAssignableFrom<IDictionary>(reservationsField?.GetValue(null));
            reservations.Clear();
        }

        await using (var secondScope = provider.CreateAsyncScope())
        {
            var db = secondScope.ServiceProvider.GetRequiredService<MasterAppDbContext>();
            var authority = new MetaSendAuthority(db, NullLogger<MetaSendAuthority>.Instance);
            var second = await authority.TrySendAsync(new MetaSendAuthorityRequest
            {
                EventType = "Lead",
                EventId = eventId,
                DeduplicationKey = dedupeKey,
                EventUtc = DateTime.UtcNow,
                Source = MetaSendAuthoritySources.MetaSignalOutcomeDispatcherHostedService
            });

            Assert.False(second.Allowed);
            Assert.Equal("durable_dispatch_claim_active", second.Note);
        }
    }

    [Fact]
    public async Task FounderSentLookupNeverTreatsAgentOwnedSignalAsFounderDuplicate()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContext<MasterAppDbContext>(options => options.UseSqlite(connection));

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var setupDb = scope.ServiceProvider.GetRequiredService<MasterAppDbContext>();
        await setupDb.Database.EnsureCreatedAsync();
        var db = scope.ServiceProvider.GetRequiredService<MasterAppDbContext>();
        var agentId = Guid.NewGuid();
        var sharedKey = "PolicyPaid:shared-key";

        db.MetaSignalEvents.Add(new Domain.Entities.MetaSignalEvent
        {
            CreatedUtc = DateTime.UtcNow,
            EventId = "agent-event",
            EventName = "PolicyPaid",
            AgentTrackingProfileId = agentId,
            MetaDeduplicationKey = sharedKey,
            MetaServerSent = true,
            TrafficType = "crm"
        });
        db.MetaSignalEvents.Add(new Domain.Entities.MetaSignalEvent
        {
            CreatedUtc = DateTime.UtcNow,
            EventId = "founder-event",
            EventName = "PolicyPaid",
            MetaDeduplicationKey = sharedKey,
            MetaServerSent = false,
            TrafficType = "crm",
            MetadataJson = Shared.Analytics.MetaSignalSingleTruthPolicy.BuildMetadataJson(
                "PolicyPaid", null, null, new { }, false, true, true, true, "test")
        });
        await db.SaveChangesAsync();

        var authority = new MetaSendAuthority(db, NullLogger<MetaSendAuthority>.Instance);
        var decision = await authority.TrySendAsync(new MetaSendAuthorityRequest
        {
            EventType = "PolicyPaid",
            EventId = "founder-event",
            DeduplicationKey = sharedKey,
            EventUtc = DateTime.UtcNow,
            Source = MetaSendAuthoritySources.MetaSignalOutcomeDispatcherHostedService
        });

        Assert.True(decision.Allowed);
    }
}
