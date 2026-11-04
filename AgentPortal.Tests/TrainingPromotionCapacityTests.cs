using System;
using System.Linq;
using System.Threading.Tasks;
using Domain.Billing;
using Domain.Entities;
using Infrastructure.Billing;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed class TrainingPromotionCapacityTests
{
    [Fact]
    public async Task FiveDistinctMembersReserveOfferSixthMustReviewAndSameMemberDoesNotConsumeAnotherPlace()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var business = new CommerceBusiness { Key = "training-cap" };
        var discount = new CommerceProductDiscount { Code = "INTRO", ExternalDiscountKey = "intro", DiscountType = "Fixed", Amount = 100, UsageLimit = 5 };
        var product = new CommerceProduct { CommerceBusinessId = business.Id, Name = "Training", Description = "Approved training benefits", BillingKind = ClientSubscriptionScope.MonthlyTraining, PriceCents = 35000, Discounts = [discount] };
        var clients = Enumerable.Range(0, 6).Select(i => new ClientProfile { ClientUserId = $"member-{i}", Email = $"member{i}@example.test" }).ToArray();
        db.AddRange(business, product); db.AddRange(clients); await db.SaveChangesAsync();
        var gateway = new Mock<IBillingGateway>(MockBehavior.Strict);
        var service = new MasterAppBillingOrchestrator(db, gateway.Object, Mock.Of<IBillingEntitlementService>(),
            Mock.Of<IClientSubscriptionActivationPolicyService>(), rollout: new() { TrainingSubscriptionsEnabled = true });
        CreateClientSubscriptionOfferCommand Command(ClientProfile client) => new(client.Id, "training-test", ClientSubscriptionOfferPriceType.Custom,
            null, "USD", BillingAnchorSelectionMode.FirstOfMonth, null, null, DateTime.UtcNow.AddHours(24),
            TrainingBusinessId: business.Id, TrainingProductId: product.Id, TrainingPromotionCode: "INTRO");
        foreach (var client in clients.Take(5))
        {
            var offer = await service.CreateClientSubscriptionOfferAsync(Command(client));
            Assert.Equal(25000, offer.MonthlyAmountCents);
            Assert.Equal(discount.Id, offer.TrainingDiscountId);
        }
        await Assert.ThrowsAsync<TrainingPromotionCapacityException>(() => service.CreateClientSubscriptionOfferAsync(Command(clients[5])));
        Assert.Equal(5, db.ClientSubscriptionOffers.Count());
        await service.CreateClientSubscriptionOfferAsync(Command(clients[0]));
        Assert.Equal(5, (await TrainingPromotionReservations.ReservedClientIdsAsync(db, discount.Id, DateTime.UtcNow)).Count);
        Assert.Empty(db.SubscriptionPayments);
        gateway.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CatalogRemovalPreservesPurchasedDiscountIdentityAndReplacementCannotResetCapacity()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var business = new CommerceBusiness { Key = "parfait" };
        var client = new ClientProfile { ClientUserId = "retained-member", Email = "retained@example.test" };
        db.AddRange(business, client); await db.SaveChangesAsync();
        var service = new ParfaitApp.Services.ParfaitProductService(null!, db, new() { TrainingSubscriptionsEnabled = true });
        var model = new ParfaitApp.Models.ParfaitProductEditorViewModel { Id = "training-product", Name = "Training", Slug = "training", Description = "Approved benefits",
            BillingKind = ClientSubscriptionScope.MonthlyTraining, PriceCents = 35000, IsActive = true,
            DiscountCodes = [new() { Id = "original-intro", Code = "INTRO", DiscountType = "Fixed", Amount = 100, UsageLimit = 5 }] };
        service.SaveProduct(business.Id, model);
        var discount = Assert.Single(db.CommerceProductDiscounts);
        db.Add(new ClientSubscriptionOffer { ClientProfileId = client.Id, TrainingDiscountId = discount.Id,
            Status = ClientSubscriptionOfferStatus.Accepted, MonthlyAmountCents = 25000 });
        await db.SaveChangesAsync();
        model.DiscountCodes.Clear(); service.SaveProduct(business.Id, model);
        Assert.False(discount.IsActive);
        Assert.Equal(discount.Id, Assert.Single(db.CommerceProductDiscounts).Id);
        model.DiscountCodes.Add(new() { Id = "replacement-intro", Code = "INTRO", DiscountType = "Fixed", Amount = 100, UsageLimit = 5 });
        Assert.Throws<InvalidOperationException>(() => service.SaveProduct(business.Id, model));
        Assert.Equal(discount.Id, Assert.Single(db.CommerceProductDiscounts).Id);
        Assert.Equal(25000, Assert.Single(db.ClientSubscriptionOffers).MonthlyAmountCents);
        model.DiscountCodes[0].Id = "original-intro";
        service.SaveProduct(business.Id, model);
        Assert.True(discount.IsActive);
        Assert.Equal(discount.Id, Assert.Single(db.CommerceProductDiscounts).Id);
        Assert.Single(await TrainingPromotionReservations.ReservedClientIdsAsync(db, discount.Id, DateTime.UtcNow));
    }

    [Fact]
    public async Task ExpiredUnstartedOfferReleasesButCanceledSubscriberAndUnknownStartedPaymentRemainReserved()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var discount = Guid.NewGuid();
        var expired = new ClientSubscriptionOffer { TrainingDiscountId = discount, ClientProfileId = Guid.NewGuid(), Status = ClientSubscriptionOfferStatus.Offered, ExpiresUtc = DateTime.UtcNow.AddDays(-1) };
        var canceled = new ClientSubscriptionOffer { TrainingDiscountId = discount, ClientProfileId = Guid.NewGuid(), Status = ClientSubscriptionOfferStatus.Superseded };
        var unknown = new ClientSubscriptionOffer { TrainingDiscountId = discount, ClientProfileId = Guid.NewGuid(), Status = ClientSubscriptionOfferStatus.Expired };
        var foreign = new ClientSubscriptionOffer { TrainingDiscountId = Guid.NewGuid(), ClientProfileId = Guid.NewGuid(), Status = ClientSubscriptionOfferStatus.Accepted };
        db.AddRange(expired, canceled, unknown, foreign,
            new ClientSubscription { AcceptedOfferId = canceled.Id, ClientProfileId = canceled.ClientProfileId, Status = ClientSubscriptionStatus.Canceled },
            new SubscriptionActivationInvitation { ClientSubscriptionOfferId = unknown.Id, ClientProfileId = unknown.ClientProfileId, Status = SubscriptionActivationInvitationStatus.Expired, PaymentStartedUtc = DateTime.UtcNow.AddDays(-2) });
        await db.SaveChangesAsync();
        var reserved = await TrainingPromotionReservations.ReservedClientIdsAsync(db, discount, DateTime.UtcNow);
        Assert.Equal(2, reserved.Count);
        Assert.Contains(canceled.ClientProfileId, reserved);
        Assert.Contains(unknown.ClientProfileId, reserved);
        Assert.DoesNotContain(expired.ClientProfileId, reserved);
        Assert.DoesNotContain(foreign.ClientProfileId, reserved);
    }
}
