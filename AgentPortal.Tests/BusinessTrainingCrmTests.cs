using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Domain.Billing;
using Domain.Entities;
using Infrastructure.Analytics;
using Infrastructure.Businesses;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace AgentPortal.Tests;
public sealed class BusinessTrainingCrmTests
{
    [Fact]
    public async Task TenantScopedRelationshipsDeduplicateRetriesSearchOlderRowsAndRejectPipelineWrites()
    {
        using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var business = new CommerceBusiness { Key = "training-crm" };
        var foreign = new CommerceBusiness { Key = "other-training" };
        db.AddRange(business, foreign, new CommerceBusinessStorefrontSettings { CommerceBusinessId = business.Id });
        for (var i=0; i<55; i++)
        {
            var person = new ClientProfile { ClientUserId = $"training-{i}", FirstName = "Member", LastName = $"{i:D3}", Email = $"member{i}@example.test", Phone = $"555000{i:D4}" };
            db.Add(person);
            db.Add(new ClientSubscriptionOffer { ClientProfileId = person.Id, CommerceBusinessId = business.Id, SubscriptionScope = ClientSubscriptionScope.Training(business.Id) });
            if (i == 54) db.Add(new ClientSubscriptionOffer { ClientProfileId = person.Id, CommerceBusinessId = business.Id, SubscriptionScope = ClientSubscriptionScope.Training(business.Id) });
        }
        var outsider = new ClientProfile { ClientUserId = "outsider", Email = "foreign@example.test" };
        db.AddRange(outsider,new ClientSubscriptionOffer {ClientProfileId=outsider.Id,CommerceBusinessId=foreign.Id,SubscriptionScope=ClientSubscriptionScope.Training(foreign.Id)});
        await db.SaveChangesAsync();
        var service = new BusinessWorkspaceService(db, Mock.Of<IAnalyticsQueryService>());
        var all = await service.TrainingAsync(business.Id,null,1,null,default);
        Assert.Equal(55,all.Total); Assert.Equal(50,all.Contacts.Count); Assert.Equal(0,all.PaidCustomers);
        var found = await service.TrainingAsync(business.Id,"5550000054",1,null,default);
        Assert.Single(found.Contacts); Assert.Equal(2,found.Contacts[0].Terms.Count);
        Assert.Single((await service.TrainingAsync(business.Id,"member54@",1,null,default)).Contacts);
        Assert.Empty((await service.TrainingAsync(business.Id,null,1,outsider.Id,default)).Contacts);
        Assert.Null(await service.QuickViewAsync(business.Id,found.Contacts[0].ProfileId.ToString(),null,default));
        Assert.Empty(db.WorkstationLeadProfiles); Assert.Empty(db.WebsiteLeads); Assert.Empty(db.AgentClients);
    }

    [Fact]
    public async Task TrainingDetailsRequireExistingBusinessCrmCapability()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var controller = new DeniedController(new BusinessWorkspaceService(db, Mock.Of<IAnalyticsQueryService>()));
        Assert.IsType<Microsoft.AspNetCore.Mvc.ForbidResult>(await controller.TrainingContact(Guid.NewGuid(), Guid.NewGuid(), default));
    }
    private sealed class DeniedController(BusinessWorkspaceService service) : BusinessWorkspaceControllerBase(service)
    {
        protected override Task<CommerceBusiness?> ResolveBusinessAsync(Guid id, string capability, CancellationToken ct) => Task.FromResult<CommerceBusiness?>(null);
    }

    [Fact]
    public async Task NetCollectionsUseSettledPaymentsAndCompleteRefundLedgerWithoutCountingPendingOrForeignMoney()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var business = Guid.NewGuid(); var person = new ClientProfile {ClientUserId="paid-member",Email="training@example.test"}; db.Add(person);
        var offer = new ClientSubscriptionOffer {ClientProfileId=person.Id,CommerceBusinessId=business,SubscriptionScope=ClientSubscriptionScope.Training(business)};
        var sub = new ClientSubscription {ClientProfileId=person.Id,AcceptedOfferId=offer.Id,CommerceBusinessId=business,SubscriptionScope=ClientSubscriptionScope.Training(business)};
        var partial = new SubscriptionPayment {ClientSubscriptionId=sub.Id,AmountCents=25000,Currency="USD",Status=SubscriptionPaymentStatus.PartiallyRefunded,ProviderRefundId="refund"};
        db.AddRange(offer,sub,partial,new SubscriptionPaymentRefund {SubscriptionPaymentId=partial.Id,AmountMinor=5000,Currency="USD",State="Succeeded",ProviderReference="refund"},
            new SubscriptionPayment {ClientSubscriptionId=sub.Id,AmountCents=99999,Status=SubscriptionPaymentStatus.Pending},
            new SubscriptionPayment {ClientSubscriptionId=sub.Id,AmountCents=12000,Status=SubscriptionPaymentStatus.Refunded},
            new SubscriptionPayment {ClientSubscriptionId=Guid.NewGuid(),AmountCents=100000,Status=SubscriptionPaymentStatus.Completed});
        await db.SaveChangesAsync();
        var result=await new BusinessWorkspaceService(db,Mock.Of<IAnalyticsQueryService>()).TrainingAsync(business,null,1,null,default);
        Assert.Equal(1,result.PaidCustomers); Assert.True(Assert.Single(result.Contacts).HasPaid);
        Assert.Equal("training@example.test", result.Contacts[0].Name);
        Assert.Equal(20000,result.NetCollectedMinor["USD"]); Assert.Equal(2,result.UnsettledPayments);
    }
}
