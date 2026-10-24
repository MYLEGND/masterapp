using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Bookings;
using Infrastructure.Data;
using Infrastructure.Reception;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public sealed partial class ReceptionistInquiryTests
{
    [Theory]
    [InlineData("founder")]
    [InlineData("agent")]
    [InlineData("business")]
    public async Task ConfiguredBookingReadinessUsesOnlyRelationalMetadataWithoutMaterializingClientOrCredentialEntities(string kind)
    {
        var owner = kind == "agent" ? MarketingOwnerScope.Agent(Guid.NewGuid()) : kind == "business"
            ? MarketingOwnerScope.Business(Guid.NewGuid()) : MarketingOwnerScope.Founder;
        await using var f = await Fixture.CreateAsync(owner, bookingEnabled: true);
        var (id, guard) = await ConfigureBookingMetadataAsync(f);
        var reader = new ReceptionistBookingReadinessReader(f.Options, f.Configuration);
        var status = await reader.ReadAsync(owner, id);
        Assert.True(status.CalendarConnectedRecorded); Assert.True(status.AppointmentPermissionRecorded);
        Assert.True(status.BookingAgentEnabled); Assert.True(status.MappingConfigured); Assert.True(status.Requested);
        Assert.Equal("explicit_customer_email_consent_required", status.InvitationPolicy);
        Assert.Equal("provider_and_runtime_acceptance_required", Assert.Single(status.Reasons));
        Assert.Equal("UTC", Assert.Single(status.Services).TimeZoneId); Assert.Equal(30, status.Services[0].DurationMinutes);
        var json = JsonSerializer.Serialize(status);
        foreach (var excluded in new[] { "founder-actor", "agent-actor", "synthetic-booking-agent", "@example.invalid", "mailbox-canary", "client-canary" })
            Assert.DoesNotContain(excluded, json);
        Assert.Equal(0, guard.Writes); Assert.Equal(0, guard.Entities);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAsync(MarketingOwnerScope.Business(Guid.NewGuid()), id));
    }

    [Theory]
    [InlineData("refresh")]
    [InlineData("reconnect")]
    [InlineData("permission")]
    [InlineData("target")]
    [InlineData("disabled")]
    [InlineData("actor")]
    public async Task ReadinessDistinguishesCredentialRefreshFromChangedReviewedAuthority(string change)
    {
        await using var f = await Fixture.CreateAsync(bookingEnabled: true);
        var (id, guard) = await ConfigureBookingMetadataAsync(f);
        var reader = new ReceptionistBookingReadinessReader(f.Options, f.Configuration);
        var original = await reader.ReadAsync(f.Owner, id);
        guard.Armed = false;
        await using (var db = f.Database())
        {
            var calendar = await db.MarketingConnections.SingleAsync(x => x.Provider == MicrosoftCalendarConnectionAuthority.Provider);
            var agent = await db.AgentProfiles.SingleAsync(x => x.Id == f.AgentProfileId);
            if (change == "refresh") { calendar.Revision = Guid.NewGuid(); calendar.LastVerifiedUtc = DateTime.UtcNow; }
            if (change == "reconnect") calendar.ConnectedUtc = calendar.ConnectedUtc!.Value.AddSeconds(1);
            if (change == "permission") calendar.ProviderPermissionsJson = "[]";
            if (change == "target") agent.BookingPageIdOrMailbox = "different-mailbox-canary";
            if (change == "disabled") agent.BookingEnabled = false;
            if (change == "actor") { agent.AgentUpn = "different@example.invalid"; agent.NormalizedEmail = "different@example.invalid"; }
            await db.SaveChangesAsync();
        }
        guard.Armed = true;
        var observed = await reader.ReadAsync(f.Owner, id);
        var expected = change switch
        {
            "reconnect" or "permission" => "calendar_configuration_not_reviewed",
            "target" => "calendar_target_not_reviewed", "disabled" => "booking_agent_disabled",
            "actor" => "explicit_calendar_target_missing", _ => "provider_and_runtime_acceptance_required"
        };
        Assert.Contains(expected, observed.Reasons);
        if (change == "refresh")
        {
            Assert.Equal(original.CalendarConfigurationSha256, observed.CalendarConfigurationSha256);
            Assert.NotEqual(original.CalendarRevision, observed.CalendarRevision); Assert.Single(observed.Reasons);
        }
        Assert.Equal(0, guard.Writes); Assert.Equal(0, guard.Entities);
    }

    private static async Task<(Guid, MetadataGuard)> ConfigureBookingMetadataAsync(Fixture f)
    {
        await using var db = f.Database();
        var actor = f.Owner.CommerceBusinessId.HasValue ? "synthetic-booking-agent" : f.Owner.AgentTrackingProfileId.HasValue ? "agent-actor" : "founder-actor";
        if (f.Owner.CommerceBusinessId is { } business)
        {
            db.AgentProfiles.Add(new() { Id = f.AgentProfileId, AgentUserId = actor, AgentUpn = "synthetic-agent@example.invalid", IsActive = true });
            var client = new ClientProfile { ClientUserId = "synthetic-client", FirstName = "client-canary", Email = "client-canary@example.invalid" };
            db.ClientProfiles.Add(client);
            db.CommerceBusinessMembers.Add(new() { CommerceBusinessId = business, ClientProfileId = client.Id });
            db.AgentClients.Add(new() { AgentUserId = actor, ClientUserId = client.ClientUserId });
            await db.SaveChangesAsync();
        }
        var profile = await db.AgentProfiles.SingleAsync(x => x.Id == f.AgentProfileId);
        profile.IsActive = true; profile.BookingEnabled = true; profile.BookingPageIdOrMailbox = "mailbox-canary";
        db.MarketingConnections.Add(new() { OwnerType = f.Owner.OwnerType, OwnerKey = f.Owner.Key,
            AgentTrackingProfileId = f.Owner.AgentTrackingProfileId, CommerceBusinessId = f.Owner.CommerceBusinessId,
            Provider = MicrosoftCalendarConnectionAuthority.Provider, ConnectedUtc = DateTime.SpecifyKind(new(2026, 2, 1), DateTimeKind.Utc),
            ProviderAuthorizationMethod = MicrosoftCalendarConnectionAuthority.ApplicationAuthorization,
            ProviderPermissionsJson = "[\"BookingsAppointment.ReadWrite.All\"]" });
        await db.SaveChangesAsync();
        var metadata = (await ReceptionistBookingReadinessReader.ReadCalendarAsync(db, f.Owner, default))!;
        var version = await db.Set<ReceptionistConfigurationVersion>().SingleAsync();
        var config = ReceptionistConfigurationCodec.Read(version.ConfigurationJson, version.ConfigurationSha256);
        config = config with { BookingPolicy = config.BookingPolicy! with
            { ExpectedCalendarConfigurationSha256 = metadata.Fingerprint,
              ExpectedCalendarTargetSha256 = ReceptionistBookingReadinessReader.TargetFingerprint(f.Owner, actor, "mailbox-canary") } };
        version.ConfigurationJson = ReceptionistConfigurationCodec.Serialize(config);
        version.ConfigurationSha256 = ReceptionistConfigurationCodec.Hash(version.ConfigurationJson); await db.SaveChangesAsync();
        var guard = new MetadataGuard();
        f.Options = new DbContextOptionsBuilder<MasterAppDbContext>(f.Options).AddInterceptors(guard)
            .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning)).Options;
        return (version.Id, guard);
    }

    private sealed class MetadataGuard : SaveChangesInterceptor, IMaterializationInterceptor
    {
        internal bool Armed = true;
        internal int Writes, Entities;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data,
            InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (Armed) { Writes++; throw new InvalidOperationException("Metadata may not save."); }
            return ValueTask.FromResult(result);
        }
        public object InitializedInstance(MaterializationInterceptionData data, object entity)
        {
            if (Armed && entity.GetType().Namespace == "Domain.Entities")
            { Entities++; throw new InvalidOperationException("Metadata may not materialize full business or personal entities."); }
            return entity;
        }
    }
}
