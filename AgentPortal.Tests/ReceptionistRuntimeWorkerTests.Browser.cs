using System;
using System.Linq;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Reception;
using Microsoft.EntityFrameworkCore;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public sealed partial class ReceptionistRuntimeWorkerTests
{
    [Fact]
    public async Task BrowserUnboundAttemptConsumesOneReservationAndCannotEnterAnyProviderWork()
    {
        await using var f = await Fixture.CreateAsync(browser: true);
        var binding = (await f.Admission.GetBrowserBindingAsync(MarketingOwnerScope.Founder, default))!;
        var creation = Guid.NewGuid(); var hash = new string('a', 64);
        var claim = await f.Admission.ReserveBrowserAsync(binding, creation, hash, false, default);
        Assert.True(claim.MayCreate); var id = claim.CallId!.Value;
        Assert.False((await f.Admission.ReserveBrowserAsync(binding, creation, hash, false, default)).MayCreate);
        Assert.Equal("creation_scope_mismatch", (await f.Admission.ReserveBrowserAsync(binding, creation, new string('b', 64), false, default)).Reason);
        Assert.Null((await f.CallAsync(id)).ProviderSessionId);
        Assert.Null(await f.Admission.GetCallContextAsync(id));
        Assert.False((await f.Admission.ClaimProviderAcceptAsync(id, "fixture")).MayExecute);
        Assert.Null(await f.Admission.AcquireSidebandLeaseAsync(id, "fixture", TimeSpan.FromSeconds(20)));
        Assert.Null(await f.Admission.AcquireObservationLeaseAsync(id, "fixture", TimeSpan.FromSeconds(20)));
        Assert.False((await f.Admission.ClaimProviderStopAsync(id, "fixture")).MayExecute);
        Assert.Empty(await f.Admission.FindPendingProviderAcceptsAsync(100));
        Assert.Empty(await f.Admission.FindUncertainProviderCallsAsync(100));
        Assert.Empty(await f.Admission.FindSidebandCandidatesAsync(100));
        f.Clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Empty(await f.Admission.FindStopRequestsAsync(100));
        Assert.Empty(await f.Admission.FindObservationCandidatesAsync(100, TimeSpan.FromHours(1)));
        await using (var host = await f.StartAsync()) { await EventuallyAsync(() => f.Clock.HasScheduledTimer); await host.StopAsync(); }
        Assert.Empty(f.Transport.Accepts); Assert.Empty(f.Transport.Hangups); Assert.Empty(f.Transport.Attaches);
        await using var db = f.Database();
        Assert.Single(await db.Set<ReceptionistCall>().ToArrayAsync());
        Assert.Empty(await db.Set<ReceptionistSessionDecision>().ToArrayAsync());
        var cost = Assert.Single(await db.Set<ReceptionistCostReservation>().ToArrayAsync());
        Assert.Equal(2m, cost.CashReserved); Assert.Equal(3m, cost.ConsumptionReserved);
        Assert.False(cost.CashFinal); Assert.False(cost.ConsumptionFinal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Admission.RecordProviderStateAsync(id, "accepted", "synthetic"));
    }

    [Fact]
    public async Task BrowserBindingPreservesOriginalIdentityAndReplaysSuccessfulCommitWithoutSipAccept()
    {
        await using var f = await Fixture.CreateAsync(browser: true);
        var binding = (await f.Admission.GetBrowserBindingAsync(MarketingOwnerScope.Founder, default))!;
        var creation = Guid.NewGuid();
        var claim = await f.Admission.ReserveBrowserAsync(binding, creation, new string('a', 64), false, default);
        var id = claim.CallId!.Value;
        var outcome = new OpenAiReceptionistBrowserOutcome(true, "live_browser", "v=0\r\n", 201, "synthetic-create");
        Assert.True(await f.Admission.BindBrowserOutcomeAsync(id, creation, outcome, true, default));
        Assert.True(await f.Admission.BindBrowserOutcomeAsync(id, creation, outcome, true, default));
        Assert.True(await f.Admission.ReleaseBrowserCleanupAsync(id, creation, claim.CleanupActionId!.Value, default));
        Assert.True(await f.Admission.ReleaseBrowserCleanupAsync(id, creation, claim.CleanupActionId!.Value, default));
        Assert.False((await f.Admission.ClaimProviderAcceptAsync(id, "fixture")).MayExecute);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Admission.BindBrowserOutcomeAsync(id, creation,
            outcome with { SessionId = "live_other" }, true, default));
        await using (var host = await f.StartAsync())
        {
            _ = await f.Transport.SocketAsync(0);
            Assert.Empty(f.Transport.Accepts); Assert.Single(f.Transport.Attaches);
            await host.StopAsync();
        }
        await using var db = f.Database();
        var decision = Assert.Single(await db.Set<ReceptionistSessionDecision>().ToArrayAsync());
        Assert.Equal("live_browser", decision.ProviderSessionId); Assert.Equal(id, decision.CallId);
        Assert.Single(await db.Set<ReceptionistCostReservation>().ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BrowserCancellationOnFirstOrRepeatedBindRequiresOriginalSessionShutdown(bool repeat)
    {
        await using var f = await Fixture.CreateAsync(browser: true);
        var binding = (await f.Admission.GetBrowserBindingAsync(MarketingOwnerScope.Founder, default))!;
        var creation = Guid.NewGuid();
        var claim = await f.Admission.ReserveBrowserAsync(binding, creation, new string('a', 64), false, default);
        var id = claim.CallId!.Value;
        var outcome = new OpenAiReceptionistBrowserOutcome(true, "live_cancelled", "v=0\r\n", 201, "synthetic-create");
        if (repeat) Assert.True(await f.Admission.BindBrowserOutcomeAsync(id, creation, outcome, true, default));
        Assert.False(await f.Admission.BindBrowserOutcomeAsync(id, creation, outcome, false, default));
        Assert.False(await f.Admission.BindBrowserOutcomeAsync(id, creation, outcome, true, default));
        Assert.False((await f.Admission.ClaimProviderStopAsync(id, "fixture")).MayExecute);
        Assert.True(await f.Admission.ReleaseBrowserCleanupAsync(id, creation, claim.CleanupActionId!.Value, default));
        Assert.Equal(id, Assert.Single(await f.Admission.FindStopRequestsAsync(100)).CallId);
        Assert.True((await f.Admission.ClaimProviderStopAsync(id, "fixture")).MayExecute);
        var call = await f.CallAsync(id);
        Assert.Equal("live_cancelled", call.ProviderSessionId); Assert.Null(call.ProviderClosedUtc);
        await using var db = f.Database();
        Assert.False((await db.Set<ReceptionistCostReservation>().SingleAsync()).CashFinal);
    }

    [Fact]
    public async Task BrowserUnknownOutcomeKeepsExposureAndCannotBeReclassifiedOrRoutedToAnotherOwner()
    {
        await using var f = await Fixture.CreateAsync(browser: true);
        var binding = (await f.Admission.GetBrowserBindingAsync(MarketingOwnerScope.Founder, default))!;
        Assert.False((await f.Admission.ReserveBrowserAsync(binding, Guid.NewGuid(), new string('a', 64), true, default)).MayCreate);
        Assert.False((await f.Admission.ReserveBrowserAsync(binding with { Owner = MarketingOwnerScope.Business(Guid.NewGuid()) },
            Guid.NewGuid(), new string('a', 64), false, default)).MayCreate);
        var creation = Guid.NewGuid();
        var claim = await f.Admission.ReserveBrowserAsync(binding, creation, new string('a', 64), false, default);
        var id = claim.CallId!.Value;
        Assert.False(await f.Admission.BindBrowserOutcomeAsync(id, creation,
            new(false, null, null, null, "synthetic-unknown"), true, default));
        await using var db = f.Database();
        var call = await db.Set<ReceptionistCall>().SingleAsync();
        Assert.Null(call.ProviderSessionId); Assert.Equal("uncertain", call.State);
        Assert.Equal("synthetic-unknown", call.ProviderStateReceipt);
        Assert.False((await db.Set<ReceptionistCostReservation>().SingleAsync()).CashFinal);
        call.BrowserOfferSha256 = null;
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        call.BrowserOfferSha256 = new string('a', 64); call.BrowserCreationId = null;
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        call.BrowserCreationId = creation; call.State = "accepted";
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
    [Fact]
    public async Task BrowserCleanupExpiryNeedsEvidenceNewerThanTheEntireCreatorWindow()
    {
        await using var f = await Fixture.CreateAsync(browser: true);
        var binding = (await f.Admission.GetBrowserBindingAsync(MarketingOwnerScope.Founder, default))!;
        var creation = Guid.NewGuid();
        var claim = await f.Admission.ReserveBrowserAsync(binding, creation, new string('a', 64), false, default);
        var id = claim.CallId!.Value;
        Assert.False(await f.Admission.BindBrowserOutcomeAsync(id, creation,
            new(false, "live_cleanup", null, 201, "synthetic-invalid-answer"), false, default));
        f.Clock.Advance(TimeSpan.FromSeconds(91));
        Assert.False((await f.Admission.ClaimProviderStopAsync(id, "fixture")).MayExecute);
        Assert.False((await f.Admission.ClaimProviderStopAsync(id, "fixture", new("synthetic-stale-active",
            claim.CleanupExpiresUtc!.Value.AddSeconds(-1), true))).MayExecute);
        Assert.True((await f.Admission.ClaimProviderStopAsync(id, "fixture", new("synthetic-fresh-active",
            f.Clock.GetUtcNow().UtcDateTime, true))).MayExecute);
    }

}
