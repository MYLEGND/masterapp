using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Businesses;
using Infrastructure.Data;
using Infrastructure.WebsiteEditing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Moq;
using ParfaitApp.Services;
using Xunit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using ParfaitApp.Controllers;

namespace AgentPortal.Tests;

public sealed class CommerceManagementScopeTests
{
    [Theory]
    [InlineData("http", "one.example.invalid")]
    [InlineData("https", "one.example.invalid:8443")]
    public async Task Insecure_or_different_port_request_cannot_manufacture_destination_origin(string scheme, string host)
    {
        using var db = ControllerTestHelpers.BuildDb();
        var (business, _) = Seed(db, "business", "tenant-one");
        AddDomain(db, business.Id, "one.example.invalid");
        await db.SaveChangesAsync();
        var request = Request(host);
        request.Request.Scheme = scheme;
        request.Request.Headers["X-Forwarded-Proto"] = "https";
        Assert.Null(await Service(db).ResolveManagementScopeAsync(request, null));
    }

    [Fact]
    public async Task Dedicated_domain_resolves_retained_identity_without_loading_or_enabling_store()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var (business, state) = Seed(db, "business", "tenant-one");
        AddDomain(db, business.Id, "one.example.invalid");
        await db.SaveChangesAsync();
        var result = await Service(db).ResolveManagementScopeAsync(Request("one.example.invalid"), null);
        Assert.NotNull(result);
        Assert.Equal(business.Id, result.CommerceBusinessId);
        Assert.Equal(state.Id, result.WebsiteStateId);
        Assert.Equal(state.OwnerKey, result.WebsiteOwnerKey);
        Assert.Equal("https://one.example.invalid", result.DestinationOrigin);
        Assert.Null(state.PublishedVersionId);
        Assert.Equal("not loaded as a document", state.DraftJson);
        Assert.False(db.ChangeTracker.HasChanges());
        Assert.Null(await Service(db).ResolveManagementScopeAsync(Request("one.example.invalid"), "tenant-two"));
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("certificate")]
    [InlineData("disabled")]
    [InlineData("wrong-owner")]
    [InlineData("inactive-business")]
    public async Task Invalid_domain_or_binding_fails_closed(string defect)
    {
        using var db = ControllerTestHelpers.BuildDb();
        var (business, state) = Seed(db, "business", "tenant-one");
        var binding = AddDomain(db, business.Id, "one.example.invalid");
        if (defect == "expired") binding.LastCheckedUtc = DateTime.UtcNow.AddDays(-2);
        if (defect == "certificate") binding.CertificateStatus = "pending";
        if (defect == "disabled") binding.Status = "removing";
        if (defect == "wrong-owner") state.OwnerKey = WebsiteEditorSiteKeys.BusinessOwnerKey(Guid.NewGuid());
        if (defect == "inactive-business") business.IsActive = false;
        await db.SaveChangesAsync();
        Assert.Null(await Service(db).ResolveManagementScopeAsync(Request("one.example.invalid"), null));
    }

    [Fact]
    public async Task Shared_Protect_host_requires_exact_key_and_Protect_state()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var (one, _) = Seed(db, "protect", "agent-one");
        var (two, _) = Seed(db, "protect", "agent-two");
        Seed(db, "business", "ordinary-business");
        await db.SaveChangesAsync();
        var service = Service(db);
        var request = Request("protect.mylegnd.com");
        Assert.Null(await service.ResolveManagementScopeAsync(request, null));
        Assert.Equal(one.Id, (await service.ResolveManagementScopeAsync(request, one.Key))!.CommerceBusinessId);
        Assert.Equal(two.Id, (await service.ResolveManagementScopeAsync(request, two.Key))!.CommerceBusinessId);
        Assert.Null(await service.ResolveManagementScopeAsync(request, "ordinary-business"));
        db.Add(new WebsiteContentState { SiteKey = "protect", OwnerKey = "ambiguous-owner", CommerceBusinessId = one.Id });
        await db.SaveChangesAsync();
        Assert.Null(await service.ResolveManagementScopeAsync(request, one.Key));
    }

    [Fact]
    public async Task Legend_has_only_its_global_binding_and_unknown_hosts_never_seed_Parfait()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var (legend, _) = Seed(db, "legend", "legend-store");
        await db.SaveChangesAsync();
        var service = Service(db);
        Assert.Equal(legend.Id, (await service.ResolveManagementScopeAsync(Request("mylegnd.com"), null))!.CommerceBusinessId);
        Assert.Null(await service.ResolveManagementScopeAsync(Request("mylegnd.com"), "legend-store"));
        Assert.Null(await service.ResolveManagementScopeAsync(Request("unknown.example.invalid"), null));
        Assert.Null(await service.ResolveManagementScopeAsync(Request("shopparfait.com"), null));
        Assert.Single(db.CommerceBusinesses);
        Assert.False(db.ChangeTracker.HasChanges());
    }

    [Fact]
    public async Task Spoofed_original_host_is_ignored_and_verified_bridge_preserves_domain_scope()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var (one, _) = Seed(db, "business", "tenant-one");
        var (two, _) = Seed(db, "business", "tenant-two");
        AddDomain(db, one.Id, "one.example.invalid");
        AddDomain(db, two.Id, "two.example.invalid");
        await db.SaveChangesAsync();
        const string secret = "synthetic-test-only-bridge-secret-not-a-credential";
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WebsiteRouting:BridgeSecret"] = secret,
            ["WebsiteRouting:BridgeOriginHost"] = "masterapp-protect.azurewebsites.net"
        }).Build();
        var service = Service(db, config);
        var request = Request("one.example.invalid");
        request.Request.Headers[WebsiteRequestHostResolver.OriginalHostHeader] = "two.example.invalid";
        request.Request.Headers[WebsiteRequestHostResolver.BridgeSecretHeader] = secret;
        Assert.Equal(one.Id, (await service.ResolveManagementScopeAsync(request, null))!.CommerceBusinessId);
        request.Request.Host = new HostString("masterapp-protect.azurewebsites.net");
        request.Request.Headers[WebsiteRequestHostResolver.BridgeSecretHeader] = "wrong";
        Assert.Null(await service.ResolveManagementScopeAsync(request, null));
        request.Request.Headers[WebsiteRequestHostResolver.BridgeSecretHeader] = secret;
        Assert.Equal(two.Id, (await service.ResolveManagementScopeAsync(request, null))!.CommerceBusinessId);
    }

    [Theory]
    [InlineData("legend")]
    [InlineData("protect")]
    public async Task Website_owner_management_requires_exact_scope_and_current_agent_lifecycle(string site)
    {
        using var db = ControllerTestHelpers.BuildDb();
        var actor = Guid.NewGuid().ToString();
        var (business, state) = Seed(db, site, "synthetic-owner-store");
        if (site == "protect") state.OwnerKey = actor;
        state.ProvisionedStoreBusinessId = business.Id;
        var agent = new AgentProfile { AgentUserId = actor, IsActive = true };
        db.Add(agent);
        db.Add(new AgentTrackingProfile { AgentUserId = actor, AgentUpn = "synthetic-owner@example.invalid", Slug = "synthetic-owner", Status = "active" });
        await db.SaveChangesAsync();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Founder:Oid"] = actor }).Build();
        var service = Service(db, config);
        var scope = (await service.ResolveManagementScopeAsync(Request(site == "legend" ? "mylegnd.com" : "protect.mylegnd.com"),
            site == "protect" ? business.Key : null))!;
        Assert.NotNull(await service.ResolveManagementActorAsync(scope, actor, "analytics"));
        using var tickets = new WebsiteEditorTicketProtector(new EphemeralDataProtectionProvider());
        await using var services = new ServiceCollection().AddSingleton(db).BuildServiceProvider();
        var context = Request(site == "legend" ? "mylegnd.com" : "protect.mylegnd.com");
        context.RequestServices = services;
        context.Request.Path = "/internal/settings/team";
        context.Request.Method = "GET";
        if (site == "protect") context.Request.QueryString = new QueryString("?businessKey=" + business.Key);
        state.DraftJson = System.Text.Json.JsonSerializer.Serialize(new WebsiteContentDocument(), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        await db.SaveChangesAsync();
        var controller = new CommerceManagementController(tickets, config, service, null!, null!, null!, null!, null!, null!, null!, null!, null!)
            { ControllerContext = new ControllerContext { HttpContext = context } };
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(context, Mock.Of<Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider>());
        var ownerTicket = new WebsiteEditorTicket(site, state.OwnerKey, "synthetic-owner", site == "legend", DateTime.UtcNow.AddMinutes(5), null, actor);
        Assert.IsType<ViewResult>(await controller.Team(tickets.Protect(ownerTicket)));
        Assert.IsType<UnauthorizedResult>(await controller.Team(tickets.Protect(ownerTicket with { CommerceBusinessId = Guid.NewGuid() })));

        Assert.Null(await service.ResolveManagementActorAsync(scope, Guid.NewGuid().ToString(), "analytics"));
        Assert.Null(await service.ResolveManagementActorAsync(scope, actor, "unknown"));
        db.Add(new AccountLifecycleRecord { UserId = actor, ParticipantType = Domain.Messaging.MessagingParticipantTypes.Agent,
            ProfileId = agent.Id, State = Domain.Accounts.AccountLifecycleStates.Paused });
        await db.SaveChangesAsync();
        Assert.Null(await service.ResolveManagementActorAsync(scope, actor, "analytics"));
    }

    private static (CommerceBusiness, WebsiteContentState) Seed(MasterAppDbContext db, string site, string key)
    {
        var business = new CommerceBusiness { Key = key, Status = "Active", IsActive = true };
        var state = new WebsiteContentState
        {
            CommerceBusinessId = business.Id, SiteKey = site,
            OwnerKey = site == "business" ? WebsiteEditorSiteKeys.BusinessOwnerKey(business.Id) :
                site == "legend" ? WebsiteEditorSiteKeys.GlobalOwnerKey : key,
            DraftJson = "not loaded as a document"
        };
        db.AddRange(business, state);
        return (business, state);
    }

    private static WebsiteDomainBinding AddDomain(MasterAppDbContext db, Guid business, string host)
    {
        var binding = new WebsiteDomainBinding { CommerceBusinessId = business, Hostname = host,
            Status = "active", CertificateStatus = "active", LastCheckedUtc = DateTime.UtcNow };
        db.Add(binding);
        return binding;
    }

    private static DefaultHttpContext Request(string host)
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString(host);
        context.Request.Scheme = "https";
        return context;
    }

    private static CommerceStoreContextService Service(MasterAppDbContext db, IConfiguration? config = null)
    {
        config ??= new ConfigurationBuilder().Build();
        return new(db, new CommerceBusinessScopeResolver(db), new ParfaitBusinessScopeService(db),
            new WebsiteDomainService(db, Mock.Of<IHttpClientFactory>(), config), config);
    }
}
