using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Domain.Billing;
using Domain.Entities;
using Infrastructure.Billing;
using Infrastructure.Leads;
using Microsoft.Extensions.Configuration;
using Moq;
using ParfaitApp.Services;
using Xunit;

namespace AgentPortal.Tests;

public sealed class TrainingEnrollmentTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("invalid")]
    public async Task UnadmittedRolloutCannotWriteProfileOfferOrSendInvitation(string? enabled)
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var product = Seed(db);
        await db.SaveChangesAsync();
        var billing = new Mock<IBillingOrchestrator>(MockBehavior.Strict);
        var mail = new Mock<IWebsiteInquiryEmailSender>(MockBehavior.Strict);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new[] {
            new System.Collections.Generic.KeyValuePair<string, string?>("Billing:ClientSubscriptions:TrainingSubscriptionsEnabled", enabled)
        }).Build();
        var service = new TrainingEnrollmentService(db, billing.Object, mail.Object, configuration);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RequestAsync(product.CommerceBusinessId,
            product.Id, "new@example.invalid", null, default));
        Assert.Empty(db.ClientProfiles);
        Assert.Empty(db.ClientSubscriptionOffers);
        Assert.Empty(db.SubscriptionActivationInvitations);
        billing.VerifyNoOtherCalls();
        mail.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PublicRequestCreatesOnlyUnverifiedProfileAndOneEmailedInvitation()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var product = Seed(db);
        await db.SaveChangesAsync();
        var mail = Mail(true);
        var gateway = new Mock<IBillingGateway>(MockBehavior.Strict);
        var service = Service(db, mail, gateway);
        await service.RequestAsync(product.CommerceBusinessId, product.Id, "New@Example.invalid", null, default);
        await service.RequestAsync(product.CommerceBusinessId, product.Id, "new@example.invalid", null, default);
        var profile = Assert.Single(db.ClientProfiles);
        Assert.False(profile.IsVerified);
        Assert.True(ClientRecordClassification.IsLead(profile.ClientUserId, profile.CrmNotes, profile.CrmStatus));
        Assert.Null(profile.ExternalIdentityObjectId);
        Assert.Single(db.ClientSubscriptionOffers);
        Assert.Equal(SubscriptionActivationInvitationStatus.Sent, Assert.Single(db.SubscriptionActivationInvitations).Status);
        Assert.Empty(db.ClientSubscriptions);
        Assert.Empty(db.SubscriptionPayments);
        Assert.Single(mail.Invocations);
        Assert.Contains("/activate/", mail.Invocations[0].Arguments[3]?.ToString());
        gateway.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task StaleUnsentInvitationRecoversSameOfferWithoutReplacingItsPriceOrProfile()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var product = Seed(db);
        var profile = new ClientProfile { Email = "existing@example.invalid", NormalizedEmail = "existing@example.invalid",
            FirstName = "Existing", LastName = "Customer", ExternalIdentityObjectId = "unchanged-oid", IsVerified = true };
        db.ClientProfiles.Add(profile);
        await db.SaveChangesAsync();
        var mail = Mail(false);
        var gateway = new Mock<IBillingGateway>(MockBehavior.Strict);
        var service = Service(db, mail, gateway);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RequestAsync(product.CommerceBusinessId, product.Id, profile.Email, null, default));
        var pending = Assert.Single(db.SubscriptionActivationInvitations);
        pending.CreatedUtc = DateTime.UtcNow.AddMinutes(-20);
        var persistedProduct = db.CommerceProducts.Single();
        persistedProduct.PriceCents = 55000;
        await db.SaveChangesAsync();
        SetMail(mail, true);
        await service.RequestAsync(product.CommerceBusinessId, product.Id, profile.Email, null, default);
        Assert.Equal(35000, Assert.Single(db.ClientSubscriptionOffers).MonthlyAmountCents);
        Assert.Equal(2, db.SubscriptionActivationInvitations.Count());
        Assert.Single(db.SubscriptionActivationInvitations.Where(x => x.Status == SubscriptionActivationInvitationStatus.Sent));
        Assert.Single(db.SubscriptionActivationInvitations.Where(x => x.Status == SubscriptionActivationInvitationStatus.Superseded));
        var unchanged = Assert.Single(db.ClientProfiles);
        Assert.Equal("Existing", unchanged.FirstName);
        Assert.Equal("unchanged-oid", unchanged.ExternalIdentityObjectId);
        Assert.True(unchanged.IsVerified);
        Assert.Empty(db.SubscriptionPayments);
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(SubscriptionActivationInvitationStatus.Viewed)]
    [InlineData(SubscriptionActivationInvitationStatus.PaymentStarted)]
    [InlineData(SubscriptionActivationInvitationStatus.Sent)]
    [InlineData(SubscriptionActivationInvitationStatus.Revoked)]
    [InlineData(SubscriptionActivationInvitationStatus.Expired)]
    [InlineData(SubscriptionActivationInvitationStatus.Superseded)]
    public async Task AnonymousRetryNeverSupersedesDeliveredOrInFlightInvitation(SubscriptionActivationInvitationStatus status)
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var product = Seed(db);
        await db.SaveChangesAsync();
        var mail = Mail(true);
        var service = Service(db, mail, new(MockBehavior.Strict));
        await service.RequestAsync(product.CommerceBusinessId, product.Id, "existing@example.invalid", null, default);
        var invitation = db.SubscriptionActivationInvitations.Single();
        invitation.Status = status;
        invitation.CreatedUtc = DateTime.UtcNow.AddHours(-2);
        await db.SaveChangesAsync();
        await service.RequestAsync(product.CommerceBusinessId, product.Id, "existing@example.invalid", null, default);
        Assert.Single(db.SubscriptionActivationInvitations);
        Assert.Single(db.ClientSubscriptionOffers);
        Assert.Single(mail.Invocations);
    }

    private static CommerceProduct Seed(Infrastructure.Data.MasterAppDbContext db)
    {
        var business = new CommerceBusiness { Key = "parfait", DisplayName = "Parfait" };
        var product = new CommerceProduct { CommerceBusinessId = business.Id, BillingKind = ClientSubscriptionScope.MonthlyTraining,
            Name = "Guided training", Description = "2–3 sessions per week", PriceCents = 35000, IsActive = true };
        db.AddRange(business, product);
        return product;
    }
    private static TrainingEnrollmentService Service(Infrastructure.Data.MasterAppDbContext db,
        Mock<IWebsiteInquiryEmailSender> mail, Mock<IBillingGateway> gateway) => new(db,
            new MasterAppBillingOrchestrator(db, gateway.Object, Mock.Of<IBillingEntitlementService>(), Mock.Of<IClientSubscriptionActivationPolicyService>(), rollout: new() { TrainingSubscriptionsEnabled = true }),
            mail.Object, new ConfigurationBuilder().AddInMemoryCollection(new[] { new System.Collections.Generic.KeyValuePair<string, string?>("Billing:ClientSubscriptions:TrainingSubscriptionsEnabled", "true") }).Build());
    private static Mock<IWebsiteInquiryEmailSender> Mail(bool result)
    {
        var mail = new Mock<IWebsiteInquiryEmailSender>(MockBehavior.Strict);
        SetMail(mail, result);
        return mail;
    }
    private static void SetMail(Mock<IWebsiteInquiryEmailSender> mail, bool result) => mail.Setup(x => x.TrySendAsync(
        It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(result);
}
