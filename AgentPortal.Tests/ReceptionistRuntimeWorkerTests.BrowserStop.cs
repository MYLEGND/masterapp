using System;
using System.Data.Common;
using System.Linq;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Reception;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public sealed partial class ReceptionistRuntimeWorkerTests
{
    private static async Task<Guid> BrowserCallAsync(Fixture f, string session)
    {
        var binding = (await f.Admission.GetBrowserBindingAsync(MarketingOwnerScope.Founder, default))!;
        var creation = Guid.NewGuid();
        var claim = await f.Admission.ReserveBrowserAsync(binding, creation, new string('a', 64), false, default);
        var id = claim.CallId!.Value;
        Assert.True(await f.Admission.BindBrowserOutcomeAsync(id, creation,
            new(true, session, "v=0\r\n", 201, "synthetic-create"), true, default));
        Assert.True(await f.Admission.ReleaseBrowserCleanupAsync(id, creation, claim.CleanupActionId!.Value, default));
        return id;
    }

    private static bool IsClose(JsonElement e) => e.GetProperty("type").GetString() == "session.close";

    [Fact]
    public void BrowserCloseCommandHasNoStartOrPrivatePayload()
    {
        using var json = JsonDocument.Parse(ReceptionistSidebandProtocol.Close("stop_fixture"));
        Assert.Equal("session.close", json.RootElement.GetProperty("type").GetString());
        Assert.Equal("stop_fixture", json.RootElement.GetProperty("event_id").GetString());
        Assert.Equal(2, json.RootElement.EnumerateObject().Count());
        Assert.Throws<ArgumentException>(() => ReceptionistSidebandProtocol.Close("bad identity"));
    }

    [Fact]
    public async Task BrowserSilentDeadlineClosesExistingSidebandAndWaitsForActualFinalUsage()
    {
        await using var f = await Fixture.CreateAsync(browser: true);
        var id = await BrowserCallAsync(f, "live_deadline");
        await using var host = await f.StartAsync();
        var socket = await f.Transport.SocketAsync(0);
        await EventuallyAsync(() => socket.Sent.Any());
        f.Clock.Advance(TimeSpan.FromSeconds(121)); // No provider event wakes the stop path.
        await EventuallyAsync(() => socket.Sent.Any(IsClose));
        var closeEventId = Assert.Single(socket.Sent.Where(IsClose)).GetProperty("event_id").GetString()!;
        Assert.StartsWith("stop_", closeEventId);
        var closeId = Guid.ParseExact(closeEventId["stop_".Length..], "N");
        // Send visibility precedes the worker's committed observation receipt.
        // Require that exact action to persist uncertainty before injecting closure.
        await EventuallyAsync(async () =>
        {
            await using var read = f.Database();
            return await read.Set<ReceptionistActionIntent>().AnyAsync(x => x.Id == closeId && x.CallId == id &&
                x.CapabilityKey == "__provider_stop" && x.State == "uncertain" &&
                x.VerificationReceipt == "openai_sideband:session.close:sent");
        });
        Assert.Single(socket.Sent.Where(IsClose)); Assert.Empty(f.Transport.Hangups); Assert.Empty(f.Transport.Accepts);
        Assert.Equal(WebSocketState.Open, socket.State);
        Assert.Null((await f.CallAsync(id)).ProviderClosedUtc);
        await using (var db = f.Database())
        {
            Assert.False((await db.Set<ReceptionistCostReservation>().SingleAsync()).CashFinal);
            var stop = await db.Set<ReceptionistActionIntent>().SingleAsync(x =>
                x.CallId == id && x.CapabilityKey == "__provider_stop" && x.State != "rejected");
            Assert.Equal(closeId, stop.Id); Assert.Equal("uncertain", stop.State);
            Assert.Equal("openai_sideband:session.close:sent", stop.VerificationReceipt);
        }
        socket.Queue(Closed("live_deadline", "closed-deadline", 19.25m));
        await EventuallyAsync(async () => (await f.CallAsync(id)).ProviderClosedUtc is not null);
        await host.StopAsync();
        await AssertFinalUsageWithMoneyStillReservedAsync(f, id, 20);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task BrowserStopDoesNotDependOnRuntimeEnablementOrValidBusinessConfiguration(bool enabled, bool brokenBusiness)
    {
        await using var f = await Fixture.CreateAsync(enabled: enabled, browser: true,
            configuration: brokenBusiness ? Configuration() with { ServiceDefinitionsJson = "[]" } : null);
        var id = await BrowserCallAsync(f, "live_no_business");
        await using var host = await f.StartAsync();
        var socket = await f.Transport.SocketAsync(0);
        await EventuallyAsync(() => socket.Sent.Any(IsClose));
        Assert.True((await f.CallAsync(id)).SidebandObservationOnly);
        Assert.All(socket.Sent, e => Assert.True(IsClose(e)));
        Assert.Empty(f.Transport.Hangups); Assert.Empty(f.Transport.Accepts);
        socket.Queue(Closed("live_no_business", "closed-no-business", 4m));
        await EventuallyAsync(async () => (await f.CallAsync(id)).ProviderClosedUtc is not null);
        await host.StopAsync(); await AssertFinalUsageWithMoneyStillReservedAsync(f, id, 4);
    }

    [Fact]
    public async Task BrowserFailedAttachKeepsStopPendingWithoutSipFallbackOrConsumingAnUnsentClose()
    {
        await using var f = await Fixture.CreateAsync(enabled: false, browser: true);
        var id = await BrowserCallAsync(f, "live_attach_failure"); f.Transport.FailAttach = true;
        await using (var host = await f.StartAsync())
        {
            await EventuallyAsync(() => f.Transport.Attaches.Count == 1);
            await host.StopAsync();
        }
        Assert.Empty(f.Transport.Hangups);
        Assert.Equal(id, Assert.Single(await f.Admission.FindStopRequestsAsync(100)).CallId);
        Assert.Null((await f.CallAsync(id)).ProviderClosedUtc);
        f.Transport.FailAttach = false; f.Clock.Advance(TimeSpan.FromSeconds(21));
        await using var restarted = await f.StartAsync();
        var socket = await f.Transport.SocketAsync(0);
        await EventuallyAsync(() => socket.Sent.Any(IsClose));
        Assert.Single(socket.Sent); Assert.Empty(f.Transport.Hangups);
        await restarted.StopAsync();
    }

    [Fact]
    public async Task BrowserLostObservationOwnershipCannotSendClose()
    {
        var rejections = new BrowserLeaseRejectionInterceptor();
        await using var f = await Fixture.CreateAsync(browser: true, interceptor: rejections);
        var id = await BrowserCallAsync(f, "live_lease_lost");
        await using var host = await f.StartAsync(); var socket = await f.Transport.SocketAsync(0);
        // The first heartbeat rotates the concurrency token. Wait for its completed
        // iteration before injecting the adversarial owner, not merely its first send.
        await EventuallyAsync(() => socket.Sent.Any() && f.Clock.HasScheduledDelay(TimeSpan.FromSeconds(2)));
        Assert.Equal(WebSocketState.Open, socket.State);
        var original = await f.CallAsync(id);
        var oldToken = original.SidebandLeaseToken!.Value;
        Assert.True(await f.Admission.ValidateSidebandLeaseAsync(id, oldToken));
        var replacementToken = Guid.NewGuid();
        var replacementRevision = Guid.NewGuid();
        var replacementExpiry = f.Clock.GetUtcNow().UtcDateTime.AddSeconds(20);
        await using (var db = f.Database())
        {
            // One atomic fixture takeover, not tracked SELECT/SaveChanges against a
            // heartbeat's stale Revision. Keep the real model's concurrency protection.
            Assert.Equal(1, await db.Set<ReceptionistCall>()
                .Where(x => x.Id == id && x.SidebandLeaseToken == oldToken && x.ProviderClosedUtc == null)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.SidebandWorkerId, "new-owner")
                    .SetProperty(x => x.SidebandLeaseToken, replacementToken)
                    .SetProperty(x => x.SidebandLeaseExpiresUtc, replacementExpiry)
                    .SetProperty(x => x.SidebandObservationOnly, true)
                    .SetProperty(x => x.SidebandFence, x => x.SidebandFence + 1)
                    .SetProperty(x => x.Revision, replacementRevision)));
        }
        Assert.False(await f.Admission.ValidateSidebandLeaseAsync(id, oldToken));
        Assert.False(await f.Admission.ValidateObservationLeaseAsync(id, oldToken));
        Assert.True(await f.Admission.ValidateObservationLeaseAsync(id, replacementToken));
        await f.Admission.RequestRuntimeStopAsync(id, "browser_ended", default);
        rejections.Arm(id, replacementToken, socket);
        f.Clock.Advance(TimeSpan.FromSeconds(2));
        // Renewal and failed conversion each commit a no-op ownership rejection.
        // Both must complete while this socket is open; a caught heartbeat exception
        // or teardown alone cannot satisfy the no-close/aborted assertions.
        await EventuallyAsync(() => rejections.CommitsWhileOpen == 2);
        await EventuallyAsync(() => socket.State == WebSocketState.Aborted);
        Assert.Equal(2, rejections.CommitsWhileOpen);
        Assert.DoesNotContain(socket.Sent, IsClose); Assert.Empty(f.Transport.Hangups);
        var stolen = await f.CallAsync(id);
        Assert.Equal("new-owner", stolen.SidebandWorkerId);
        Assert.Equal(replacementToken, stolen.SidebandLeaseToken);
        Assert.Equal(original.SidebandFence + 1, stolen.SidebandFence);
        Assert.True(stolen.SidebandObservationOnly); Assert.Null(stolen.ProviderClosedUtc);

        Assert.True(await f.Admission.ValidateObservationLeaseAsync(id, replacementToken));
        await using (var db = f.Database())
        {
            var cost = await db.Set<ReceptionistCostReservation>().SingleAsync(x => x.CallId == id);
            Assert.False(cost.CashFinal); Assert.False(cost.ConsumptionFinal);
        }
        await host.StopAsync();
    }

    [Fact]
    public async Task BrowserShutdownDuringCloseRetainsUncertainClaimAndRestartDoesNotReplayIt()
    {
        await using var f = await Fixture.CreateAsync(enabled: false, browser: true);
        var id = await BrowserCallAsync(f, "live_cancel_close");
        var closeStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Transport.ConfigureSocket = socket => socket.Sending = async (e, ct) =>
        { if (IsClose(e)) { closeStarted.TrySetResult(true); await Task.Delay(Timeout.Infinite, ct); } };
        await using (var first = await f.StartAsync())
        {
            await closeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)); await first.StopAsync();
        }
        Assert.Null((await f.CallAsync(id)).ProviderClosedUtc); Assert.False((await f.CallAsync(id)).VoiceUsageFinal);
        Assert.False((await f.Admission.ClaimProviderStopAsync(id, "retry-worker")).MayExecute);
        f.Transport.ConfigureSocket = null; f.Clock.Advance(TimeSpan.FromSeconds(21));
        await using var second = await f.StartAsync(); var recovered = await f.Transport.SocketAsync(1);
        recovered.Queue(new { type = "session.started", event_id = "buffered", session = new { id = "live_cancel_close", model = "gpt-live-1" } });
        recovered.Queue(Closed("live_cancel_close", "closed-after-cancel", 8m));
        await EventuallyAsync(async () => (await f.CallAsync(id)).ProviderClosedUtc is not null);
        Assert.DoesNotContain(recovered.Sent, IsClose); Assert.Empty(f.Transport.Hangups);
        await second.StopAsync(); await AssertFinalUsageWithMoneyStillReservedAsync(f, id, 8);
    }
    [Fact]
    public async Task BrowserCloseIsSerializedBehindAnExistingSidebandSend()
    {
        await using var f = await Fixture.CreateAsync(browser: true);
        var id = await BrowserCallAsync(f, "live_serialized");
        var sending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstSend = 0;
        f.Transport.ConfigureSocket = socket => socket.Sending = async (_, ct) =>
        {
            if (Interlocked.Increment(ref firstSend) == 1)
            { sending.TrySetResult(true); await release.Task.WaitAsync(ct); }
        };
        await using var host = await f.StartAsync(); var socket = await f.Transport.SocketAsync(0);
        await sending.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await f.Admission.RequestRuntimeStopAsync(id, "browser_ended", default);
        Assert.DoesNotContain(socket.Sent, IsClose);
        release.TrySetResult(true);
        await EventuallyAsync(() => socket.Sent.Any(IsClose));
        // ScriptedSocket asserts exactly one simultaneous send for every message.
        Assert.Single(socket.Sent.Where(IsClose)); Assert.Empty(f.Transport.Hangups);
        await host.StopAsync();
        Assert.Null((await f.CallAsync(id)).ProviderClosedUtc);
    }

    [Fact]
    public async Task BrowserClosureWithoutUsageDoesNotInventFinalSecondsOrReleaseMoney()
    {
        await using var f = await Fixture.CreateAsync(enabled: false, browser: true);
        var id = await BrowserCallAsync(f, "live_no_usage");
        await using var host = await f.StartAsync(); var socket = await f.Transport.SocketAsync(0);
        await EventuallyAsync(() => socket.Sent.Any(IsClose));
        socket.Queue(new { type = "session.closed", event_id = "closed-no-usage", session = new { id = "live_no_usage" } });
        await EventuallyAsync(async () => (await f.CallAsync(id)).ProviderClosedUtc is not null);
        await host.StopAsync();
        Assert.False((await f.CallAsync(id)).VoiceUsageFinal);
        await using var db = f.Database(); var cost = await db.Set<ReceptionistCostReservation>().SingleAsync();
        Assert.False(cost.CashFinal); Assert.False(cost.ConsumptionFinal);
    }

    [Fact]
    public async Task BrowserExpiredOriginalCleanupRemainsHeldAndCannotBecomeAnUnownedStop()
    {
        await using var f = await Fixture.CreateAsync(browser: true);
        f.Transport.BrowserCreate = _ =>
        {
            f.Clock.Advance(TimeSpan.FromSeconds(91));
            return Task.FromResult(new OpenAiReceptionistBrowserOutcome(false, "live_expired_cleanup", null, 201, "synthetic-invalid-answer"));
        };
        using var scope = f.CreateScope(); var controller = BrowserController(scope.ServiceProvider);
        var grant = Field(await controller.Grant(new("/contact"), default), "grant");
        Assert.IsNotType<Microsoft.AspNetCore.Mvc.OkObjectResult>(await controller.Start(new(grant, "v=0\r\n"), default));
        await using var db = f.Database(); var call = await db.Set<ReceptionistCall>().SingleAsync();
        Assert.Equal("live_expired_cleanup", call.ProviderSessionId);
        var cleanup = await db.Set<ReceptionistActionIntent>().SingleAsync(x => x.CapabilityKey == "__provider_stop");
        Assert.Equal("pending", cleanup.State); Assert.Null(cleanup.VerificationReceipt);
        Assert.False((await f.Admission.ClaimProviderStopAsync(call.Id, "unauthorized-retry")).MayExecute);
        f.SetRuntimeEnabled(false); await using var host = await f.StartAsync();
        var socket = await f.Transport.SocketAsync(0); f.Clock.Advance(TimeSpan.FromSeconds(2));
        await host.StopAsync(); Assert.DoesNotContain(socket.Sent, IsClose); Assert.Empty(f.Transport.Hangups);
        Assert.False((await db.Set<ReceptionistCostReservation>().SingleAsync()).CashFinal);
        Assert.Null((await f.CallAsync(call.Id)).ProviderClosedUtc);
    }

    [Fact]
    public async Task BrowserLeaseExpiryAfterStopClaimCannotSendOrReplayClose()
    {
        var interceptor = new BrowserStopClaimInterceptor();
        await using var f = await Fixture.CreateAsync(enabled: false, browser: true, interceptor: interceptor);
        var id = await BrowserCallAsync(f, "live_expired_claim");
        interceptor.AfterClaim = () => f.Clock.Advance(TimeSpan.FromSeconds(21));
        await using var host = await f.StartAsync(); var socket = await f.Transport.SocketAsync(0);
        // Only this call's heartbeat schedules a two-second delay. It does so after
        // StopAsync returns; the first iteration has no earlier heartbeat timer.
        // Wait for that continuation, not host cancellation, to establish the expiry
        // rejection. The one-second global discovery timer cannot satisfy this barrier.
        await EventuallyAsync(() => interceptor.Triggered && f.Clock.HasScheduledDelay(TimeSpan.FromSeconds(2)));
        Assert.Equal(WebSocketState.Open, socket.State);
        Assert.DoesNotContain(socket.Sent, IsClose); Assert.Empty(f.Transport.Hangups);
        Assert.True((await f.CallAsync(id)).SidebandLeaseExpiresUtc <= f.Clock.GetUtcNow().UtcDateTime);
        Assert.False((await f.Admission.ClaimProviderStopAsync(id, "retry-worker")).MayExecute);
        await using var db = f.Database();
        Assert.Equal("pending", (await db.Set<ReceptionistActionIntent>().SingleAsync(x =>
            x.CapabilityKey == "__provider_stop" && x.ConfirmationReceipt != "browser-create-cleanup")).State);
        Assert.False((await db.Set<ReceptionistCostReservation>().SingleAsync()).CashFinal);
        await host.StopAsync(); // Teardown occurs only after every expiry assertion.
    }

    [Fact]
    public async Task BrowserWrongSessionClosureCannotFinalizeTheStoppingCall()
    {
        await using var f = await Fixture.CreateAsync(enabled: false, browser: true);
        var id = await BrowserCallAsync(f, "live_correct_session");
        await using var host = await f.StartAsync(); var socket = await f.Transport.SocketAsync(0);
        await EventuallyAsync(() => socket.Sent.Any(IsClose));
        socket.Queue(Closed("live_wrong_session", "wrong-session-closed", 99m));
        socket.Queue(new { type = "session.usage.updated", event_id = "after-wrong-closure", usage = new { seconds = 3 } });
        await EventuallyAsync(async () => (await f.CallAsync(id)).ObservedVoiceSeconds == 3);
        Assert.Null((await f.CallAsync(id)).ProviderClosedUtc); Assert.False((await f.CallAsync(id)).VoiceUsageFinal);
        socket.Queue(Closed("live_correct_session", "matching-session-closed", 4m));
        await EventuallyAsync(async () => (await f.CallAsync(id)).ProviderClosedUtc is not null);
        await host.StopAsync(); await AssertFinalUsageWithMoneyStillReservedAsync(f, id, 4);
        Assert.Single(socket.Sent.Where(IsClose));
    }

    [Fact]
    public async Task BrowserHealthyHeartbeatDoesNotDemoteBusinessLeaseOrClaimAStop()
    {
        await using var f = await Fixture.CreateAsync(browser: true);
        var id = await BrowserCallAsync(f, "live_still_active");
        await using var host = await f.StartAsync(); var socket = await f.Transport.SocketAsync(0);
        await EventuallyAsync(() => socket.Sent.Any());
        var original = await f.CallAsync(id); f.Clock.Advance(TimeSpan.FromSeconds(2));
        await EventuallyAsync(async () => (await f.CallAsync(id)).SidebandLeaseExpiresUtc > original.SidebandLeaseExpiresUtc);
        var active = await f.CallAsync(id); Assert.False(active.SidebandObservationOnly);
        Assert.True(await f.Admission.ValidateSidebandLeaseAsync(id, active.SidebandLeaseToken!.Value));
        Assert.DoesNotContain(socket.Sent, IsClose); Assert.Empty(f.Transport.Hangups);
        await host.StopAsync();
        await using var db = f.Database();
        Assert.DoesNotContain(await db.Set<ReceptionistActionIntent>().ToListAsync(), x =>
            x.CapabilityKey == "__provider_stop" && x.ConfirmationReceipt != "browser-create-cleanup");
    }

    private sealed class BrowserLeaseRejectionInterceptor : DbTransactionInterceptor
    {
        private Guid callId;
        private Guid replacementToken;
        private ScriptedSocket? socket;
        private int armed;
        private int commitsWhileOpen;
        internal int CommitsWhileOpen => Volatile.Read(ref commitsWhileOpen);
        internal void Arm(Guid id, Guid token, ScriptedSocket currentSocket)
        {
            callId = id; replacementToken = token; socket = currentSocket;
            Volatile.Write(ref armed, 1);
        }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref armed) == 1 && socket?.State == WebSocketState.Open &&
                eventData.Context?.ChangeTracker.Entries<ReceptionistCall>().Any(x =>
                    x.State == EntityState.Unchanged && x.Entity.Id == callId &&
                    x.Entity.SidebandWorkerId == "new-owner" && x.Entity.SidebandLeaseToken == replacementToken &&
                    x.Entity.SidebandObservationOnly && x.Entity.ProviderClosedUtc is null) == true)
                Interlocked.Increment(ref commitsWhileOpen);
            return Task.CompletedTask;
        }
    }

    private sealed class BrowserStopClaimInterceptor : SaveChangesInterceptor
    {
        internal Action? AfterClaim;
        internal bool Triggered;
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (!Triggered && AfterClaim is not null && eventData.Context?.ChangeTracker.Entries<ReceptionistActionIntent>().Any(x =>
                x.Entity.CapabilityKey == "__provider_stop" && x.Entity.ConfirmationReceipt != "browser-create-cleanup") == true)
            { Triggered = true; AfterClaim(); }
            return ValueTask.FromResult(result);
        }
    }

}
