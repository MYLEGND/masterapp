using System;
using System.Security.Claims;
using System.Threading.Tasks;
using ClientApp.Controllers;
using ClientApp.Models;
using ClientApp.Services;
using Domain.Billing;
using Domain.Entities;
using Infrastructure.Billing;
using Infrastructure.Billing.Square;
using Infrastructure.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed class TrainingBillingOwnershipTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ManagementRejectsOtherOwnerOrMembershipBeforeAnyFinancialService(bool otherOwner)
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var self = new ClientProfile { ClientUserId = "self-oid", ExternalIdentityObjectId = "self-oid", Email = "self@example.invalid" };
        var other = new ClientProfile { ClientUserId = "other-oid", ExternalIdentityObjectId = "other-oid", Email = "other@example.invalid" };
        db.AddRange(self, other);
        var business = Guid.NewGuid();
        var subscription = new ClientSubscription { ClientProfileId = otherOwner ? other.Id : self.Id,
            SubscriptionScope = otherOwner ? ClientSubscriptionScope.Training(business) : ClientSubscriptionScope.LegendMembership,
            CommerceBusinessId = otherOwner ? business : null, CommerceProductId = otherOwner ? Guid.NewGuid() : null,
            ActivatedUtc = DateTime.UtcNow, Status = ClientSubscriptionStatus.Active };
        db.ClientSubscriptions.Add(subscription);
        await db.SaveChangesAsync();
        var billing = new Mock<IBillingOrchestrator>(MockBehavior.Strict);
        var cards = new Mock<IClientPaymentMethodService>(MockBehavior.Strict);
        var entitlement = new BillingEntitlementService(db, ControllerTestHelpers.BuildHouseholdMembershipService(db));
        var normalizer = new ClientAppReturnUrlNormalizer();
        var identity = new ClientIdentityAccessService(db, entitlement,
            new ClientIdentityContinuationService(db, new EphemeralDataProtectionProvider(), normalizer), normalizer,
            Mock.Of<IClientEntraLifecycleService>());
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("oid", "self-oid") }, "test")),
            RequestServices = new ServiceCollection().AddSingleton(identity).BuildServiceProvider() };
        var controller = new SubscriptionController(db, new EffectiveClientContextService(db), entitlement, billing.Object,
            cards.Object, new SquareBillingOptions(), normalizer) { ControllerContext = new ControllerContext { HttpContext = http } };
        Assert.IsType<ForbidResult>(await controller.Training(subscription.Id));
        Assert.IsType<ForbidResult>(await controller.CancelTraining(subscription.Id));
        Assert.IsType<ForbidResult>(await controller.RetryTraining(subscription.Id));
        Assert.IsType<ForbidResult>(await controller.AddTrainingPaymentMethod(subscription.Id, new ClientPaymentMethodInput()));
        Assert.IsType<ForbidResult>(await controller.DefaultTrainingPaymentMethod(subscription.Id, Guid.NewGuid()));
        billing.VerifyNoOtherCalls(); cards.VerifyNoOtherCalls();
    }
}
