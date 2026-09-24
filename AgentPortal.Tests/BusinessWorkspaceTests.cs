using System;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Analytics;
using Infrastructure.Businesses;
using Infrastructure.Bookings;
using Infrastructure.Leads;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProtectWebsite.Services.Communication;
using Shared.Analytics;
using Shared.Crm;
using Xunit;

namespace AgentPortal.Tests;

public sealed class BusinessWorkspaceTests
{
    [Fact]
    public async Task BulkEditsRejectStaleBatchWithoutChangingAnyContact()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var business = new CommerceBusiness { Key = "bulk" };
        var first = new WorkstationLeadProfile { LeadId = "bulk-first", CommerceBusinessId = business.Id, CrmStage = "New" };
        var second = new WorkstationLeadProfile { LeadId = "bulk-second", CommerceBusinessId = business.Id, CrmStage = "New" };
        db.AddRange(business, first, second, new CommerceBusinessStorefrontSettings { CommerceBusinessId = business.Id });
        await db.SaveChangesAsync();
        var service = new BusinessWorkspaceService(db, Mock.Of<IAnalyticsQueryService>(), new(db, new ConfigurationBuilder().Build()));
        var input = new BusinessCrmBulkRequest { ClientUserIds = [first.LeadId, second.LeadId], PipelineStage = "Qualified", CrmPriority = "High" };
        foreach (var id in input.ClientUserIds)
        {
            var payload = System.Text.Json.JsonSerializer.SerializeToElement(await service.QuickViewAsync(business.Id, id, "Lead", default));
            input.Revisions[id] = payload.GetProperty("revision").GetString()!;
        }
        var validRevision = input.Revisions[second.LeadId];
        input.Revisions[second.LeadId] = "stale";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.BulkUpdateAsync(business.Id, input, "owner", default));
        Assert.Equal("New", first.CrmStage);
        Assert.Equal("New", second.CrmStage);
        input.Revisions[second.LeadId] = validRevision;
        Assert.NotNull(await service.BulkUpdateAsync(business.Id, input, "owner", default));
        Assert.Equal("Qualified", first.CrmStage);
        Assert.Equal("High", ClientCrmMetaSerializer.Deserialize(first.CrmNotes, new BusinessWorkspacePreferences().Stages).CrmPriority);
    }

    [Fact]
    public async Task BusinessQuickViewPersistsCustomStageAndRejectsStaleOrForeignWrites()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var business = new CommerceBusiness { Key = "contract" };
        var preferences = new BusinessWorkspacePreferences { Stages = ["Estimate", "Scheduled", "Complete"] };
        var row = new WorkstationLeadProfile { LeadId = "contract-lead", CommerceBusinessId = business.Id,
            AgentUserId = "", CrmStatus = "Lead", CrmStage = "Estimate", FirstName = "Contact" };
        var foreign = new WorkstationLeadProfile { LeadId = "foreign-contract", CommerceBusinessId = Guid.NewGuid(),
            AgentUserId = "", CrmStatus = "Lead", CrmStage = "Estimate" };
        db.AddRange(business, row, foreign, new CommerceBusinessStorefrontSettings
            { CommerceBusinessId = business.Id, WorkspacePreferencesJson = preferences.Write() });
        await db.SaveChangesAsync();
        var service = new BusinessWorkspaceService(db, Mock.Of<IAnalyticsQueryService>(), new(db, new ConfigurationBuilder().Build()));
        var initial = System.Text.Json.JsonSerializer.SerializeToElement(await service.QuickViewAsync(business.Id, row.LeadId, "Lead", default));
        var input = new BusinessCrmQuickViewRequest { ClientUserId = row.LeadId,
            Revision = initial.GetProperty("revision").GetString()!, PipelineStage = "Scheduled", CrmStatus = "Lead",
            Email = "contact@example.org", AgentNotes = "Use side entrance", CrmNextDate = DateTime.UtcNow.Date.AddDays(1),
            CrmNextText = "Confirm estimate" };
        var saved = System.Text.Json.JsonSerializer.SerializeToElement(await service.SaveQuickViewAsync(business.Id, "Lead", input, "owner", default));
        Assert.Equal("Scheduled", saved.GetProperty("pipelineStage").GetString());
        Assert.Equal("Use side entrance", ClientCrmMetaSerializer.Deserialize(row.CrmNotes, preferences.Stages).AgentNotes);
        Assert.NotEqual(input.Revision, saved.GetProperty("revision").GetString());
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.SaveQuickViewAsync(business.Id, "Lead", input, "owner", default));
        Assert.Null(await service.QuickViewAsync(business.Id, foreign.LeadId, "Lead", default));
        input.ClientUserId = foreign.LeadId;
        Assert.Null(await service.SaveQuickViewAsync(business.Id, "Lead", input, "owner", default));
        Assert.Null(await service.QuickViewAsync(business.Id, row.LeadId, "Client", default));
        var activity = new BusinessCrmActivityRequest { ClientUserId = row.LeadId,
            Revision = saved.GetProperty("revision").GetString()!, Type = "Note", Note = "Confirmed access" };
        Assert.NotNull(await service.AddActivityAsync(business.Id, activity, "owner", default));
        Assert.Contains(ClientCrmMetaSerializer.Deserialize(row.CrmNotes, preferences.Stages).Activities,
            x => x.Note == "Confirmed access" && x.CreatedBy == "owner");
    }

    [Fact]
    public async Task BusinessReorderRejectsEntireMixedTenantBatchAndStaleRevision()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var business = new CommerceBusiness { Key = "reorder" };
        var first = new WorkstationLeadProfile { LeadId = "first", CommerceBusinessId = business.Id, CrmStage = "New" };
        var second = new WorkstationLeadProfile { LeadId = "second", CommerceBusinessId = business.Id, CrmStage = "New" };
        var other = new WorkstationLeadProfile { LeadId = "other", CommerceBusinessId = Guid.NewGuid(), CrmStage = "New" };
        db.AddRange(business, first, second, other, new CommerceBusinessStorefrontSettings { CommerceBusinessId = business.Id });
        await db.SaveChangesAsync();
        var service = new BusinessWorkspaceService(db, Mock.Of<IAnalyticsQueryService>(), new(db, new ConfigurationBuilder().Build()));
        var request = new BusinessCrmReorderRequest { Bucket = "Qualified", Ids = [second.LeadId, first.LeadId] };
        foreach (var id in request.Ids)
        {
            var payload = System.Text.Json.JsonSerializer.SerializeToElement(await service.QuickViewAsync(business.Id, id, "Lead", default));
            request.Revisions[id] = payload.GetProperty("revision").GetString()!;
        }
        request.Ids.Add(other.LeadId);
        Assert.Null(await service.ReorderAsync(business.Id, "Lead", request, "owner", default));
        Assert.Equal("New", first.CrmStage);
        Assert.Equal("New", second.CrmStage);
        request.Ids.Remove(other.LeadId);
        Assert.NotNull(await service.ReorderAsync(business.Id, "Lead", request, "owner", default));
        Assert.Equal("Qualified", first.CrmStage);
        Assert.Equal(0, second.CrmOrder);
        Assert.Equal(1, first.CrmOrder);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.ReorderAsync(business.Id, "Lead", request, "owner", default));
        Assert.Equal("New", other.CrmStage);
    }

    [Fact]
    public async Task CanonicalBoardReceivesAllScopedContactsForItsOwnPagination()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var business = new CommerceBusiness { Key = "full-board" };
        db.AddRange(business, new CommerceBusinessStorefrontSettings { CommerceBusinessId = business.Id });
        for (var index = 0; index < 41; index++) db.Add(new WorkstationLeadProfile
        {
            LeadId = "contact-" + index, CommerceBusinessId = business.Id, AgentUserId = "", CrmStatus = "Lead", CrmOrder = index
        });
        await db.SaveChangesAsync();
        var service = new BusinessWorkspaceService(db, Mock.Of<IAnalyticsQueryService>(), new(db, new ConfigurationBuilder().Build()));
        var board = await service.CrmAsync(business, "Lead", null, 1, null, default);
        Assert.Equal(41, board.Total);
        Assert.Equal(41, board.CanonicalContacts.Count);
    }

    [Fact]
    public async Task AgentQuickFindRequiresSelectedBusinessAndNeverEnumeratesOtherClients()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var selected = new CommerceBusiness { Key = "selected-navigation" };
        var other = new CommerceBusiness { Key = "other-navigation" };
        var profile = new ClientProfile { ClientUserId = Guid.NewGuid().ToString(), Email = "client@example.org",
            AccountManagementMode = ClientAccountManagementModes.SharedAccount };
        var actor = "navigation-agent";
        db.AddRange(selected, other, profile,
            new AgentClient { AgentUserId = actor, AgentUpn = "agent@example.org", ClientUserId = profile.ClientUserId },
            new CommerceBusinessMember { CommerceBusinessId = selected.Id, ClientProfileId = profile.Id },
            new CommerceBusinessMember { CommerceBusinessId = other.Id, ClientProfileId = profile.Id },
            new CommerceBusinessStorefrontSettings { CommerceBusinessId = selected.Id },
            new CommerceBusinessStorefrontSettings { CommerceBusinessId = other.Id });
        await db.SaveChangesAsync();
        var service = new BusinessWorkspaceService(db, Mock.Of<IAnalyticsQueryService>(), new(db, new ConfigurationBuilder().Build()));
        Assert.Empty(await service.NavigationForBusinessAsync(null, actor, "agent@example.org", default));
        Assert.Empty(await service.NavigationForBusinessAsync(Guid.Empty, actor, "agent@example.org", default));
        Assert.Equal(selected.Id, Assert.Single(await service.NavigationForBusinessAsync(selected.Id, actor, "agent@example.org", default)).BusinessId);
        Assert.Empty(await service.NavigationForBusinessAsync(selected.Id, "unrelated", "stranger@example.org", default));
        Assert.Equal(2, (await service.NavigationAsync(profile.Id, profile.ClientUserId, profile.Email, default)).Count);
        profile.AccountManagementMode = ClientAccountManagementModes.SelfManaged;
        await db.SaveChangesAsync();
        Assert.Empty(await service.NavigationForBusinessAsync(selected.Id, actor, "agent@example.org", default));
    }

    [Fact]
    public async Task QuickFindWebsitePermissionTracksAuthorizedMembershipAndRevocation()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var business = new CommerceBusiness { Key = "navigation" };
        var profile = new ClientProfile { ClientUserId = Guid.NewGuid().ToString(), Email = "owner@example.org" };
        var member = new CommerceBusinessMember { CommerceBusinessId = business.Id, ClientProfileId = profile.Id };
        db.AddRange(business, profile, member, new CommerceBusinessStorefrontSettings { CommerceBusinessId = business.Id });
        await db.SaveChangesAsync();
        var service = new BusinessWorkspaceService(db, Mock.Of<IAnalyticsQueryService>(), new(db, new ConfigurationBuilder().Build()));
        var own = await service.NavigationAsync(profile.Id, profile.ClientUserId, profile.Email, default);
        Assert.True(Assert.Single(own).CanWebsite);
        Assert.Empty(await service.NavigationAsync(profile.Id, "unrelated-actor", "stranger@example.org", default));
        member.CanManageStorefront = false;
        member.RoleKey = "member";
        await db.SaveChangesAsync();
        var crmOnly = Assert.Single(await service.NavigationAsync(profile.Id, profile.ClientUserId, profile.Email, default));
        Assert.True(crmOnly.CanCrm);
        Assert.False(crmOnly.CanWebsite);
        Assert.False(await Infrastructure.WebsiteEditing.WebsiteBusinessAccess.CanManageAsActorAsync(db, business.Id,
            profile.Id, profile.ClientUserId, profile.Email));
        member.Status = "Inactive";
        await db.SaveChangesAsync();
        Assert.Empty(await service.NavigationAsync(profile.Id, profile.ClientUserId, profile.Email, default));
    }

    [Fact]
    public async Task SharedWebsiteHandoffStoresOnlyHashAndExactActorBusinessScope()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var profile = Guid.NewGuid();
        var business = Guid.NewGuid();
        var handoff = await Infrastructure.WebsiteEditing.WebsiteEditorHandoffService.CreateAsync(
            db, profile, business, "actor", "ACTOR@example.org");
        var saved = await db.ClientIdentityContinuations.SingleAsync();
        Assert.Equal(business, saved.CommerceBusinessId);
        Assert.Equal(profile, saved.ClientProfileId);
        Assert.Equal("actor", saved.ActorUserId);
        Assert.Equal("actor@example.org", saved.ActorEmail);
        Assert.Equal(Infrastructure.WebsiteEditing.WebsiteEditorHandoffToken.Hash(handoff.OpaqueState), saved.TokenHash);
        Assert.NotEqual(handoff.OpaqueState, saved.TokenHash);
        Assert.Null(saved.ConsumedUtc);
        Assert.InRange(handoff.ExpiresUtc - saved.CreatedUtc, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2));
    }

    [Fact]
    public async Task SeparatePagesUseCanonicalViewsAndRejectOtherBusinessAndRelationshipContacts()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var business = new CommerceBusiness { Key = "scoped", DisplayName = "Scoped business" };
        var other = new CommerceBusiness { Key = "other" };
        db.AddRange(business, other, new CommerceBusinessStorefrontSettings { CommerceBusinessId = business.Id },
            new WorkstationLeadProfile { LeadId = "lead", CommerceBusinessId = business.Id, AgentUserId = "", CrmStatus = "Lead" },
            new WorkstationLeadProfile { LeadId = "client", CommerceBusinessId = business.Id, AgentUserId = "", CrmStatus = "Client" },
            new WorkstationLeadProfile { LeadId = "foreign", CommerceBusinessId = other.Id, AgentUserId = "", CrmStatus = "Client" });
        await db.SaveChangesAsync();
        var service = new BusinessWorkspaceService(db, Mock.Of<IAnalyticsQueryService>(), new(db, new ConfigurationBuilder().Build()));
        var controller = new PageController(service, business);
        var clients = Assert.IsType<Microsoft.AspNetCore.Mvc.ViewResult>(await controller.Clients(business.Id, null));
        Assert.Equal("~/Views/Clients/Index.cshtml", clients.ViewName);
        Assert.Equal("client", Assert.Single(Assert.IsType<System.Collections.Generic.List<AgentPortal.Models.ClientListItemViewModel>>(clients.Model)).ClientUserId);
        var leads = Assert.IsType<Microsoft.AspNetCore.Mvc.ViewResult>(await controller.Leads(business.Id, null));
        Assert.Equal("~/Views/Leads/Index.cshtml", leads.ViewName);
        Assert.Equal("lead", Assert.Single(Assert.IsType<System.Collections.Generic.List<AgentPortal.Models.ClientListItemViewModel>>(leads.Model)).ClientUserId);
        Assert.IsType<Microsoft.AspNetCore.Mvc.NotFoundResult>(await controller.Clients(business.Id, "foreign"));
        Assert.IsType<Microsoft.AspNetCore.Mvc.NotFoundResult>(await controller.Clients(business.Id, "lead"));
        Assert.IsType<Microsoft.AspNetCore.Mvc.ForbidResult>(await controller.Leads(other.Id, null));
        var legacy = Assert.IsType<Microsoft.AspNetCore.Mvc.RedirectToActionResult>(await controller.Crm(business.Id, null, "Client"));
        Assert.Equal(nameof(BusinessWorkspaceControllerBase.Clients), legacy.ActionName);
        Assert.Equal(business.Id, legacy.RouteValues!["businessId"]);
    }

    [Fact]
    public void SharedCrmViewsKeepBusinessRenderingInsideTheBusinessRouteContract()
    {
        var clientsIndex = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "business-crm-clients-index.cshtml"));
        var clientsPipeline = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "business-crm-clients-pipeline.cshtml"));
        var leadsIndex = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "business-crm-leads-index.cshtml"));
        var leadsPipeline = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "business-crm-leads-pipeline.cshtml"));

        Assert.Contains("var isBusinessWorkspace = businessWorkspace is not null;", clientsIndex, StringComparison.Ordinal);
        Assert.Contains("var isBusinessWorkspace = businessWorkspace is not null;", leadsIndex, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"@Url.Action(\"Edit\", \"Clients\"", clientsIndex, StringComparison.Ordinal);

        foreach (var pipeline in new[] { clientsPipeline, leadsPipeline })
        {
            Assert.Contains("action=\"@listUrl\"", pipeline, StringComparison.Ordinal);
            Assert.Contains("href=\"@listUrl\"", pipeline, StringComparison.Ordinal);
            Assert.DoesNotContain("asp-action=\"Index\"", pipeline, StringComparison.Ordinal);
            Assert.Contains("ViewData[\"BusinessWorkspace\"] as Shared.Crm.BusinessWorkspaceModel", pipeline, StringComparison.Ordinal);
        }

        var archive = clientsIndex.IndexOf("asp-action=\"Archive\"", StringComparison.Ordinal);
        var archiveGuard = clientsIndex.LastIndexOf("@if (!isBusinessWorkspace)", archive, StringComparison.Ordinal);
        Assert.True(archive >= 0 && archiveGuard >= 0 && archive - archiveGuard < 180);

        var clientModal = clientsIndex.IndexOf("id=\"clientProductionModal\"", StringComparison.Ordinal);
        var clientModalGuard = clientsIndex.LastIndexOf("@if (!isBusinessWorkspace)", clientModal, StringComparison.Ordinal);
        Assert.True(clientModal >= 0 && clientModalGuard >= 0 && clientModal - clientModalGuard < 180);

        var leadModal = leadsIndex.IndexOf("id=\"productionModal\"", StringComparison.Ordinal);
        var leadModalGuard = leadsIndex.LastIndexOf("@if (!isBusinessWorkspace)", leadModal, StringComparison.Ordinal);
        Assert.True(leadModal >= 0 && leadModalGuard >= 0 && leadModal - leadModalGuard < 180);
    }

    [Fact]
    public async Task ClientAppBusinessClientViewRendersUnderBusinessWorkspaceContract()
    {
        using var host = await BuildClientAppRazorHostAsync();
        var html = await RenderBusinessCrmViewAsync(host.Services, "~/Views/Clients/Index.cshtml", "Client");
        Assert.Contains("id=\"legendWrap\"", html, StringComparison.Ordinal);
        Assert.Contains("/clients", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClientAppBusinessLeadViewRendersUnderBusinessWorkspaceContract()
    {
        using var host = await BuildClientAppRazorHostAsync();
        var html = await RenderBusinessCrmViewAsync(host.Services, "~/Views/Leads/Index.cshtml", "Lead");
        Assert.Contains("id=\"legendWrap\"", html, StringComparison.Ordinal);
        Assert.Contains("/leads", html, StringComparison.Ordinal);
    }

    private static Task<IHost> BuildClientAppRazorHostAsync()
    {
        return new HostBuilder()
            .ConfigureWebHost(webBuilder => webBuilder
                .UseEnvironment("Testing")
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddDataProtection();
                    services.AddControllersWithViews()
                        .AddApplicationPart(typeof(ClientApp.Controllers.HomeController).Assembly)
                        .AddApplicationPart(Assembly.Load("ClientApp.Views"));
                })
                .Configure(_ => { }))
            .StartAsync();
    }

    private static async Task<string> RenderBusinessCrmViewAsync(IServiceProvider services, string viewPath, string kind)
    {
        using var scope = services.CreateScope();
        var scoped = scope.ServiceProvider;
        var viewEngine = scoped.GetRequiredService<IRazorViewEngine>();
        var metadata = scoped.GetRequiredService<IModelMetadataProvider>();
        var tempDataProvider = scoped.GetRequiredService<ITempDataProvider>();
        var businessId = Guid.NewGuid();

        var http = new DefaultHttpContext { RequestServices = scoped };
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("client.test");
        http.Request.Path = kind == "Client"
            ? $"/business/{businessId}/clients"
            : $"/business/{businessId}/leads";

        var routeData = new RouteData();
        routeData.Values["controller"] = "BusinessWorkspace";
        routeData.Values["action"] = kind == "Client" ? "Clients" : "Leads";
        routeData.Values["businessId"] = businessId;

        var actionContext = new ActionContext(http, routeData, new ActionDescriptor());
        var found = viewEngine.GetView(null, viewPath, isMainPage: false);
        Assert.True(found.Success, $"View {viewPath} was not found. Searched: {string.Join(", ", found.SearchedLocations ?? Array.Empty<string>())}");

        var model = new System.Collections.Generic.List<AgentPortal.Models.ClientListItemViewModel>
        {
            new()
            {
                ClientUserId = kind.ToLowerInvariant() + "-render-probe",
                FirstName = "Render",
                LastName = "Probe",
                Email = "render@example.test",
                Phone = "5555550100",
                CrmStatus = kind,
                RecordType = kind,
                PipelineStage = "New",
                StageEnteredUtc = DateTime.UtcNow
            }
        };
        var viewData = new ViewDataDictionary<System.Collections.Generic.List<AgentPortal.Models.ClientListItemViewModel>>(
            metadata, new ModelStateDictionary())
        {
            Model = model
        };
        viewData["BusinessWorkspace"] = new BusinessWorkspaceModel
        {
            BusinessId = businessId,
            BusinessName = "Render Test Business",
            Kind = kind,
            Total = model.Count,
            Preferences = new BusinessWorkspacePreferences()
        };
        viewData["Search"] = "";
        viewData["TotalClients"] = model.Count;
        viewData["ClientPortalBaseUrl"] = "https://client.test";
        viewData["CanSetFounderSubscriptionOptions"] = false;
        viewData["ProductionTotals"] = new AgentPortal.Services.ProductionTotals();

        var tempData = new TempDataDictionary(http, tempDataProvider);
        using var writer = new StringWriter();
        var viewContext = new ViewContext(actionContext, found.View, viewData, tempData, writer, new HtmlHelperOptions());
        await found.View.RenderAsync(viewContext);
        return writer.ToString();
    }

    [Fact]
    public async Task BusinessBookingResolvesOnlyExistingAttachedAgentAndBusinessContact()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var business = new CommerceBusiness { Key = "booking-business", DisplayName = "Booking Business" };
        var otherBusiness = new CommerceBusiness { Key = "booking-other", DisplayName = "Other Business" };
        var memberProfile = new ClientProfile
        {
            ClientUserId = Guid.NewGuid().ToString(),
            Email = "owner@example.org"
        };
        var agent = new AgentProfile
        {
            AgentUserId = "booking-agent",
            AgentUpn = "booking-agent@mylegnd.com",
            BookingEnabled = true,
            BookingPageIdOrMailbox = "booking-business-id",
            CalendarEmail = "booking-agent@mylegnd.com"
        };
        var unassignedAgent = new AgentProfile
        {
            AgentUserId = "unassigned-agent",
            AgentUpn = "unassigned@mylegnd.com",
            BookingEnabled = true,
            BookingPageIdOrMailbox = "other-booking-id"
        };
        var contact = new WorkstationLeadProfile
        {
            LeadId = "business-booking-contact",
            CommerceBusinessId = business.Id,
            AgentUserId = "",
            FirstName = "Business",
            LastName = "Contact",
            CrmStatus = "Lead"
        };
        var foreignContact = new WorkstationLeadProfile
        {
            LeadId = "foreign-booking-contact",
            CommerceBusinessId = otherBusiness.Id,
            AgentUserId = "",
            CrmStatus = "Lead"
        };

        db.AddRange(
            business,
            otherBusiness,
            memberProfile,
            agent,
            unassignedAgent,
            new CommerceBusinessMember
            {
                CommerceBusinessId = business.Id,
                ClientProfileId = memberProfile.Id,
                Status = "Active"
            },
            new AgentClient
            {
                AgentUserId = agent.AgentUserId,
                AgentUpn = agent.AgentUpn,
                ClientUserId = memberProfile.ClientUserId
            },
            contact,
            foreignContact);
        await db.SaveChangesAsync();

        var resolvedAgent = await BusinessBookingAccess.ResolveAttachedAgentAsync(
            db, business.Id, agent.Id, default);
        Assert.NotNull(resolvedAgent);
        Assert.Equal(agent.Id, resolvedAgent!.Id);
        Assert.Null(await BusinessBookingAccess.ResolveAttachedAgentAsync(
            db, business.Id, unassignedAgent.Id, default));
        Assert.Null(await BusinessBookingAccess.ResolveAttachedAgentAsync(
            db, otherBusiness.Id, agent.Id, default));

        Assert.Equal(contact.LeadId,
            (await BusinessBookingAccess.ResolveBusinessContactAsync(
                db, business.Id, contact.LeadId, default))!.LeadId);
        Assert.Null(await BusinessBookingAccess.ResolveBusinessContactAsync(
            db, business.Id, foreignContact.LeadId, default));
    }

    [Fact]
    public void BusinessBookingKeepsAgentPortalAsSingleSchedulerAndSingleUiSource()
    {
        string Read(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file));

        var clientProject = Read("business-booking-clientapp.csproj");
        var clientLayout = Read("business-booking-client-layout.cshtml");
        var clientController = Read("business-booking-client-controller.cs");
        var internalController = Read("business-booking-internal-controller.cs");
        var calendar = Read("business-booking-calendar-controller.cs");
        var clientsScript = Read("business-booking-clients-index.js");
        var leadsScript = Read("business-booking-leads-index.js");
        var scheduler = Read("business-booking-qv.js");
        var schedulerCss = Read("business-booking-qv.css");
        var mobileBookingView = Read("business-booking-mobile-view.cshtml");
        var agentProgram = Read("business-booking-agent-program.cs");
        var clientProgram = Read("business-booking-client-program.cs");

        Assert.Contains("../AgentPortal/wwwroot/css/qv-booking.css", clientProject, StringComparison.Ordinal);
        Assert.Contains("~/css/qv-booking.css", clientLayout, StringComparison.Ordinal);
        Assert.Equal(1, clientProject.Split("../AgentPortal/wwwroot/css/qv-booking.css", StringSplitOptions.None).Length - 1);
        Assert.Equal(1, clientLayout.Split("~/css/qv-booking.css", StringSplitOptions.None).Length - 1);

        foreach (var script in new[] { clientsScript, leadsScript })
        {
            Assert.Contains("function crmCalendarRoute(path)", script, StringComparison.Ordinal);
            Assert.Contains("crmRoute(`/Booking/", script, StringComparison.Ordinal);
            Assert.Contains("fetchStatus(url, init)", script, StringComparison.Ordinal);
            Assert.Contains("fetchAvailability(url, options)", script, StringComparison.Ordinal);
            Assert.Contains("postJson(crmCalendarRoute(url), payload)", script, StringComparison.Ordinal);
        }

        Assert.Contains("options.fetchStatus", scheduler, StringComparison.Ordinal);
        Assert.Contains("options.fetchAvailability", scheduler, StringComparison.Ordinal);
        Assert.Contains("statusTransport(", scheduler, StringComparison.Ordinal);
        Assert.Contains("busyAvailabilityTransport(", scheduler, StringComparison.Ordinal);

        Assert.Contains("BusinessBookingTicketProtector", clientController, StringComparison.Ordinal);
        Assert.Contains("AgentPortalBusinessBooking", clientController, StringComparison.Ordinal);
        Assert.DoesNotContain("GraphServiceClient", clientController, StringComparison.Ordinal);
        Assert.DoesNotContain("BookingAppointment", clientController, StringComparison.Ordinal);
        Assert.DoesNotContain("BookingBusinesses", clientController, StringComparison.Ordinal);

        Assert.Contains("CalendarController calendar", internalController, StringComparison.Ordinal);
        Assert.Contains("calendar.DayAvailability", internalController, StringComparison.Ordinal);
        Assert.Contains("calendar.CreateEvent", internalController, StringComparison.Ordinal);
        Assert.Contains("calendar.UpdateAppointment", internalController, StringComparison.Ordinal);
        Assert.Contains("calendar.CancelAppointment", internalController, StringComparison.Ordinal);
        Assert.DoesNotContain("new BookingAppointment", internalController, StringComparison.Ordinal);
        Assert.DoesNotContain("GraphServiceClient", internalController, StringComparison.Ordinal);

        Assert.Contains("JsonIgnore] public Guid? ScopedBusinessId", calendar, StringComparison.Ordinal);
        Assert.Contains("x.CommerceBusinessId == scopedBusinessId", calendar, StringComparison.Ordinal);
        Assert.Contains("x.CommerceBusinessId == req.ScopedBusinessId", calendar, StringComparison.Ordinal);
        Assert.Contains("BusinessBookingTicketProtector.CreateShared", agentProgram, StringComparison.Ordinal);
        Assert.Contains("BusinessBookingTicketProtector.CreateShared", clientProgram, StringComparison.Ordinal);
        Assert.Contains("AddHttpClient(\"AgentPortalBusinessBooking\"", clientProgram, StringComparison.Ordinal);

        Assert.Contains("CANONICAL RESPONSIVE GEOMETRY", schedulerCss, StringComparison.Ordinal);
        Assert.True(
            schedulerCss.IndexOf("CANONICAL RESPONSIVE GEOMETRY", StringComparison.Ordinal) >
            schedulerCss.IndexOf("VIEWPORT FIT SYSTEM", StringComparison.Ordinal));
        Assert.Equal(1, schedulerCss.Split("@media (max-width:1100px)", StringSplitOptions.None).Length - 1);
        Assert.Equal(1, schedulerCss.Split("@media (max-width:760px)", StringSplitOptions.None).Length - 1);
        Assert.Contains("min-height:100dvh", schedulerCss, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns:repeat(2, minmax(0,1fr))", schedulerCss, StringComparison.Ordinal);
        Assert.Contains(".mobile-client-booking .qv-booking-close", schedulerCss, StringComparison.Ordinal);
        Assert.Contains("~/css/qv-booking.css", mobileBookingView, StringComparison.Ordinal);
        Assert.DoesNotContain("mobile-client-booking.css", mobileBookingView, StringComparison.Ordinal);
    }

    private sealed class PageController(BusinessWorkspaceService service, CommerceBusiness business)
        : BusinessWorkspaceControllerBase(service)
    {
        protected override Task<CommerceBusiness?> ResolveBusinessAsync(Guid id, string capability, CancellationToken ct) =>
            Task.FromResult<CommerceBusiness?>(id == business.Id ? business : null);
    }

    [Fact]
    public async Task CanonicalAnalyticsAdapterAlwaysUsesPermanentBusinessScope()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var businessId = Guid.NewGuid();
        var range = TimeRangeRequest.FromPreset("7d", null, null, TimeZoneInfo.Utc);
        var analytics = new Mock<IAnalyticsQueryService>(MockBehavior.Strict);
        var summary = new SummaryKpiDto();
        analytics.Setup(x => x.GetSummaryAsync(range,
            It.Is<ScopeContext>(scope => scope.ScopeType == ScopeType.Business && scope.CommerceBusinessId == businessId && scope.AgentTrackingProfileId == null),
            TrafficType.All)).ReturnsAsync(summary);
        var service = new BusinessWorkspaceService(db, analytics.Object, new(db, new ConfigurationBuilder().Build()));
        Assert.Same(summary, await service.AnalyticsDataAsync(businessId, "summary", range, TrafficType.All));
        Assert.Null(await service.AnalyticsDataAsync(businessId, "unregistered-agent-action", range, TrafficType.All));
        await Assert.ThrowsAsync<ArgumentException>(() => service.AnalyticsDataAsync(Guid.Empty, "summary", range, TrafficType.All));
        analytics.VerifyAll();
    }

    [Fact]
    public async Task PreferencesAndContactsStayBusinessScopedAndCustomStageSurvivesCapture()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var a = new CommerceBusiness { Key = "plumbing", DisplayName = "Plumbing" };
        var b = new CommerceBusiness { Key = "bakery", DisplayName = "Bakery" };
        db.AddRange(a, b);
        db.AddRange(new CommerceBusinessStorefrontSettings { CommerceBusinessId = a.Id }, new CommerceBusinessStorefrontSettings { CommerceBusinessId = b.Id });
        await db.SaveChangesAsync();
        var service = new BusinessWorkspaceService(db, Mock.Of<IAnalyticsQueryService>(), new(db, new ConfigurationBuilder().Build()));
        var settings = await service.CustomizeAsync(a, default);
        await service.CustomizeAsync(a.Id, new() { Revision = settings.SettingsRevision, LeadLabel = "Requests", ClientLabel = "Customers", Stages = "Received\nScheduled\nCompleted", Metrics = ["leads", "sessions"] }, default);
        var lead = new WebsiteLead { LeadId = Guid.NewGuid(), CommerceBusinessId = a.Id, FirstName = "Visitor", Email = "visitor@example.org" };
        db.Add(lead); await db.SaveChangesAsync();
        var capture = new WebsiteLifeLeadCaptureService(db, NullLogger<WebsiteLifeLeadCaptureService>.Instance);
        var result = await capture.UpsertAsync(new() { WebsiteLeadId = lead.LeadId });
        Assert.True(result.Captured);
        var crm = await service.CrmAsync(a, "Lead", null, 1, result.WorkstationLeadId, default);
        Assert.Equal("Received", crm.Selected!.Stage);
        Assert.Equal("Requests", crm.Preferences.LeadLabel);
        Assert.Empty((await service.CrmAsync(b, "Lead", null, 1, result.WorkstationLeadId, default)).Contacts);
        Assert.Null((await service.CrmAsync(b, "Lead", null, 1, result.WorkstationLeadId, default)).Selected);
        Assert.Equal("Leads", (await service.CustomizeAsync(b, default)).Preferences.LeadLabel);
        Assert.False(await service.UpdateAsync(b.Id, result.WorkstationLeadId!, new() { Kind = "Lead", Stage = "New" }, "actor", default));
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.CustomizeAsync(a.Id, new() { Revision = settings.SettingsRevision }, default));
    }

    [Fact]
    public async Task RecipientRevocationNeverFallsBackToFounderOrAnotherBusiness()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var business = new CommerceBusiness { Key = "business", OwnerEmail = "owner@example.org" };
        var profile = new ClientProfile { ClientUserId = Guid.NewGuid().ToString(), Email = business.OwnerEmail };
        var member = new CommerceBusinessMember { CommerceBusinessId = business.Id, ClientProfileId = profile.Id, DisplayName = "Owner" };
        var settings = new CommerceBusinessStorefrontSettings { CommerceBusinessId = business.Id, WorkspacePreferencesJson = new BusinessWorkspacePreferences { NotificationMemberId = member.Id }.Write() };
        db.AddRange(business, profile, member, settings); await db.SaveChangesAsync();
        var resolver = new WebsiteIntakeRecipientResolver(db, new ConfigurationBuilder().AddInMemoryCollection(new[] { new System.Collections.Generic.KeyValuePair<string,string?>("Contact:RecipientEmail", "founder@example.org") }).Build());
        Assert.Equal(profile.Email, await resolver.ResolveAsync(MarketingOwnerScope.Business(business.Id)));
        member.Status = "Inactive"; await db.SaveChangesAsync();
        Assert.Null(await resolver.ResolveAsync(MarketingOwnerScope.Business(business.Id)));
        Assert.Null(await resolver.ResolveAsync(MarketingOwnerScope.Business(Guid.NewGuid())));
    }

    [Fact]
    public async Task InquiryDeliveryUsesOwnerAndRetriesOnlyUntilAccepted()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var business = new CommerceBusiness { Key = "business", OwnerEmail = "owner@example.org" };
        var profile = new ClientProfile { ClientUserId = Guid.NewGuid().ToString(), Email = business.OwnerEmail };
        var row = new CommerceWebsiteInquiry { CommerceBusinessId = business.Id, Email = "visitor@example.org", Message = "<script>unsafe</script>" };
        db.AddRange(business, profile, row, new CommerceBusinessStorefrontSettings { CommerceBusinessId = business.Id }, new CommerceBusinessMember { CommerceBusinessId = business.Id, ClientProfileId = profile.Id });
        await db.SaveChangesAsync();
        var sender = new Mock<IProtectEmailSender>();
        sender.Setup(x => x.TrySendAsync(business.OwnerEmail, It.IsAny<string>(), It.Is<string>(s => s.Contains("&lt;script&gt;") && !s.Contains("<script>")), null, row.Email, false, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var service = new BusinessInquiryNotificationService(db, new(db, new ConfigurationBuilder().Build()), sender.Object);
        await service.DeliverPendingAsync(default);
        await service.DeliverPendingAsync(default);
        Assert.Equal("Sent", row.NotificationStatus);
        Assert.NotNull(row.NotificationSentUtc);
        sender.Verify(x => x.TrySendAsync(business.OwnerEmail, It.IsAny<string>(), It.IsAny<string>(), null, row.Email, false, It.IsAny<CancellationToken>()), Times.Once);
    }
}
