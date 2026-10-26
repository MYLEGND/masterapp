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
        Assert.Equal("live_browser_controller", Assert.Single(f.Transport.Hangups).Session);
        await using var db = f.Database();
        var call = await db.Set<ReceptionistCall>().SingleAsync();
        Assert.Equal("live_browser_controller", call.ProviderSessionId);
        Assert.Equal(call.ReservedUtc, call.MustStopUtc); Assert.Null(call.ProviderClosedUtc);
        var cleanup = await db.Set<ReceptionistActionIntent>().SingleAsync(x => x.CapabilityKey == "__provider_stop");
        Assert.Equal("uncertain", cleanup.State);
        Assert.False((await db.Set<ReceptionistCostReservation>().SingleAsync()).CashFinal);
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
        Assert.Equal("live_browser_controller", Assert.Single(f.Transport.Hangups).Session);
        await using var db = f.Database();
        var call = await db.Set<ReceptionistCall>().SingleAsync();
        Assert.Equal("live_browser_controller", call.ProviderSessionId); Assert.Equal("uncertain", call.State);
        Assert.Equal("uncertain", (await db.Set<ReceptionistActionIntent>().SingleAsync(x => x.CapabilityKey == "__provider_stop")).State);
        Assert.False((await f.Admission.ClaimProviderStopAsync(call.Id, "competing-worker")).MayExecute);
        Assert.False((await db.Set<ReceptionistCostReservation>().SingleAsync()).CashFinal);
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
