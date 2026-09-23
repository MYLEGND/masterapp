using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Analytics;
using Infrastructure.Businesses;
using Infrastructure.Leads;
using Microsoft.EntityFrameworkCore;
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
