using System;
using System.Threading.Tasks;
using System.Reflection;
using System.Collections;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProtectWebsite.Services.Meta;
using Xunit;

namespace AgentPortal.Tests;

public class MetaSendAuthorityTests
{
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
