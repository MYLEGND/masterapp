using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Reception;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentPortal.Tests;

public sealed partial class ReceptionistRuntimeWorkerTests
{
    private static ReceptionistBrowserController BrowserController(IServiceProvider provider)
    {
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Scheme = "https"; context.Request.Host = new("portal.mylegnd.com");
        context.Request.Method = "POST"; context.Request.Headers.Origin = "https://www.mylegnd.com";
        return new(provider) { ControllerContext = new() { HttpContext = context } };
    }
    private static string Field(IActionResult result, string name) =>
        JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value).GetProperty(name).GetString()!;

    [Theory]
    [InlineData("tampered")]
    [InlineData("expired")]
    [InlineData("cross_origin")]
    [InlineData("paused")]
    public async Task BrowserGrantCannotOutliveItsScopeOrOwnerPermission(string change)
    {
        await using var f = await Fixture.CreateAsync(browser: true);
        using var scope = f.CreateScope(); var controller = BrowserController(scope.ServiceProvider);
        var grant = Field(await controller.Grant(new("/contact"), default), "grant");
        if (change == "tampered") grant = "invalid-" + grant;
        if (change == "expired") f.Clock.Advance(TimeSpan.FromSeconds(91));
        if (change == "cross_origin") controller.Request.Headers.Origin = "https://other.example.test";
        if (change == "paused") await f.DisableOwnerAsync();
        Assert.IsNotType<OkObjectResult>(await controller.Start(new(grant, "v=0\r\n"), default));
        Assert.Equal(0, f.Transport.BrowserCreates);
        await using var db = f.Database(); Assert.Empty(await db.Set<ReceptionistCall>().ToArrayAsync());
    }

    [Fact]
    public async Task BrowserControllerProjectsOpaqueHandlesAndConsumesGrantOnce()
    {
        await using var f = await Fixture.CreateAsync(browser: true);
        using var scope = f.CreateScope(); var controller = BrowserController(scope.ServiceProvider);
        var grant = Field(await controller.Grant(new("/contact"), default), "grant");
        var result = await controller.Start(new(grant, "v=0\r\n"), default);
        var handle = Field(result, "handle");
        var json = JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(result).Value);
        Assert.DoesNotContain("live_browser_controller", json);
        Assert.DoesNotContain("synthetic-local-test-key", json);
        Assert.IsNotType<OkObjectResult>(await controller.Start(new(grant, "v=0\r\n"), default));
        Assert.Equal(1, f.Transport.BrowserCreates);
        Assert.Equal("connecting", Field(await controller.Status(new(handle), default), "state"));
        controller.Request.Headers.Origin = "https://other.example.test";
        Assert.IsType<BadRequestResult>(await controller.Status(new(handle), default));
        Assert.IsType<NotFoundResult>(await controller.End(new(handle), default));
        controller.Request.Headers.Origin = "https://www.mylegnd.com";
        Assert.Equal("ending", Field(await controller.End(new(handle), default), "state"));
        Assert.Equal("ending", Field(await controller.Status(new(handle), default), "state"));
        await using var db = f.Database();
        Assert.Single(await db.Set<ReceptionistCall>().ToArrayAsync());
        Assert.Single(await db.Set<ReceptionistCostReservation>().ToArrayAsync());
        Assert.Empty(f.Transport.Hangups); // The canonical worker owns the released cleanup.
    }

    [Fact]
    public async Task BrowserAbortDuringBindingUsesItsSinglePreclaimedCleanupAndRetainsCosts()
    {
        using var browserAbort = new CancellationTokenSource();
        var interceptor = new BrowserBindInterceptor(afterSave: browserAbort.Cancel);
        await using var f = await Fixture.CreateAsync(browser: true, interceptor: interceptor);
        using var scope = f.CreateScope(); var controller = BrowserController(scope.ServiceProvider);
        var grant = Field(await controller.Grant(new("/contact"), default), "grant");
        Assert.IsNotType<OkObjectResult>(await controller.Start(new(grant, "v=0\r\n"), browserAbort.Token));
        Assert.Equal(1, f.Transport.BrowserCreates);
        Assert.Empty(f.Transport.Hangups); Assert.Empty(f.Transport.Attaches);
        await using var db = f.Database();
        var call = await db.Set<ReceptionistCall>().SingleAsync();
        Assert.Equal("live_browser_controller", call.ProviderSessionId);
        Assert.Equal(call.ReservedUtc, call.MustStopUtc); Assert.Null(call.ProviderClosedUtc);
        var cleanup = await db.Set<ReceptionistActionIntent>().SingleAsync(x => x.CapabilityKey == "__provider_stop");
        Assert.Equal("rejected", cleanup.State);
        Assert.Equal("browser-cleanup-unused", cleanup.VerificationReceipt);
        Assert.False((await db.Set<ReceptionistCostReservation>().SingleAsync()).CashFinal);
        await AssertOrphanBrowserCleanupAfterControllerReturnAsync(f, call.Id);
    }

    [Fact]
    public async Task FailedBindingCompensatesOnlyOriginalSessionThenReconcilesWithoutRecreating()
    {
        var interceptor = new BrowserBindInterceptor(failBeforeSave: true);
        await using var f = await Fixture.CreateAsync(browser: true, interceptor: interceptor);
        using var scope = f.CreateScope(); var controller = BrowserController(scope.ServiceProvider);
        var grant = Field(await controller.Grant(new("/contact"), default), "grant");
        Assert.IsNotType<OkObjectResult>(await controller.Start(new(grant, "v=0\r\n"), default));
        Assert.Equal(1, f.Transport.BrowserCreates);
        Assert.Empty(f.Transport.Hangups); Assert.Empty(f.Transport.Attaches);
        await using var db = f.Database();
        var call = await db.Set<ReceptionistCall>().SingleAsync();
        Assert.Equal("live_browser_controller", call.ProviderSessionId); Assert.Equal("uncertain", call.State);
        Assert.Equal("rejected", (await db.Set<ReceptionistActionIntent>().SingleAsync(x => x.CapabilityKey == "__provider_stop")).State);
        Assert.Equal(call.Id, Assert.Single(await f.Admission.FindStopRequestsAsync(100)).CallId);
        Assert.False((await db.Set<ReceptionistCostReservation>().SingleAsync()).CashFinal);
        await AssertOrphanBrowserCleanupAfterControllerReturnAsync(f, call.Id);
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("permission")]
    [InlineData("revoked")]
    [InlineData("revision")]
    [InlineData("expired")]
    [InlineData("duplicate")]
    [InlineData("public_endpoint")]
    public async Task RestrictedGrantRechecksActorPermissionAndExactPersistedWindow(string change)
    {
        await using var f = await Fixture.CreateAsync(browser: true, controlledBrowser: true);
        using var scope = f.CreateScope(); var controller = BrowserController(scope.ServiceProvider);
        controller.Request.Headers.Origin = "https://portal.mylegnd.com";
        void Actor(string actor) => controller.HttpContext.User = new(new System.Security.Claims.ClaimsIdentity(
            [new(System.Security.Claims.ClaimTypes.NameIdentifier, actor), new("oid", actor)], "synthetic-test"));
        Actor("synthetic-owner");
        var owner = Shared.Analytics.MarketingOwnerScope.Founder;
        var grant = Field(await controller.GrantForOwnerAsync(owner, (_, _) => Task.FromResult(true), default), "grant");
        if (change == "actor") Actor("different-owner");
        if (change == "expired") f.Clock.Advance(TimeSpan.FromHours(1));
        await using (var db = f.Database())
        {
            var window = await db.Set<ReceptionistAcceptanceWindow>().SingleAsync();
            if (change == "revoked") window.RevokedUtc = f.Clock.GetUtcNow().UtcDateTime;
            if (change == "revision") window.Revision = Guid.NewGuid();
            if (change == "duplicate")
            {
                var binding = (await f.Admission.GetIngressBindingAsync("openai", "test_ingress", default))!;
                await ReceptionistTestWindows.SeedAsync(db, binding.MarketingConnectionId, binding.ConfigurationId, binding.Binding, f.Clock.GetUtcNow().UtcDateTime);
            }
            await db.SaveChangesAsync();
        }
        var result = change == "public_endpoint" ? await controller.Start(new(grant, "v=0\r\n"), default) :
            await controller.StartForOwnerAsync(owner, new(grant, "v=0\r\n"), (_, _) => Task.FromResult(change != "permission"), default);
        Assert.IsNotType<OkObjectResult>(result); Assert.Equal(0, f.Transport.BrowserCreates);
        await using var read = f.Database(); Assert.Empty(await read.Set<ReceptionistCall>().ToArrayAsync());
    }

    [Theory]
    [InlineData("revoked")]
    [InlineData("nonhex")]
    [InlineData("space")]
    public async Task RestrictedBrowserPinsWindowDeadlineAndRevocationFencesItsCall(string malformedEvidence)
    {
        await using var f = await Fixture.CreateAsync(browser: true, controlledBrowser: true);
        using var scope = f.CreateScope(); var controller = BrowserController(scope.ServiceProvider);
        controller.Request.Headers.Origin = "https://portal.mylegnd.com";
        controller.HttpContext.User = new(new System.Security.Claims.ClaimsIdentity(
            [new(System.Security.Claims.ClaimTypes.NameIdentifier, "synthetic-owner"), new("oid", "synthetic-owner")], "synthetic-test"));
        var owner = Shared.Analytics.MarketingOwnerScope.Founder;
        await using (var db = f.Database())
        { (await db.Set<ReceptionistAcceptanceWindow>().SingleAsync()).ExpiresUtc = f.Clock.GetUtcNow().UtcDateTime.AddSeconds(20); await db.SaveChangesAsync(); }
        var grant = Field(await controller.GrantForOwnerAsync(owner, (_, _) => Task.FromResult(true), default), "grant");
        var handle = Field(await controller.StartForOwnerAsync(owner, new(grant, "v=0\r\n"), (_, _) => Task.FromResult(true), default), "handle");
        Assert.Equal(1, f.Transport.BrowserCreates);
        await using (var db = f.Database())
        {
            var call = await db.Set<ReceptionistCall>().SingleAsync(); var window = await db.Set<ReceptionistAcceptanceWindow>().SingleAsync();
            Assert.True(call.ControlledTest); Assert.Equal(window.ConfigurationVersionId, call.ConfigurationVersionId); Assert.Equal(window.ExpiresUtc, call.MustStopUtc);
            if (malformedEvidence == "nonhex") window.RestrictedRouteEvidenceSha256 = new string('z', 64);
            else if (malformedEvidence == "space") window.RestrictedRouteEvidenceSha256 = new string('a', 31) + " " + new string('a', 32);
            else window.RevokedUtc = f.Clock.GetUtcNow().UtcDateTime;
            window.Revision = Guid.NewGuid(); await db.SaveChangesAsync();
        }
        Assert.Equal("ending", Field(await controller.Status(new(handle), default), "state"));
        Assert.IsNotType<OkObjectResult>(await controller.StartForOwnerAsync(owner, new(grant, "v=0\r\n"), (_, _) => Task.FromResult(true), default));
        Assert.Equal(1, f.Transport.BrowserCreates);
    }

    private static async Task AssertOrphanBrowserCleanupAfterControllerReturnAsync(Fixture f, Guid id)
    {
        // The creator has returned and no controller-owned provider operation remains.
        // A restarted/disabled worker discovers the same durable identity, never creates again.
        f.SetRuntimeEnabled(false);
        Assert.Equal(id, Assert.Single(await f.Admission.FindObservationCandidatesAsync(100, TimeSpan.FromMinutes(2))).CallId);
        await using var host = await f.StartAsync();
        var socket = await f.Transport.SocketAsync(0);
        await EventuallyAsync(() => socket.Sent.Any(IsClose));
        Assert.Equal("live_browser_controller", Assert.Single(f.Transport.Attaches).Session);
        Assert.Single(socket.Sent.Where(IsClose)); Assert.Empty(f.Transport.Hangups);
        Assert.Equal(1, f.Transport.BrowserCreates);
        socket.Queue(Closed("live_browser_controller", "closed-orphan", 2m));
        await EventuallyAsync(async () => (await f.CallAsync(id)).ProviderClosedUtc is not null);
        await host.StopAsync();
        await AssertFinalUsageWithMoneyStillReservedAsync(f, id, 2);
    }

    private sealed class BrowserBindInterceptor(bool failBeforeSave = false, Action? afterSave = null) : SaveChangesInterceptor
    {
        private bool triggered;
        private static bool IsBinding(DbContext? db) => db?.ChangeTracker.Entries<ReceptionistCall>().Any(x =>
            x.Entity.BrowserCreationId is not null && x.Entity.ProviderSessionId is not null) == true;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!triggered && failBeforeSave && IsBinding(eventData.Context))
            { triggered = true; throw new DbUpdateException("Synthetic original-bind persistence failure"); }
            return ValueTask.FromResult(result);
        }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (!triggered && afterSave is not null && IsBinding(eventData.Context))
            { triggered = true; afterSave(); }
            return ValueTask.FromResult(result);
        }
    }
}
