using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Controllers;
using AgentPortal.Models;
using AgentPortal.Services;
using Domain.Billing;
using Domain.Entities;
using Infrastructure.Billing;
using Infrastructure.Crm;
using Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed partial class CrmHistoryTests
{
    [Theory]
    [InlineData("workstation",false)]
    [InlineData("client-lead",false)]
    [InlineData("portal-access",false)]
    [InlineData("workstation",true)]
    [InlineData("client-lead",true)]
    public async Task ConversionHandlersPreserveHistoryAndRollbackFailedCreation(string route,bool failBeforeCommit)
    {
        await using var connection=new SqliteConnection("Data Source=:memory:");await connection.OpenAsync();
        await using var db=new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        const string owner="synthetic-owner", oldUser="lead-synthetic-conversion", leadId="synthetic-conversion";
        var native=route!="workstation";
        var profile=new ClientProfile { ClientUserId=oldUser,FirstName="Synthetic",LastName="Applicant",Email="synthetic@example.test",NormalizedEmail="synthetic@example.test",CrmNotes=ClientCrmMetaSerializer.Serialize(new ClientCrmMeta { RecordType="Lead",PipelineStage="Qualified" }) };
        if(native){db.Add(profile);db.Add(new AgentClient { AgentUserId=owner,ClientUserId=oldUser,CreatedUtc=DateTime.UtcNow });}
        else db.Add(new WorkstationLeadProfile { LeadId=leadId,AgentUserId=owner,FirstName="Synthetic",LastName="Applicant",Email="synthetic@example.test" });
        var source=new WebsiteLead { LeadId=Guid.NewGuid(),FirstName="Synthetic",Email="synthetic@example.test",CreatedUtc=new DateTime(2026,10,1,0,0,0,DateTimeKind.Utc) };
        db.Add(source);await db.SaveChangesAsync();
        var intake=new WebsiteLeadIntakeLink { Id=Guid.NewGuid(),WebsiteLeadRowId=source.Id,WebsiteLeadPublicId=source.LeadId,
            WorkstationLeadId=native?null:leadId,HistoryClientProfileId=native?profile.Id:null,AgentUserId=owner,
            SubmittedUtc=source.CreatedUtc,CapturedUtc=source.CreatedUtc,SnapshotJson="{\"syntheticAnswer\":false,\"disclosure\":\"Original synthetic wording\"}" };
        db.Add(intake);var store=new CrmHistoryStore(db);var tile=await store.StageSubmissionAsync(intake);await db.SaveChangesAsync();
        var originalId=tile.Id;var originalTime=tile.OccurredUtc;var originalContent=tile.ContentJson;var key=tile.RelationshipKey;
        var beforeIds=await db.Set<CrmHistoryEntry>().Select(x=>x.Id).ToArrayAsync();
        IBillingOrchestrator billing;
        Mock<IBillingOrchestrator>? failedBilling=null;
        if(failBeforeCommit)
        {
            var failed=new Mock<IBillingOrchestrator>();failedBilling=failed;
            failed.Setup(x=>x.CreateClientSubscriptionOfferAsync(It.IsAny<CreateClientSubscriptionOfferCommand>(),It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("Synthetic failure before conversion commit"));
            billing=failed.Object;
        }
        else
        {
            var gateway=new Mock<IBillingGateway>(MockBehavior.Strict);gateway.SetupGet(x=>x.Provider).Returns(BillingProvider.Square);gateway.SetupGet(x=>x.Environment).Returns(BillingProviderEnvironment.Sandbox);
            billing=new MasterAppBillingOrchestrator(db,gateway.Object,new BillingEntitlementService(db,ControllerTestHelpers.BuildHouseholdMembershipService(db)),Mock.Of<IClientSubscriptionActivationPolicyService>());
        }
        var controller=ControllerTestHelpers.BuildClientsController(db,Mock.Of<IExecutionEngine>(),Mock.Of<ICommitmentService>(),ControllerTestHelpers.BuildUser(owner,"synthetic@example.test"),billingOrchestrator:billing);
        controller.Url=Mock.Of<IUrlHelper>();using var services=new ServiceCollection().BuildServiceProvider();controller.HttpContext.RequestServices=services;
        IActionResult result=route=="portal-access" ? await controller.EnablePortalAccess(new ClientsController.EnablePortalAccessRequest { ClientUserId=oldUser,RecordType="Client" }) :
            await controller.Create(new CreateClientViewModel { RecordType="Client",AccountManagementMode="SharedAccount",FirstName="Synthetic",LastName="Applicant",Email="synthetic@example.test",
                SourceWorkstationLeadId=native?null:leadId,SourceLeadClientUserId=native?oldUser:null,
                SubscriptionPriceType=nameof(ClientSubscriptionOfferPriceType.Fixed100),SubscriptionBillingAnchorMode=nameof(BillingAnchorSelectionMode.FirstOfMonth) });
        db.ChangeTracker.Clear();
        if(failBeforeCommit)
        {
            Assert.IsType<ViewResult>(result);
            failedBilling!.Verify(x=>x.CreateClientSubscriptionOfferAsync(It.IsAny<CreateClientSubscriptionOfferCommand>(),It.IsAny<CancellationToken>()),Times.Once);
            Assert.Equal(beforeIds.Order(),(await db.Set<CrmHistoryEntry>().Select(x=>x.Id).ToArrayAsync()).Order());
            if(native){Assert.Equal(oldUser,(await db.ClientProfiles.SingleAsync()).ClientUserId);Assert.Equal(oldUser,(await db.AgentClients.SingleAsync()).ClientUserId);}
            else {Assert.Empty(await db.ClientProfiles.ToArrayAsync());Assert.Empty(await db.AgentClients.ToArrayAsync());}
        }
        else
        {
            if(route=="portal-access")
            {
                var response=Assert.IsType<JsonResult>(result);
                using var json=System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(response.Value));
                Assert.True(json.RootElement.GetProperty("portalAccessEnabled").GetBoolean());
            }
            else Assert.IsType<RedirectToActionResult>(result);
            var converted=await db.ClientProfiles.SingleAsync();Assert.True(Guid.TryParse(converted.ClientUserId,out _));
            Assert.Equal(key,await store.ResolveClientKeyAsync(converted));
            Assert.Equal(converted.ClientUserId,(await db.AgentClients.SingleAsync()).ClientUserId);
            var history=await db.Set<CrmHistoryEntry>().Where(x=>x.RelationshipKey==key).ToArrayAsync();
            Assert.Single(history.Where(x=>x.Type=="Conversion"));
            Assert.IsType<OkObjectResult>(await BuildController(db,owner).Entry(originalId,"profile",converted.Id.ToString()));
            Assert.IsType<NotFoundResult>(await BuildController(db,"other-owner").Entry(originalId,"profile",converted.Id.ToString()));
        }
        var retained=await db.Set<CrmHistoryEntry>().AsNoTracking().SingleAsync(x=>x.Id==originalId);
        Assert.Equal(originalTime,retained.OccurredUtc);Assert.Equal(originalContent,retained.ContentJson);
        Assert.Equal(intake.SnapshotJson,(await db.WebsiteLeadIntakeLinks.AsNoTracking().SingleAsync()).SnapshotJson);
    }
}
