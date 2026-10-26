using System;
using System.Threading.Tasks;
using Domain.Billing;
using Domain.Entities;
using Infrastructure.Billing;
using Infrastructure.Households;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed class TrainingSubscriptionScopeTests
{
    [Fact]
    public async Task UnadmittedRolloutRejectsOfferAndExistingOfferActivationBeforeGateway()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var client = new ClientProfile { Email = "held@example.invalid", NormalizedEmail = "held@example.invalid" };
        var product = Product();
        db.AddRange(client, product, new CommerceBusiness { Id = product.CommerceBusinessId, Key = "held" });
        await db.SaveChangesAsync();
        var gateway = new Mock<IBillingGateway>(MockBehavior.Strict);
        var service = new MasterAppBillingOrchestrator(db, gateway.Object,
            Mock.Of<IBillingEntitlementService>(), Mock.Of<IClientSubscriptionActivationPolicyService>());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateClientSubscriptionOfferAsync(new(client.Id,
            "training-test", ClientSubscriptionOfferPriceType.Custom, null, "USD", BillingAnchorSelectionMode.FirstOfMonth,
            null, null, DateTime.UtcNow.AddDays(1), TrainingBusinessId: product.CommerceBusinessId, TrainingProductId: product.Id)));
        Assert.Empty(db.ClientSubscriptionOffers);
        var enabledService = new MasterAppBillingOrchestrator(db, gateway.Object,
            Mock.Of<IBillingEntitlementService>(), Mock.Of<IClientSubscriptionActivationPolicyService>(),
            rollout: new() { TrainingSubscriptionsEnabled = true });
        var offer = await enabledService.CreateClientSubscriptionOfferAsync(new(client.Id,
            "training-test", ClientSubscriptionOfferPriceType.Custom, null, "USD", BillingAnchorSelectionMode.FirstOfMonth,
            null, null, DateTime.UtcNow.AddDays(1), TrainingBusinessId: product.CommerceBusinessId, TrainingProductId: product.Id));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ActivateClientSubscriptionAsync(new(client.Id,
            offer.Id, "training-test", "unused-source", "USD", 1, "UTC", DateTime.UtcNow, DateTime.UtcNow.AddMonths(1),
            DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(1)), true, true, true, client.Email)));
        Assert.Equal("Training activation is temporarily unavailable.", error.Message);
        Assert.Empty(db.SubscriptionPayments);
        Assert.Empty(db.ClientSubscriptions);
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("Closed")]
    [InlineData("DeletionRequested")]
    public async Task ClosingAccountAfterOfferPreventsAnyPayment(string lifecycle)
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var client = new ClientProfile { Email = "closed@example.invalid", NormalizedEmail = "closed@example.invalid" };
        var product = Product();
        db.ClientProfiles.Add(client);
        db.CommerceBusinesses.Add(new CommerceBusiness { Id = product.CommerceBusinessId, Key = "training-test" });
        db.CommerceProducts.Add(product);
        await db.SaveChangesAsync();
        var gateway = new Mock<IBillingGateway>(MockBehavior.Strict);
        var service = new MasterAppBillingOrchestrator(db, gateway.Object,
            Mock.Of<IBillingEntitlementService>(), Mock.Of<IClientSubscriptionActivationPolicyService>(),
            rollout: new() { TrainingSubscriptionsEnabled = true });
        var offer = await service.CreateClientSubscriptionOfferAsync(new(client.Id, "training-test", ClientSubscriptionOfferPriceType.Custom,
            null, "USD", BillingAnchorSelectionMode.FirstOfMonth, null, null, DateTime.UtcNow.AddDays(1),
            TrainingBusinessId: product.CommerceBusinessId, TrainingProductId: product.Id));
        db.AccountLifecycleRecords.Add(new AccountLifecycleRecord { ProfileId = client.Id, ParticipantType = "Client", State = lifecycle });
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ActivateClientSubscriptionAsync(new(client.Id,
            offer.Id, "training-test", "unused-source", "USD", 1, "UTC", DateTime.UtcNow, DateTime.UtcNow.AddMonths(1),
            DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(1)), true, true, true, client.Email)));
        Assert.Empty(db.SubscriptionPayments);
        gateway.VerifyNoOtherCalls();
    }

    [Fact]
    public void CatalogPromotionIsRecurringAndDoesNotMutateAcceptedTerms()
    {
        var product = Product();
        product.Discounts.Add(new CommerceProductDiscount { Code = "SUMMER", DiscountType = "Percent", Amount = 20 });
        var terms = TrainingPlanTerms.FromCatalog(product, "summer");
        Assert.Equal(28000, terms.MonthlyAmountCents);
        Assert.Equal("SUMMER", terms.PromotionCode);
        product.PriceCents = 50000;
        product.Description = "Changed future benefits";
        Assert.Equal(28000, terms.MonthlyAmountCents);
        Assert.Equal("2–3 coaching sessions per week", terms.Benefits);
        Assert.Throws<InvalidOperationException>(() => TrainingPlanTerms.FromCatalog(product, "UNKNOWN"));
    }

    [Fact]
    public void TrainingPromotionCanBeBelowMembershipFloorButNeverFree()
    {
        var product = Product();
        product.PriceCents = 5000;
        product.Discounts.Add(new CommerceProductDiscount { Code = "GYM", DiscountType = "Fixed", Amount = 10 });
        Assert.Equal(4000, TrainingPlanTerms.FromCatalog(product, "GYM").MonthlyAmountCents);
        product.Discounts[0].Amount = 50;
        Assert.Throws<InvalidOperationException>(() => TrainingPlanTerms.FromCatalog(product, "GYM"));
        product.Discounts.Clear();
        product.BillingKind = ClientSubscriptionScope.OneTimeProduct;
        Assert.Throws<InvalidOperationException>(() => TrainingPlanTerms.FromCatalog(product, null));
    }

    [Fact]
    public async Task TrainingEntitlementIsExactClientBusinessAndPlanWithoutHouseholdInheritance()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var business = Guid.NewGuid();
        var plan = Guid.NewGuid();
        var client = Guid.NewGuid();
        var row = new ClientSubscription
        {
            ClientProfileId = client, SubscriptionScope = ClientSubscriptionScope.Training(business),
            CommerceBusinessId = business, CommerceProductId = plan,
            Status = ClientSubscriptionStatus.Active, PaymentStanding = ClientSubscriptionPaymentStanding.Current
        };
        db.ClientSubscriptions.Add(row);
        await db.SaveChangesAsync();
        var households = new Mock<IHouseholdMembershipService>(MockBehavior.Strict);
        var service = new BillingEntitlementService(db, households.Object);
        var key = ClientSubscriptionScope.TrainingEntitlement(business, plan);
        Assert.Equal(ClientEntitlementStatus.Active, (await service.EvaluateAsync(new(client, key, DateTime.UtcNow))).Status);
        Assert.Equal(ClientEntitlementStatus.NotGranted, (await service.EvaluateAsync(new(Guid.NewGuid(), key, DateTime.UtcNow))).Status);
        Assert.Equal(ClientEntitlementStatus.NotGranted, (await service.EvaluateAsync(new(client, ClientSubscriptionScope.TrainingEntitlement(Guid.NewGuid(), plan), DateTime.UtcNow))).Status);
        Assert.Equal(ClientEntitlementStatus.NotGranted, (await service.EvaluateAsync(new(client, ClientSubscriptionScope.TrainingEntitlement(business, Guid.NewGuid()), DateTime.UtcNow))).Status);
        Assert.Equal(ClientEntitlementStatus.NotGranted, (await service.EvaluateAsync(new(client, "Training:invalid", DateTime.UtcNow))).Status);
        var projection = await service.RefreshAsync(client, key);
        Assert.Equal(client, projection.ClientProfileId);
        Assert.Equal(key, projection.EntitlementKey);
        households.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task TrainingNeverGrantsLegendMembershipAndMembershipNeverGrantsTraining()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var client = new ClientProfile { ClientUserId = "training-fixture", Email = "training@example.invalid" };
        var product = Product();
        db.ClientProfiles.Add(client);
        db.ClientSubscriptions.Add(new ClientSubscription
        {
            ClientProfileId = client.Id, SubscriptionScope = ClientSubscriptionScope.Training(product.CommerceBusinessId),
            CommerceBusinessId = product.CommerceBusinessId, CommerceProductId = product.Id,
            Status = ClientSubscriptionStatus.Active, PaymentStanding = ClientSubscriptionPaymentStanding.Current
        });
        await db.SaveChangesAsync();
        var service = new BillingEntitlementService(db, ControllerTestHelpers.BuildHouseholdMembershipService(db));
        Assert.Equal(ClientEntitlementStatus.NotGranted, (await service.EvaluateAsync(new(client.Id, BillingEntitlementKeys.ClientAppFullAccess, DateTime.UtcNow))).Status);
        var other = new ClientProfile { ClientUserId = "membership-fixture", Email = "member@example.invalid" };
        db.ClientProfiles.Add(other);
        db.ClientSubscriptions.Add(new ClientSubscription { ClientProfileId = other.Id, Status = ClientSubscriptionStatus.Active });
        await db.SaveChangesAsync();
        Assert.Equal(ClientEntitlementStatus.NotGranted, (await service.EvaluateAsync(new(other.Id,
            ClientSubscriptionScope.TrainingEntitlement(product.CommerceBusinessId, product.Id), DateTime.UtcNow))).Status);
    }

    [Fact]
    public async Task TrainingCancellationUsesExistingFinancialStateAndPeriodBoundary()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var product = Product();
        var row = new ClientSubscription
        {
            ClientProfileId = Guid.NewGuid(), SubscriptionScope = ClientSubscriptionScope.Training(product.CommerceBusinessId),
            CommerceBusinessId = product.CommerceBusinessId, CommerceProductId = product.Id,
            Status = ClientSubscriptionStatus.Active, CancelAtPeriodEnd = true, CurrentPeriodEndUtc = DateTime.UtcNow.AddMinutes(-1)
        };
        db.ClientSubscriptions.Add(row);
        await db.SaveChangesAsync();
        var service = new BillingEntitlementService(db, Mock.Of<IHouseholdMembershipService>());
        var result = await service.EvaluateAsync(new(row.ClientProfileId, ClientSubscriptionScope.EntitlementKey(row), DateTime.UtcNow));
        Assert.Equal(ClientEntitlementStatus.NotGranted, result.Status);
        Assert.Equal("SUBSCRIPTION_ENDED_AT_PERIOD_END", result.ReasonCode);
    }

    [Fact]
    public async Task FounderMembershipUpdateCannotChangePurchasedTrainingTerms()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var product = Product();
        var row = new ClientSubscription
        {
            ClientProfileId = Guid.NewGuid(), SubscriptionScope = ClientSubscriptionScope.Training(product.CommerceBusinessId),
            CommerceBusinessId = product.CommerceBusinessId, CommerceProductId = product.Id,
            MonthlyAmountCents = 35000, BillingAnchorDay = 12, Status = ClientSubscriptionStatus.Active
        };
        db.ClientSubscriptions.Add(row);
        await db.SaveChangesAsync();
        var gateway = new Mock<IBillingGateway>(MockBehavior.Strict);
        var service = new MasterAppBillingOrchestrator(db, gateway.Object,
            Mock.Of<IBillingEntitlementService>(), Mock.Of<IClientSubscriptionActivationPolicyService>());
        var result = await service.UpdateClientSubscriptionAsync(new(row.Id,
            ClientSubscriptionOfferPriceType.Custom, 70000, BillingAnchorSelectionMode.SpecificDayOfMonth, 20, "founder", true));
        Assert.False(result.Success);
        Assert.Equal("PURCHASED_TRAINING_TERMS_IMMUTABLE", result.SafeErrorCode);
        Assert.Equal(35000, row.MonthlyAmountCents);
        Assert.Equal(12, row.BillingAnchorDay);
        Assert.Empty(db.BillingAuditEntries);
        gateway.VerifyNoOtherCalls();
    }

    private static CommerceProduct Product() => new()
    {
        CommerceBusinessId = Guid.NewGuid(), BillingKind = ClientSubscriptionScope.MonthlyTraining,
        Name = "Coaching", Description = "2–3 coaching sessions per week", PriceCents = 35000, IsActive = true
    };
}
