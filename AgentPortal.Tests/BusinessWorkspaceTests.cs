using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
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
