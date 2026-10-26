using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Analytics;
using Infrastructure.Bookings;
using Infrastructure.Data;
using Infrastructure.Reception;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public sealed partial class ReceptionistInquiryTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReviewedBookingRecoveryPackagePreservesDistinctOutcomeAndReplayWithoutAnotherProviderAttempt(bool pending)
    {
        await using var f = await Fixture.CreateAsync(bookingEnabled: true, qaOnly: false);
        var call = await f.CallAsync(); var intake = await f.ClaimAsync(call, Facts());
        await PersistBookingIntakeAsync(f, call, intake);
        var gateway = new BookingGatewayFake { Verify = false };
        using var services = new ServiceCollection().AddSingleton<IReceptionistBookingGateway>(gateway)
            .AddSingleton<IReceptionistApplicationBooking>(sp => Booking(f, sp)).BuildServiceProvider();
        if (pending)
            await f.Admission.ClaimActionAsync(call.CallId, "caller-booking-1", ReceptionistCapabilities.CanonicalBooking,
                new string('a', 64), "synthetic-consent", f.Leases[call.CallId]);
        else
        {
            var booking = services.GetRequiredService<IReceptionistApplicationBooking>();
            var slot = Assert.Single(await booking.PrepareAsync(call, intake, "website_services", DateOnly.FromDateTime(f.Clock.Now.AddDays(1)), f.Leases[call.CallId], default));
            Assert.False((await booking.CommitAsync(call, slot.Handle, true, true, "synthetic-consent", f.Leases[call.CallId], default)).Verified);
        }
        f.Clock.Now = f.Clock.Now.AddMinutes(10);
        await using var db = f.Database();
        var connection = await db.MarketingConnections.SingleAsync(x => x.OwnerKey == f.Owner.Key);
        connection.ReceptionistAdmissionEnabled = false; await db.SaveChangesAsync();
        var metadata = await f.Admission.ReadBookingRecoveryPreparationAsync(f.Owner.Key, call.ConfigurationId, call.CallId, default);
        var candidate = Assert.Single(metadata.Actions);
        using var protector = new MarketingCredentialProtector(f.Protection);
        var host = new ConfigurationBuilder().AddInMemoryCollection().Build();
        const string actor = "11111111-1111-1111-1111-111111111111";
        host["FOUNDER_OID"] = actor;
        var package = RecoveryPackage(f, call, metadata, candidate, actor);
        var json = JsonSerializer.Serialize(package, ReceptionistConfigurationCodec.Json); var hash = ReceptionistConfigurationCodec.Hash(json);
        host["Receptionist:OperatorPackages:synthetic_recovery:Json"] = json;
        host["Receptionist:OperatorPackages:synthetic_recovery:Sha256"] = hash;
        var http = new RecoveryHttpContext { HttpContext = new DefaultHttpContext
            { User = new ClaimsPrincipal(new ClaimsIdentity([new("oid", actor)], "test")) } };
        var operation = new ReceptionistOperatorService(new MarketingConnectionStore(db, protector), f.Admission,
            host, protector, http, f.Clock, services);
        var plan = await operation.PlanAsync("synthetic_recovery", hash);
        Assert.True(plan.CanApply, string.Join(";", plan.Blockers));
        Assert.Contains(plan.Facts, x => x.Label == "Booking outcome" && x.Value.StartsWith("Unresolved"));
        gateway.Verify = true;
        var request = new ReceptionistOperatorApplyRequest("synthetic_recovery", hash, plan.ExpectedStateToken!);
        var receipt = await operation.ApplyAsync(request);
        Assert.True(receipt.ReadBackVerified); Assert.True(receipt.CommitKnown);
        var expected = pending ? "No appointment created; rejected before provider attempt" : "Appointment independently verified";
        Assert.Contains(receipt.Facts, x => x.Label == "Booking outcome" && x.Value == expected);
        var reads = gateway.OriginalReads;
        var replay = await operation.ApplyAsync(request);
        Assert.True(replay.ReadBackVerified); Assert.Equal("already_satisfied", replay.Outcome);
        Assert.Contains(replay.Facts, x => x.Label == "Booking outcome" && x.Value == expected);
        Assert.Equal(reads, gateway.OriginalReads); Assert.Equal(pending ? 0 : 1, gateway.Attempts);
        Assert.False((await db.MarketingConnections.AsNoTracking().SingleAsync(x => x.Id == connection.Id)).ReceptionistAdmissionEnabled);
        Assert.DoesNotContain(Facts().Email!, JsonSerializer.Serialize(new { plan, receipt, replay }));
    }

    [Fact]
    public async Task ReviewedBookingRecoveryRejectsChangedActionRevisionBeforeProviderRead()
    {
        await using var f = await Fixture.CreateAsync(bookingEnabled: true, qaOnly: false);
        var call = await f.CallAsync(); await f.ClaimAsync(call, Facts());
        await f.Admission.ClaimActionAsync(call.CallId, "caller-booking-1", ReceptionistCapabilities.CanonicalBooking,
            new string('a', 64), "synthetic-consent", f.Leases[call.CallId]);
        var metadata = await f.Admission.ReadBookingRecoveryPreparationAsync(f.Owner.Key, call.ConfigurationId, call.CallId, default);
        var candidate = Assert.Single(metadata.Actions);
        await using var db = f.Database();
        using var protector = new MarketingCredentialProtector(f.Protection);
        var host = new ConfigurationBuilder().AddInMemoryCollection().Build();
        const string actor = "11111111-1111-1111-1111-111111111111"; host["FOUNDER_OID"] = actor;
        var package = RecoveryPackage(f, call, metadata, candidate, actor);
        var json = JsonSerializer.Serialize(package, ReceptionistConfigurationCodec.Json); var hash = ReceptionistConfigurationCodec.Hash(json);
        host["Receptionist:OperatorPackages:synthetic_recovery:Json"] = json;
        host["Receptionist:OperatorPackages:synthetic_recovery:Sha256"] = hash;
        var http = new RecoveryHttpContext { HttpContext = new DefaultHttpContext
            { User = new ClaimsPrincipal(new ClaimsIdentity([new("oid", actor)], "test")) } };
        // No booking adapter exists: a stale preview must fail before resolving one.
        using var services = new ServiceCollection().BuildServiceProvider();
        var operation = new ReceptionistOperatorService(new MarketingConnectionStore(db, protector), f.Admission, host, protector, http, f.Clock, services);
        var plan = await operation.PlanAsync("synthetic_recovery", hash); Assert.True(plan.CanApply);
        (await db.Set<ReceptionistActionIntent>().SingleAsync(x => x.Id == candidate.ActionId)).Revision = Guid.NewGuid();
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => operation.ApplyAsync(new("synthetic_recovery", hash, plan.ExpectedStateToken!)));
    }

    private static ReceptionistOperatorPackage RecoveryPackage(Fixture f, ReceptionistCallContext call,
        ReceptionistBookingRecoveryPreparation metadata, ReceptionistBookingRecoveryCandidate action, string actor) => new()
    {
        Version = 1, Label = "Synthetic original booking recovery", Operation = "reconcile_booking_action",
        ReviewedByFounderOid = actor, ReviewedUtc = f.Clock.Now, ExpiresUtc = f.Clock.Now.AddHours(1),
        MarketingConnectionId = metadata.MarketingConnectionId, ExpectedOwnerKey = f.Owner.Key,
        Provider = call.Provider, ProviderProjectKey = call.ProviderProjectKey, ConfigurationId = call.ConfigurationId,
        ConfigurationSha256 = metadata.ConfigurationSha256, ExpectedConnectionRevision = metadata.ConnectionRevision,
        CallId = call.CallId, ExpectedCallRevision = action.CallRevision, BookingActionId = action.ActionId,
        ExpectedBookingActionRevision = action.ActionRevision,
        ExpectedAccountRevisions = metadata.Accounts.ToDictionary(x => x.Id, x => x.Revision),
        FundingScopes = metadata.Accounts.Where(x => x.Kind == "funding").ToDictionary(x => x.Id, x => x.ScopeKey),
        ReceiptReference = "synthetic-reviewed-recovery", Evidence = [new("reconcile_booking_action", "synthetic-reviewed-recovery",
            ReceptionistConfigurationCodec.Hash("synthetic-reviewed-recovery"), f.Clock.Now)]
    };
    private sealed class RecoveryHttpContext : IHttpContextAccessor { public HttpContext? HttpContext { get; set; } }
}
