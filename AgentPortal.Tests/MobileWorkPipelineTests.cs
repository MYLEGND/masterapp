using System;
using System.Linq;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Mobile;
using AgentPortal.Services;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Analytics;
using Infrastructure.Businesses;
using Infrastructure.Data;
using Infrastructure.Leads;
using Infrastructure.Mobile;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed class MobileWorkPipelineTests
{
    [Theory]
    [InlineData("Fulfilled", "Paid", "None", "completed")]
    [InlineData("Fulfilled", "Paid", "Requested", "active")]
    [InlineData("Processing", "Refunded", "None", "closed")]
    [InlineData("Cancelled", "Paid", "None", "closed")]
    [InlineData("Shipped", "Paid", "None", "active")]
    public void WorkGroupingDoesNotConfuseShippingOrReturnsWithCompletion(string status, string payment, string returns, string expected) =>
        Assert.Equal(expected, WorkPipelinePresentation.OrderBucket(status, payment, returns));

    [Fact]
    public async Task InquiryStatusIsScopedAndRejectsStaleSaveAtomically()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:;Foreign Keys=False");
        await connection.OpenAsync();
        await using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var row = new CommerceWebsiteInquiry { CommerceBusinessId = Guid.NewGuid(), Name = "Synthetic inquiry" };
        db.Add(row); await db.SaveChangesAsync();
        Assert.IsType<NotFoundResult>(await WebsiteInquiryAuthority.UpdateStatusAsync(db, Guid.NewGuid(), row.Id, "Closed", "New", default));
        Assert.IsType<OkObjectResult>(await WebsiteInquiryAuthority.UpdateStatusAsync(db, row.CommerceBusinessId, row.Id, "Contacted", "New", default));
        Assert.IsType<ConflictObjectResult>(await WebsiteInquiryAuthority.UpdateStatusAsync(db, row.CommerceBusinessId, row.Id, "Closed", "New", default));
        Assert.Equal("Contacted", await db.Set<CommerceWebsiteInquiry>().AsNoTracking().Select(x => x.Status).SingleAsync());
    }

    [Fact]
    public async Task BusinessWorkUsesConfiguredStagesRevisionAndExistingContactHistory()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = new ClientProfile { ClientUserId = "pipeline-owner", Email = "synthetic@example.invalid" };
        var business = new CommerceBusiness { Key = "pipeline-business", DisplayName = "Synthetic business" };
        var contact = new WorkstationLeadProfile { LeadId = "pipeline-contact", CommerceBusinessId = business.Id,
            AgentUserId = "", FirstName = "Synthetic", CrmStage = "New", UpdatedUtc = DateTime.UtcNow };
        db.AddRange(profile, business, contact, new CommerceBusinessMember { CommerceBusinessId = business.Id, ClientProfileId = profile.Id, CanManageCrm = true },
            new CommerceBusinessStorefrontSettings { CommerceBusinessId = business.Id });
        await db.SaveChangesAsync();
        var workspace = new BusinessWorkspaceService(db, Mock.Of<IAnalyticsQueryService>());
        using var services = new ServiceCollection().AddSingleton(workspace).BuildServiceProvider();
        var controller = Controller(db, profile.ClientUserId, profile.Id, services);
        var result = Assert.IsType<OkObjectResult>(await controller.List(default));
        var row = Assert.Single(Assert.IsAssignableFrom<IEnumerable<MobileWorkLeadsController.WorkLead>>(result.Value));
        Assert.Equal("business", row.Kind); Assert.Contains("Won", row.AllowedStages);
        var input = new MobileWorkLeadsController.StageInput(row.Kind, row.RecordId, row.BusinessId, row.ProfileId,
            "Won", row.Stage, "Synthetic follow-up", row.UpdatedUtc, row.Revision);
        Assert.IsType<ForbidResult>(await controller.Stage(input with { BusinessId = Guid.NewGuid() }, default));
        Assert.IsType<BadRequestObjectResult>(await controller.Stage(input with { Stage = "invented" }, default));
        Assert.IsType<OkObjectResult>(await controller.Stage(input, default));
        Assert.Equal("Won", contact.CrmStage);
        Assert.Contains("Synthetic follow-up", contact.CrmNotes);
        Assert.IsType<ConflictObjectResult>(await controller.Stage(input with { Stage = "Closed" }, default));
        var fresh = Assert.Single(Assert.IsAssignableFrom<IEnumerable<MobileWorkLeadsController.WorkLead>>((await controller.List(default) as OkObjectResult)!.Value));
        Assert.Equal("completed", fresh.Bucket); Assert.Equal("success", fresh.Tone);
    }

    [Fact]
    public async Task HistoryAndNotificationTargetRemainMembershipScopedAndPaginated()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = new ClientProfile { ClientUserId = "history-owner", Email = "synthetic@example.invalid" };
        var business = new CommerceBusiness { Key = "history-business" };
        db.AddRange(profile, business, new CommerceBusinessMember { CommerceBusinessId = business.Id, ClientProfileId = profile.Id, CanManageCrm = true });
        for (var i = 0; i < 101; i++) db.Add(new CommerceOrder { CommerceBusinessId = business.Id,
            OrderNumber = "Synthetic-" + i, FulfillmentStatus = "Fulfilled", PaymentStatus = "Paid", UpdatedUtc = DateTime.UtcNow.AddMinutes(-i) });
        var foreign = new CommerceOrder { CommerceBusinessId = Guid.NewGuid(), OrderNumber = "FOREIGN", FulfillmentStatus = "Fulfilled", PaymentStatus = "Paid" };
        db.Add(foreign); await db.SaveChangesAsync();
        var actor = new MobileResolvedActor(new MessagingActor(profile.ClientUserId, "Client"), profile.Id, "Synthetic");
        var resolver = Resolver(actor);
        var controller = new MobileWorkOrdersController(resolver.Object, db, new ParfaitApp.Services.ParfaitOrderService(db)) {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        object[] Rows(IActionResult result) => Assert.IsAssignableFrom<System.Collections.IEnumerable>(Assert.IsType<OkObjectResult>(result).Value).Cast<object>().ToArray();
        Assert.Empty(Rows(await controller.List(default)));
        Assert.Equal(100, Rows(await controller.List(default, true)).Length);
        Assert.Single(Rows(await controller.List(default, true, 100)));
        var older = await db.CommerceOrders.Where(x => x.CommerceBusinessId == business.Id).OrderBy(x => x.UpdatedUtc).FirstAsync();
        var notice = Infrastructure.Commerce.CommerceOrderAppNotificationService.NotificationId(older.Id, actor.Actor);
        Assert.Single(Rows(await controller.List(default, true, 0, notice)));
        var foreignNotice = Infrastructure.Commerce.CommerceOrderAppNotificationService.NotificationId(foreign.Id, actor.Actor);
        Assert.Empty(Rows(await controller.List(default, true, 0, foreignNotice)));
    }

    [Fact]
    public async Task LinkedInquiryNeverReappearsAsASecondPipelineAcrossPages()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = new ClientProfile { ClientUserId = "paged-owner", Email = "synthetic@example.invalid" };
        var business = new CommerceBusiness { Key = "paged-business" };
        var oldContact = new WorkstationLeadProfile { LeadId = "old-contact", CommerceBusinessId = business.Id,
            AgentUserId = "", CrmStage = "Qualified", UpdatedUtc = DateTime.UtcNow.AddDays(-20) };
        var source = Guid.NewGuid();
        db.AddRange(profile, business, oldContact, new CommerceBusinessMember { CommerceBusinessId = business.Id, ClientProfileId = profile.Id, CanManageCrm = true },
            new CommerceBusinessStorefrontSettings { CommerceBusinessId = business.Id },
            new WebsiteLeadIntakeLink { Id = Guid.NewGuid(), CommerceBusinessId = business.Id, WorkstationLeadId = oldContact.LeadId, WebsiteLeadPublicId = source },
            new CommerceWebsiteInquiry { CommerceBusinessId = business.Id, WebsiteLeadId = source, CreatedUtc = DateTime.UtcNow, Name = "Linked inquiry" });
        for (var index = 0; index < 100; index++) db.Add(new WorkstationLeadProfile { LeadId = "recent-" + index,
            CommerceBusinessId = business.Id, AgentUserId = "", CrmStage = "New", UpdatedUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
        using var services = new ServiceCollection().AddSingleton(new BusinessWorkspaceService(db, Mock.Of<IAnalyticsQueryService>())).BuildServiceProvider();
        var controller = Controller(db, profile.ClientUserId, profile.Id, services);
        IEnumerable<MobileWorkLeadsController.WorkLead> Rows(IActionResult result) =>
            Assert.IsAssignableFrom<IEnumerable<MobileWorkLeadsController.WorkLead>>(Assert.IsType<OkObjectResult>(result).Value);
        var first = Rows(await controller.List(default)).ToArray();
        Assert.Equal(100, first.Length); Assert.DoesNotContain(first, x => x.Kind == "inquiry");
        var second = Assert.Single(Rows(await controller.List(default, 100)));
        Assert.Equal(oldContact.LeadId, second.RecordId); Assert.Equal("Qualified", second.Stage);
        var target = Assert.Single(Rows(await controller.List(default, 0, WebsiteLeadAppNotificationService.NotificationId(source))));
        Assert.Equal(oldContact.LeadId, target.RecordId); Assert.Equal("business", target.Kind);
    }

    [Fact]
    public async Task CanonicalAgentOutcomeRejectsStaleRevisionBeforeChangingLead()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var now = DateTime.UtcNow;
        var row = new WorkstationLeadProfile { LeadId = "stale-agent-lead", AgentUserId = "agent-1", CrmStage = "Qualified", UpdatedUtc = now };
        db.Add(row); await db.SaveChangesAsync();
        var controller = ControllerTestHelpers.BuildLeadsController(db, Mock.Of<IExecutionEngine>(), Mock.Of<ICommitmentService>(), ControllerTestHelpers.BuildUser());
        var result = await controller.ApplyOutcome(new AgentPortal.Controllers.LeadsController.LeadOutcomeRequest(row.LeadId, "Lost", null, now.AddSeconds(-1)));
        Assert.IsType<ConflictObjectResult>(result); Assert.Equal("Qualified", row.CrmStage);
    }

    private static Mock<IMobileActorResolver> Resolver(MobileResolvedActor actor)
    {
        var resolver = new Mock<IMobileActorResolver>();
        resolver.Setup(x => x.ResolveAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MobileActorResolution(true, null, null, [actor], actor, false));
        return resolver;
    }
    private static MobileWorkLeadsController Controller(MasterAppDbContext db, string user, Guid profile, IServiceProvider services) =>
        new(Resolver(new MobileResolvedActor(new MessagingActor(user, "Client"), profile, "Synthetic")).Object, db, new MobileAgentCrmService(db)) {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = services } }
        };
}
