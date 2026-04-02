using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using AgentPortal.Middleware;
using AgentPortal.Services;
using AgentPortal.Services.Tracking;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

/// <summary>
/// Unit tests for AssistantResolutionMiddleware and FounderImpersonationMiddleware.
/// Uses InMemory DB + mocks — no live infrastructure required.
/// </summary>
public class MiddlewareTests
{
    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static MasterAppDbContext BuildDb() => ControllerTestHelpers.BuildDb();

    private static IConfiguration BuildConfig(
        string tenantId = "test-tenant",
        string domain = "mylegnd.com") =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new[]
            {
                new KeyValuePair<string, string?>("AzureAd:TenantId", tenantId),
                new KeyValuePair<string, string?>("AzureAd:Domain", domain)
            })
            .Build();

    private static ClaimsPrincipal AuthenticatedUser(string oid, string? email = null, bool guestTenant = false)
    {
        var tid = guestTenant ? "external-tenant" : "test-tenant";
        var claims = new List<Claim>
        {
            new("oid", oid),
            new("tid", tid),
        };
        if (email != null)
            claims.Add(new("preferred_username", email));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }

    private static HttpContext BuildHttpContext(ClaimsPrincipal user, string path = "/Leads")
    {
        var ctx = new DefaultHttpContext { User = user };
        ctx.Request.Path = path;
        ctx.Request.Headers.Accept = "text/html";
        return ctx;
    }

    private static (AssistantResolutionMiddleware mw, AssistantContextService ctx, AgentRegistryService reg)
        BuildAssistantMiddleware(MasterAppDbContext db, RequestDelegate? next = null)
    {
        next ??= _ => Task.CompletedTask;
        var config = BuildConfig();
        var mw = new AssistantResolutionMiddleware(next, config);
        var tracking = Mock.Of<IAgentTrackingService>();
        var ctx = new AssistantContextService(db, NullLogger<AssistantContextService>.Instance);
        var reg = new AgentRegistryService(db, NullLogger<AgentRegistryService>.Instance, tracking);
        return (mw, ctx, reg);
    }

    // -----------------------------------------------------------------------
    // AssistantResolutionMiddleware — happy path: first-party agent passes through
    // -----------------------------------------------------------------------
    [Fact]
    public async Task AssistantMiddleware_FirstPartyAgent_PassesThrough()
    {
        using var db = BuildDb();
        var (mw, ctx, reg) = BuildAssistantMiddleware(db);
        var nextCalled = false;
        var (mw2, ctx2, reg2) = BuildAssistantMiddleware(db, _ => { nextCalled = true; return Task.CompletedTask; });

        var user = AuthenticatedUser("oid-agent-fp", "agent@mylegnd.com", guestTenant: false);
        var httpCtx = BuildHttpContext(user, "/Leads");

        await mw2.InvokeAsync(httpCtx, ctx2, reg2);

        Assert.True(nextCalled);
        Assert.Equal(200, httpCtx.Response.StatusCode);
    }

    // -----------------------------------------------------------------------
    // AssistantResolutionMiddleware — guest not assigned as assistant → 302 to /Access/Limited
    // -----------------------------------------------------------------------
    [Fact]
    public async Task AssistantMiddleware_UnassignedGuest_RedirectsToLimited()
    {
        using var db = BuildDb();
        var redirectTarget = string.Empty;
        RequestDelegate next = _ => Task.CompletedTask;
        var (mw, ctx, reg) = BuildAssistantMiddleware(db, next);
        var (mw2, ctx2, reg2) = BuildAssistantMiddleware(db, c =>
        {
            redirectTarget = c.Response.Headers.Location.ToString();
            return Task.CompletedTask;
        });

        // Guest from external tenant, no assistant record
        var user = AuthenticatedUser("oid-guest-1", "guest@external.com", guestTenant: true);
        var httpCtx = BuildHttpContext(user, "/Leads");

        await mw2.InvokeAsync(httpCtx, ctx2, reg2);

        // Should redirect, not call next with 200
        Assert.True(httpCtx.Response.StatusCode == 302 || httpCtx.Response.Headers.ContainsKey("Location"),
            "Expected redirect for unassigned guest");
    }

    // -----------------------------------------------------------------------
    // AssistantResolutionMiddleware — active assistant accessing allowed route passes through
    // -----------------------------------------------------------------------
    [Fact]
    public async Task AssistantMiddleware_ActiveAssistant_AllowedRoute_PassesThrough()
    {
        using var db = BuildDb();

        // Seed: parent agent + active assistant
        var parentOid = "parent-oid-1";
        var assistantOid = "assistant-oid-1";
        var assistantEmail = "assistant@example.com";

        db.AgentAssistants.Add(new AgentAssistant
        {
            Id = Guid.NewGuid(),
            ParentAgentUserId = parentOid,
            AssistantUserId = assistantOid,
            FirstName = "Asst",
            LastName = "Test",
            Email = assistantEmail,
            NormalizedEmail = assistantEmail,
            IsActive = true,
            InvitedAt = DateTime.UtcNow,
            CreatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var nextCalled = false;
        var (mw, ctx, reg) = BuildAssistantMiddleware(db, _ => { nextCalled = true; return Task.CompletedTask; });

        var user = AuthenticatedUser(assistantOid, assistantEmail, guestTenant: true);
        var httpCtx = BuildHttpContext(user, "/Leads");
        // Simulate routing values that middleware reads
        httpCtx.Request.RouteValues["controller"] = "Leads";
        httpCtx.Request.RouteValues["action"] = "Index";

        await mw.InvokeAsync(httpCtx, ctx, reg);

        Assert.True(nextCalled);
    }

    // -----------------------------------------------------------------------
    // AssistantResolutionMiddleware — active assistant blocked from forbidden route → 302
    // -----------------------------------------------------------------------
    [Fact]
    public async Task AssistantMiddleware_ActiveAssistant_ForbiddenRoute_Redirects()
    {
        using var db = BuildDb();

        var parentOid = "parent-oid-2";
        var assistantOid = "assistant-oid-2";
        var assistantEmail = "asst2@example.com";

        db.AgentAssistants.Add(new AgentAssistant
        {
            Id = Guid.NewGuid(),
            ParentAgentUserId = parentOid,
            AssistantUserId = assistantOid,
            FirstName = "Asst",
            LastName = "Two",
            Email = assistantEmail,
            NormalizedEmail = assistantEmail,
            IsActive = true,
            InvitedAt = DateTime.UtcNow,
            CreatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var nextCalled = false;
        var (mw, ctx, reg) = BuildAssistantMiddleware(db, _ => { nextCalled = true; return Task.CompletedTask; });

        var user = AuthenticatedUser(assistantOid, assistantEmail, guestTenant: true);
        var httpCtx = BuildHttpContext(user, "/Finance");
        httpCtx.Request.RouteValues["controller"] = "Finance"; // not in AssistantAllowedControllers
        httpCtx.Request.RouteValues["action"] = "Index";

        await mw.InvokeAsync(httpCtx, ctx, reg);

        Assert.False(nextCalled, "Next should not be called for a forbidden route");
        // Either 302 redirect or 403 for API clients
        Assert.True(
            httpCtx.Response.StatusCode == 302 || httpCtx.Response.StatusCode == 403,
            $"Expected 302 or 403, got {httpCtx.Response.StatusCode}");
    }

    // -----------------------------------------------------------------------
    // AssistantResolutionMiddleware — disabled assistant → 302
    // -----------------------------------------------------------------------
    [Fact]
    public async Task AssistantMiddleware_DisabledAssistant_Redirects()
    {
        using var db = BuildDb();

        var parentOid = "parent-oid-3";
        var assistantOid = "assistant-oid-3";
        var assistantEmail = "asst3@example.com";

        db.AgentAssistants.Add(new AgentAssistant
        {
            Id = Guid.NewGuid(),
            ParentAgentUserId = parentOid,
            AssistantUserId = assistantOid,
            FirstName = "Disabled",
            LastName = "Asst",
            Email = assistantEmail,
            NormalizedEmail = assistantEmail,
            IsActive = false, // disabled
            InvitedAt = DateTime.UtcNow,
            CreatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var nextCalled = false;
        var (mw, ctx, reg) = BuildAssistantMiddleware(db, _ => { nextCalled = true; return Task.CompletedTask; });

        var user = AuthenticatedUser(assistantOid, assistantEmail, guestTenant: true);
        var httpCtx = BuildHttpContext(user, "/Leads");
        httpCtx.Request.RouteValues["controller"] = "Leads";

        await mw.InvokeAsync(httpCtx, ctx, reg);

        Assert.False(nextCalled, "Disabled assistant should be blocked before next()");
    }

    // -----------------------------------------------------------------------
    // FounderImpersonationMiddleware — non-founder: no impersonation context set
    // -----------------------------------------------------------------------
    [Fact]
    public async Task FounderMiddleware_NonFounder_NoImpersonationSet()
    {
        var nextCalled = false;
        RequestDelegate next = ctx =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };
        var mw = new FounderImpersonationMiddleware(next);

        var user = AuthenticatedUser("regular-oid", "agent@mylegnd.com");
        var httpCtx = BuildHttpContext(user);
        var svc = Mock.Of<FounderImpersonationService>();

        await mw.InvokeAsync(httpCtx, svc);

        Assert.True(nextCalled);
        Assert.False(httpCtx.Items.ContainsKey("ImpersonatedAgentOid"));
    }
}
