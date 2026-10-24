using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Bookings;
using Infrastructure.Leads;
using Infrastructure.Reception;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public sealed partial class ReceptionistInquiryTests
{
    [Fact]
    public async Task SyntheticBookingClassificationIsAtomicAndRecoversItsLostCommitAcknowledgement()
    {
        await using var f = await Fixture.CreateAsync(bookingEnabled: true);
        var call = await f.CallAsync(true); var intake = await f.ClaimAsync(call, Facts());
        await f.Inquiries.PersistQaOutcomeAsync(call, intake, Facts(), Receipt);
        using var services = new ServiceCollection().BuildServiceProvider();
        var lease = f.Leases[call.CallId];
        var option = Assert.Single(await Booking(f, services).PrepareAsync(call, intake, "website_services",
            DateOnly.FromDateTime(f.Clock.Now.AddDays(1)), lease, default));
        // Claim commits first; the next transaction atomically classifies the QA outcome.
        f.Options = new DbContextOptionsBuilder<Infrastructure.Data.MasterAppDbContext>(f.Options)
            .AddInterceptors(new LostCommitAcknowledgement(commitsToSkip: 1)).Options;
        Assert.True((await Booking(f, services).CommitAsync(call, option.Handle, true, true, "synthetic-consent", lease, default)).Verified);
        Assert.True((await Booking(f, services).CommitAsync(call, option.Handle, true, true, "synthetic-consent", lease, default)).Verified);
        await using var read = f.Database();
        var row = Assert.Single(await read.Set<ReceptionistActionIntent>().Where(x => x.CapabilityKey == ReceptionistCapabilities.CanonicalBooking).ToListAsync());
        Assert.Equal("verified", row.State); Assert.Empty(await read.LeadAppointments.ToListAsync());
        Assert.Empty(await read.WebsiteLeads.ToListAsync()); Assert.Empty(await read.WorkstationLeadProfiles.ToListAsync());
    }

    [Theory]
    [InlineData("call")]
    [InlineData("configuration")]
    [InlineData("provider_session")]
    public async Task SyntheticBookingTicketCannotBeReattachedToDifferentRecordedScope(string change)
    {
        await using var f = await Fixture.CreateAsync(bookingEnabled: true);
        var call = await f.CallAsync(true); var intake = await f.ClaimAsync(call, Facts());
        await f.Inquiries.PersistQaOutcomeAsync(call, intake, Facts(), Receipt);
        using var services = new ServiceCollection().BuildServiceProvider(); var lease = f.Leases[call.CallId];
        var option = Assert.Single(await Booking(f, services).PrepareAsync(call, intake, "website_services",
            DateOnly.FromDateTime(f.Clock.Now.AddDays(1)), lease, default));
        Assert.True((await Booking(f, services).CommitAsync(call, option.Handle, true, true, "synthetic-consent", lease, default)).Verified);
        var other = change == "call" ? await f.CallAsync(true) : call;
        await using var db = f.Database();
        var action = await db.Set<ReceptionistActionIntent>().SingleAsync(x => x.CapabilityKey == ReceptionistCapabilities.CanonicalBooking);
        var original = await db.Set<ReceptionistCall>().SingleAsync(x => x.Id == call.CallId);
        var configurationId = call.ConfigurationId;
        if (change == "call") action.CallId = other.CallId;
        else if (change == "provider_session") original.ProviderSessionId = "different-synthetic-session";
        else
        {
            var version = await db.Set<ReceptionistConfigurationVersion>().AsNoTracking().SingleAsync(x => x.Id == call.ConfigurationId);
            var next = new ReceptionistConfigurationVersion { MarketingConnectionId = version.MarketingConnectionId, Version = version.Version + 1,
                ConfigurationJson = version.ConfigurationJson, ConfigurationSha256 = version.ConfigurationSha256, CreatedByActorId = "synthetic" };
            db.Add(next); original.ConfigurationVersionId = next.Id; configurationId = next.Id;
        }
        await db.SaveChangesAsync();
        var ticket = Assert.Single(await f.Admission.ReadQaOutcomesAsync(f.Owner.Key, configurationId, action.Id, default));
        Assert.Equal("Unresolved synthetic outcome", ticket.State);
    }
    [Fact]
    public async Task QaOutcomeRespectsTheCurrentLeaseAndRecoversALostCommitAcknowledgement()
    {
        await using var expired = await Fixture.CreateAsync();
        var expiredCall = await expired.CallAsync(true); var expiredAction = await expired.ClaimAsync(expiredCall, Facts());
        expired.Clock.Now = expired.Clock.Now.AddSeconds(31);
        await Assert.ThrowsAsync<InvalidOperationException>(() => expired.Inquiries.PersistQaOutcomeAsync(expiredCall, expiredAction, Facts(), Receipt));
        Assert.Null(await expired.Inquiries.ReadSafeOutcomeAsync(expiredCall, expiredAction));
        await using var f = await Fixture.CreateAsync();
        var call = await f.CallAsync(true); var action = await f.ClaimAsync(call, Facts());
        f.Options = new DbContextOptionsBuilder<Infrastructure.Data.MasterAppDbContext>(f.Options).AddInterceptors(new LostCommitAcknowledgement()).Options;
        await Assert.ThrowsAsync<System.IO.IOException>(() => f.Inquiries.PersistQaOutcomeAsync(call, action, Facts(), Receipt));
        Assert.Equal("qa_outcome_verified", (await f.Inquiries.ReadSafeOutcomeAsync(call, action))!.State);
        Assert.False((await f.Inquiries.PersistQaOutcomeAsync(call, action, Facts(), Receipt)).Created);
        var ticket = Assert.Single(await f.Admission.ReadQaOutcomesAsync(f.Owner.Key, call.ConfigurationId, action, default));
        Assert.Equal(action, ticket.TicketId); Assert.Equal("Independently verified synthetic outcome", ticket.State);
        Assert.Empty(await f.Admission.ReadQaOutcomesAsync("foreign-owner", call.ConfigurationId, action, default));
    }

    [Fact]
    public async Task RecordedBookingCanBeReconciledAfterLeaseAndHandleExpiryWithoutAnotherCreate()
    {
        await using var f = await Fixture.CreateAsync(bookingEnabled: true, qaOnly: false);
        var call = await f.CallAsync(); var intake = await f.ClaimAsync(call, Facts());
        await PersistBookingIntakeAsync(f, call, intake);
        var gateway = new BookingGatewayFake { Verify = false };
        using var services = new ServiceCollection().AddSingleton<IReceptionistBookingGateway>(gateway).BuildServiceProvider();
        var authority = Booking(f, services); var lease = f.Leases[call.CallId];
        var option = Assert.Single(await authority.PrepareAsync(call, intake, "website_services", DateOnly.FromDateTime(f.Clock.Now.AddDays(1)), lease, default));
        Assert.False((await authority.CommitAsync(call, option.Handle, true, true, "consent", lease, default)).Verified);
        f.Clock.Now = f.Clock.Now.AddMinutes(10);
        await using var db = f.Database();
        var connection = await db.MarketingConnections.SingleAsync(x => x.OwnerKey == f.Owner.Key);
        connection.ReceptionistAdmissionEnabled = false; await db.SaveChangesAsync();
        var action = await db.Set<ReceptionistActionIntent>().AsNoTracking().SingleAsync(x => x.CapabilityKey == ReceptionistCapabilities.CanonicalBooking);
        var metadata = await f.Admission.ReadBookingRecoveryPreparationAsync(f.Owner.Key, call.ConfigurationId, call.CallId, default);
        Assert.Equal(action.Revision, Assert.Single(metadata.Actions).ActionRevision);
        Assert.DoesNotContain("synthetic-original-event", JsonSerializer.Serialize(metadata));
        Assert.DoesNotContain(Facts().Email!, JsonSerializer.Serialize(metadata));
        gateway.Verify = true;
        Assert.True((await Booking(f, services).ReconcileRecordedAsync(f.Owner, action.Id, action.Revision)).Verified);
        Assert.Equal(1, gateway.Attempts); Assert.Equal(1, gateway.OriginalReads);
        await using var fresh = f.Database();
        var saved = await fresh.Set<ReceptionistActionIntent>().AsNoTracking().SingleAsync(x => x.Id == action.Id);
        Assert.True(ReceptionistBookingAuthority.VerifiedAction(saved, call.Configuration));
        await Assert.ThrowsAsync<InvalidOperationException>(() => authority.ReconcileRecordedAsync(f.Owner, action.Id, action.Revision));
        Assert.True((await authority.ReconcileRecordedAsync(f.Owner, action.Id, saved.Revision)).Verified);
        Assert.Equal(1, gateway.Attempts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RecoveryRejectsReplacedProviderIdentityOrChangedOriginalIntent(bool replaceIdentity)
    {
        await using var f = await Fixture.CreateAsync(bookingEnabled: true, qaOnly: false);
        var call = await f.CallAsync(); var intake = await f.ClaimAsync(call, Facts());
        await PersistBookingIntakeAsync(f, call, intake);
        var gateway = new BookingGatewayFake { Verify = false };
        using var services = new ServiceCollection().AddSingleton<IReceptionistBookingGateway>(gateway).BuildServiceProvider();
        var authority = Booking(f, services); var lease = f.Leases[call.CallId];
        var option = Assert.Single(await authority.PrepareAsync(call, intake, "website_services", DateOnly.FromDateTime(f.Clock.Now.AddDays(1)), lease, default));
        await authority.CommitAsync(call, option.Handle, true, true, "consent", lease, default);
        await using var db = f.Database();
        var action = await db.Set<ReceptionistActionIntent>().SingleAsync(x => x.CapabilityKey == ReceptionistCapabilities.CanonicalBooking);
        if (replaceIdentity) { action.ActualRecordReference = "booking-provider:v1:unrelated-event"; await db.SaveChangesAsync(); }
        else gateway.Original = gateway.Original! with { StartUtc = gateway.Original.StartUtc.AddMinutes(30), EndUtc = gateway.Original.EndUtc.AddMinutes(30) };
        gateway.Verify = true;
        var result = await authority.ReconcileRecordedAsync(f.Owner, action.Id, action.Revision);
        Assert.False(result.Verified); Assert.Equal("reconciliation_evidence_mismatch", result.State);
        Assert.Equal(replaceIdentity ? 0 : 1, gateway.OriginalReads); Assert.Equal(1, gateway.Attempts);
    }

    [Fact]
    public async Task PendingRejectionAndProviderStartCannotBothWin()
    {
        await using var f = await Fixture.CreateAsync(bookingEnabled: true, qaOnly: false);
        var call = await f.CallAsync(); await f.ClaimAsync(call, Facts()); var lease = f.Leases[call.CallId];
        var claim = await f.Admission.ClaimActionAsync(call.CallId, "synthetic-booking-race", ReceptionistCapabilities.CanonicalBooking,
            new string('a', 64), "synthetic-consent", lease);
        var action = claim.ActionId!.Value;
        var started = f.Admission.TryBeginBookingAsync(call.CallId, action, new string('a', 64), "synthetic-consent", lease, false, default);
        var rejected = f.Admission.RejectUnstartedBookingAsync(call.CallId, action, new string('a', 64), default);
        await Task.WhenAll(started, rejected);
        Assert.NotEqual(await started, await rejected);
        await using var db = f.Database(); var row = await db.Set<ReceptionistActionIntent>().SingleAsync(x => x.Id == action);
        Assert.Equal(await started ? "uncertain" : "rejected", row.State);
    }
    [Fact]
    public async Task BookingQaIsDurableAcrossAuthorityRestartWithoutAnyCalendarDependency()
    {
        await using var f = await Fixture.CreateAsync(bookingEnabled: true);
        var call = await f.CallAsync(controlledTest: true); var action = await f.ClaimAsync(call, Facts());
        await f.Inquiries.PersistQaOutcomeAsync(call, action, Facts(), Receipt);
        using var services = new ServiceCollection().BuildServiceProvider(); // No gateway/token/provider can be resolved.
        var authority = Booking(f, services); var lease = f.Leases[call.CallId];
        var option = Assert.Single(await authority.PrepareAsync(call, action, "website_services", DateOnly.FromDateTime(f.Clock.Now.AddDays(1)), lease, default));
        Assert.True(option.Synthetic);
        var result = await authority.CommitAsync(call, option.Handle, true, true, "synthetic-booking-consent", lease, default);
        Assert.True(result.Verified); Assert.True(result.Synthetic); Assert.Equal("qa_booking_outcome_verified", result.State);
        Assert.True((await Booking(f, services).CommitAsync(call, option.Handle, true, true, "synthetic-booking-consent", lease, default)).Verified);
        await using var read = f.Database();
        var row = Assert.Single(await read.Set<ReceptionistActionIntent>().AsNoTracking()
            .Where(x => x.CapabilityKey == ReceptionistCapabilities.CanonicalBooking).ToListAsync());
        Assert.Equal("verified", row.State); Assert.StartsWith("receptionist-qa-booking:", row.ActualRecordReference);
        Assert.Empty(await read.LeadAppointments.ToListAsync()); Assert.Empty(await read.WebsiteLeads.ToListAsync());
        Assert.Empty(await read.WorkstationLeadProfiles.ToListAsync()); Assert.Empty(await read.WebsiteLeadIntakeLinks.ToListAsync());
        Assert.DoesNotContain(Facts().Email!, JsonSerializer.Serialize(result)); Assert.DoesNotContain("synthetic-booking-consent", JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task MissingEitherBookingConsentCreatesNoBookingAction(bool appointmentConsent, bool emailConsent)
    {
        await using var f = await Fixture.CreateAsync(bookingEnabled: true);
        var call = await f.CallAsync(true); var intake = await f.ClaimAsync(call, Facts());
        await f.Inquiries.PersistQaOutcomeAsync(call, intake, Facts(), Receipt);
        using var services = new ServiceCollection().BuildServiceProvider();
        var authority = Booking(f, services); var lease = f.Leases[call.CallId];
        var option = Assert.Single(await authority.PrepareAsync(call, intake, "website_services", DateOnly.FromDateTime(f.Clock.Now.AddDays(1)), lease, default));
        var result = await authority.CommitAsync(call, option.Handle, appointmentConsent, emailConsent, "consent", lease, default);
        Assert.False(result.Verified);
        await using var read = f.Database();
        Assert.Empty(await read.Set<ReceptionistActionIntent>().Where(x => x.CapabilityKey == ReceptionistCapabilities.CanonicalBooking).ToListAsync());
    }

    [Fact]
    public async Task BookingHandleIsBoundToOwnerCallConfigurationModeProviderAndExpiry()
    {
        await using var f = await Fixture.CreateAsync(bookingEnabled: true);
        var call = await f.CallAsync(true); var intake = await f.ClaimAsync(call, Facts());
        await f.Inquiries.PersistQaOutcomeAsync(call, intake, Facts(), Receipt);
        using var services = new ServiceCollection().BuildServiceProvider();
        var authority = Booking(f, services); var lease = f.Leases[call.CallId];
        var option = Assert.Single(await authority.PrepareAsync(call, intake, "website_services", DateOnly.FromDateTime(f.Clock.Now.AddDays(1)), lease, default));
        foreach (var tampered in new[] { call with { Owner = MarketingOwnerScope.Business(Guid.NewGuid()) },
            call with { ConfigurationId = Guid.NewGuid() }, call with { Provider = "other" }, call with { ControlledTest = false } })
            await Assert.ThrowsAsync<InvalidOperationException>(() => authority.CommitAsync(tampered, option.Handle, true, true, "consent", lease, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => authority.CommitAsync(call, "forged-purpose", true, true, "consent", lease, default));
        var second = await f.CallAsync(true); var secondIntake = await f.ClaimAsync(second, Facts());
        await Assert.ThrowsAsync<InvalidOperationException>(() => authority.CommitAsync(second, option.Handle, true, true, "consent", f.Leases[second.CallId], default));
        f.Clock.Now = f.Clock.Now.AddMinutes(6);
        await Assert.ThrowsAsync<InvalidOperationException>(() => authority.CommitAsync(call, option.Handle, true, true, "consent", lease, default));
        await using var read = f.Database();
        Assert.Empty(await read.Set<ReceptionistActionIntent>().Where(x => x.CapabilityKey == ReceptionistCapabilities.CanonicalBooking).ToListAsync());
    }

    [Fact]
    public async Task ConcurrentBookingConfirmationMakesOneProviderAttemptAndKeepsTheOriginalIdentity()
    {
        await using var f = await Fixture.CreateAsync(bookingEnabled: true, qaOnly: false);
        var call = await f.CallAsync(); var intake = await f.ClaimAsync(call, Facts());
        await PersistBookingIntakeAsync(f, call, intake);
        var gateway = new BookingGatewayFake(); using var services = new ServiceCollection().AddSingleton<IReceptionistBookingGateway>(gateway).BuildServiceProvider();
        var authority = Booking(f, services); var lease = f.Leases[call.CallId];
        var option = Assert.Single(await authority.PrepareAsync(call, intake, "website_services", DateOnly.FromDateTime(f.Clock.Now.AddDays(1)), lease, default));
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => authority.CommitAsync(call, option.Handle, true, true, "same-consent", lease, default)));
        Assert.Equal(1, gateway.Attempts);
        var replay = await Booking(f, services).CommitAsync(call, option.Handle, true, true, "same-consent", lease, default);
        Assert.True(replay.Verified); Assert.False(replay.Synthetic); Assert.Equal(1, gateway.Attempts);
        await using var read = f.Database();
        var action = Assert.Single(await read.Set<ReceptionistActionIntent>().Where(x => x.CapabilityKey == ReceptionistCapabilities.CanonicalBooking).ToListAsync());
        Assert.Equal("booking-provider:v1:synthetic-original-event", action.ActualRecordReference);
        Assert.Equal("verified", action.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => authority.CommitAsync(call, option.Handle, true, true, "changed-consent", lease, default));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task UncertainBookingNeverRetriesTheProviderOrClaimsSuccess(bool failAfterStart, bool verificationFails)
    {
        await using var f = await Fixture.CreateAsync(bookingEnabled: true, qaOnly: false);
        var call = await f.CallAsync(); var intake = await f.ClaimAsync(call, Facts());
        await PersistBookingIntakeAsync(f, call, intake);
        var gateway = new BookingGatewayFake { FailAfterStart = failAfterStart, Verify = !verificationFails };
        using var services = new ServiceCollection().AddSingleton<IReceptionistBookingGateway>(gateway).BuildServiceProvider();
        var authority = Booking(f, services); var lease = f.Leases[call.CallId];
        var option = Assert.Single(await authority.PrepareAsync(call, intake, "website_services", DateOnly.FromDateTime(f.Clock.Now.AddDays(1)), lease, default));
        Assert.False((await authority.CommitAsync(call, option.Handle, true, true, "consent", lease, default)).Verified);
        Assert.False((await authority.CommitAsync(call, option.Handle, true, true, "consent", lease, default)).Verified);
        Assert.Equal(1, gateway.Attempts);
        await Assert.ThrowsAsync<InvalidOperationException>(() => authority.PrepareAsync(call, intake, "website_services", DateOnly.FromDateTime(f.Clock.Now.AddDays(2)), lease, default));
        await using var read = f.Database();
        var row = Assert.Single(await read.Set<ReceptionistActionIntent>().Where(x => x.CapabilityKey == ReceptionistCapabilities.CanonicalBooking).ToListAsync());
        Assert.Equal("uncertain", row.State);
        if (verificationFails) Assert.Equal("booking-provider:v1:synthetic-original-event", row.ActualRecordReference);
        else Assert.Null(row.ActualRecordReference);
    }

    [Fact]
    public async Task DefinitePreProviderRejectionPermitsOnlyANewlyOfferedConfirmedSlot()
    {
        await using var f = await Fixture.CreateAsync(bookingEnabled: true, qaOnly: false);
        var call = await f.CallAsync(); var intake = await f.ClaimAsync(call, Facts());
        await PersistBookingIntakeAsync(f, call, intake);
        var gateway = new BookingGatewayFake { RejectBeforeStart = true };
        using var services = new ServiceCollection().AddSingleton<IReceptionistBookingGateway>(gateway).BuildServiceProvider();
        var authority = Booking(f, services); var lease = f.Leases[call.CallId];
        var first = Assert.Single(await authority.PrepareAsync(call, intake, "website_services", DateOnly.FromDateTime(f.Clock.Now.AddDays(1)), lease, default));
        Assert.Equal("booking_rejected_before_attempt", (await authority.CommitAsync(call, first.Handle, true, true, "first-consent", lease, default)).State);
        Assert.Equal(0, gateway.Attempts);
        gateway.RejectBeforeStart = false;
        Assert.Equal("booking_rejected_before_attempt", (await authority.CommitAsync(call, first.Handle, true, true, "first-consent", lease, default)).State);
        Assert.Equal(0, gateway.Attempts);
        var next = Assert.Single(await authority.PrepareAsync(call, intake, "website_services", DateOnly.FromDateTime(f.Clock.Now.AddDays(2)), lease, default));
        Assert.True((await authority.CommitAsync(call, next.Handle, true, true, "new-consent", lease, default)).Verified);
        Assert.Equal(1, gateway.Attempts);
        await using var read = f.Database();
        var rows = await read.Set<ReceptionistActionIntent>().Where(x => x.CapabilityKey == ReceptionistCapabilities.CanonicalBooking).ToListAsync();
        Assert.Equal(2, rows.Count); Assert.Single(rows.Where(ReceptionistAdmissionService.IsZeroEffectBookingRejection));
        Assert.Single(rows.Where(x => x.State == "verified"));
    }

    private static async Task PersistBookingIntakeAsync(Fixture f, ReceptionistCallContext call, Guid intake)
    {
        await f.Inquiries.PersistConfirmedAsync(call, intake, Facts(), Receipt);
        var readback = (await f.Inquiries.ReadBackAsync(call.Owner, call.CallId, intake))!;
        await f.Admission.RecordActionOutcomeAsync(intake, "verified", readback.RecordReference, readback.VerificationReceipt);
    }

    private static ReceptionistBookingAuthority Booking(Fixture f, IServiceProvider services) =>
        new(f.Options, f.Admission, f.Inquiries, f.Protection, services, f.Clock);
    private sealed class BookingGatewayFake : IReceptionistBookingGateway
    {
        internal int Attempts, OriginalReads;
        internal bool RejectBeforeStart, FailAfterStart, Verify = true;
        internal ReceptionistCalendarSlot? Original;
        public Task<IReadOnlyList<ReceptionistCalendarSlot>> SlotsAsync(ReceptionistTrustedBookingTarget target, DateOnly date,
            int notice, int advance, CancellationToken ct)
        {
            var start = DateTime.SpecifyKind(date.ToDateTime(new TimeOnly(12, 0)), DateTimeKind.Utc);
            return Task.FromResult<IReadOnlyList<ReceptionistCalendarSlot>>([new(start, start.AddMinutes(target.Service.DurationMinutes), "synthetic-staff", target.CalendarTargetSha256)]);
        }
        public async Task CreateAsync(ReceptionistTrustedBookingCommit command, IReceptionistBookingExecutionFence fence, CancellationToken ct)
        {
            if (RejectBeforeStart || !await fence.TryStartProviderAsync(ct)) return;
            Interlocked.Increment(ref Attempts);
            if (FailAfterStart) throw new InvalidOperationException("Synthetic uncertain provider outcome.");
            Original = command.Slot;
            await fence.ProviderCreatedAsync(new("synthetic-original-event", command.Slot.TargetSha256), ct);
        }
        public Task<bool> VerifyAsync(ReceptionistTrustedBookingCommit command, ReceptionistProviderBookingIdentity identity, CancellationToken ct) => Task.FromResult(Verify);
        public Task<ReceptionistCalendarSlot?> ReadOriginalSlotAsync(ReceptionistTrustedBookingTarget target, string originalAppointmentId, CancellationToken ct)
        { OriginalReads++; return Task.FromResult(originalAppointmentId == "synthetic-original-event" ? Original : null); }
    }
}
