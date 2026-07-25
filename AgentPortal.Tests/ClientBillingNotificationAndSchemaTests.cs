using System;
using System.Threading.Tasks;
using AgentPortal.Services;
using Domain.Billing;
using Domain.Entities;
using Infrastructure.Billing;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed class ClientBillingNotificationAndSchemaTests
{
    [Fact]
    public async Task NotificationQueue_IsIdempotentAndDeliveryMarksTheNoticeSent()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddProfileAsync(db);
        var subscription = await AddSubscriptionAsync(db, profile.Id);
        var notifications = new ClientBillingNotificationService(db);

        var request = new ClientBillingNotificationRequest(
            profile.Id,
            subscription.Id,
            ClientBillingNotificationKind.PaymentFailed,
            $"payment-failed:{subscription.Id:N}");
        notifications.Queue(request);
        notifications.Queue(request);
        await db.SaveChangesAsync();

        var email = new Mock<IEmailSender>(MockBehavior.Strict);
        email.Setup(sender => sender.TrySendAsync(
                profile.Email!,
                It.Is<string>(subject => subject.Contains("Action needed", StringComparison.Ordinal)),
                null,
                It.Is<string>(body => body.Contains("review your payment method", StringComparison.Ordinal)),
                null,
                null,
                null))
            .ReturnsAsync(true);
        var delivery = new ClientBillingNotificationDeliveryService(
            db,
            email.Object,
            NullLogger<ClientBillingNotificationDeliveryService>.Instance);

        var firstRun = await delivery.DeliverDueAsync(10);
        var secondRun = await delivery.DeliverDueAsync(10);
        var notification = await db.ClientBillingNotifications.SingleAsync();

        Assert.Equal(1, firstRun.Selected);
        Assert.Equal(1, firstRun.Sent);
        Assert.Equal(0, firstRun.Failed);
        Assert.Equal(0, secondRun.Selected);
        Assert.NotNull(notification.SentUtc);
        Assert.Equal(1, notification.AttemptCount);
        Assert.Null(notification.NextAttemptUtc);
        email.VerifyAll();
    }

    [Fact]
    public async Task NotificationDelivery_DefersOnlyTheFailedNoticeWithTheConfiguredRetrySchedule()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddProfileAsync(db);
        var subscription = await AddSubscriptionAsync(db, profile.Id);
        var notifications = new ClientBillingNotificationService(db);
        notifications.Queue(new ClientBillingNotificationRequest(
            profile.Id,
            subscription.Id,
            ClientBillingNotificationKind.PaymentMethodUpdated,
            $"payment-method-updated:{subscription.Id:N}"));
        await db.SaveChangesAsync();

        var email = new Mock<IEmailSender>(MockBehavior.Strict);
        email.Setup(sender => sender.TrySendAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                null,
                It.IsAny<string>(),
                null,
                null,
                null))
            .ReturnsAsync(false);
        var delivery = new ClientBillingNotificationDeliveryService(
            db,
            email.Object,
            NullLogger<ClientBillingNotificationDeliveryService>.Instance);

        var before = DateTime.UtcNow;
        var result = await delivery.DeliverDueAsync(10);
        var notification = await db.ClientBillingNotifications.SingleAsync();

        Assert.Equal(1, result.Selected);
        Assert.Equal(0, result.Sent);
        Assert.Equal(1, result.Failed);
        Assert.Equal("EMAIL_DELIVERY_FAILED", notification.SafeFailureCode);
        Assert.Equal(1, notification.AttemptCount);
        Assert.True(notification.NextAttemptUtc >= before.AddMinutes(15));
        Assert.Null(notification.SentUtc);
        email.VerifyAll();
    }

    [Fact]
    public async Task SQLiteBootstrapper_NormalizesExistingPaymentMetadataWithoutResettingSubscriptionData()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new MasterAppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var profile = await AddProfileAsync(db);
        var subscription = await AddSubscriptionAsync(db, profile.Id);

        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"ClientSubscriptions\" ADD COLUMN \"ProviderPaymentMethodId\" TEXT");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"ClientSubscriptions\" ADD COLUMN \"PaymentMethodBrand\" TEXT");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"ClientSubscriptions\" ADD COLUMN \"PaymentMethodLast4\" TEXT");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"ClientSubscriptions\" ADD COLUMN \"PaymentMethodExpirationMonth\" INTEGER");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"ClientSubscriptions\" ADD COLUMN \"PaymentMethodExpirationYear\" INTEGER");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"ClientSubscriptions\" ADD COLUMN \"PaymentMethodCardholderName\" TEXT");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"ClientSubscriptions\" ADD COLUMN \"PaymentMethodUpdatedUtc\" TEXT");
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "ClientSubscriptions"
            SET
                "ProviderPaymentMethodId" = {"card_legacy_123"},
                "PaymentMethodBrand" = {"VISA"},
                "PaymentMethodLast4" = {"4242"},
                "PaymentMethodExpirationMonth" = {12},
                "PaymentMethodExpirationYear" = {2030},
                "PaymentMethodCardholderName" = {"Client One"},
                "PaymentMethodUpdatedUtc" = {DateTime.UtcNow}
            WHERE "Id" = {subscription.Id}
            """);
        await db.Database.ExecuteSqlRawAsync("DROP TABLE \"ClientBillingNotifications\"");
        await db.Database.ExecuteSqlRawAsync("DROP TABLE \"ClientPaymentMethods\"");

        await MasterAppSqliteSchemaBootstrapper.InitializeAsync(
            db,
            NullLogger.Instance);

        db.ChangeTracker.Clear();
        var preservedSubscription = await db.ClientSubscriptions.AsNoTracking().SingleAsync(item => item.Id == subscription.Id);
        var paymentMethod = await db.ClientPaymentMethods.SingleAsync();
        var appliedMigrations = await db.Database.GetAppliedMigrationsAsync();

        Assert.Equal(profile.Id, preservedSubscription.ClientProfileId);
        Assert.Equal(paymentMethod.Id, preservedSubscription.DefaultPaymentMethodId);
        Assert.Equal("card_legacy_123", paymentMethod.ProviderPaymentMethodId);
        Assert.Equal("VISA", paymentMethod.CardBrand);
        Assert.Equal("4242", paymentMethod.Last4);
        Assert.Equal(12, paymentMethod.ExpirationMonth);
        Assert.Equal(2030, paymentMethod.ExpirationYear);
        Assert.Contains("20260725192303_NormalizeClientPaymentMethods", appliedMigrations);
        Assert.Contains("20260725195951_AddClientBillingNotifications", appliedMigrations);
        Assert.Equal(0, await db.ClientBillingNotifications.CountAsync());
    }

    private static async Task<ClientProfile> AddProfileAsync(MasterAppDbContext db)
    {
        var profile = new ClientProfile
        {
            ClientUserId = Guid.NewGuid().ToString("N"),
            FirstName = "Client",
            LastName = "One",
            Email = "client@example.test",
            NormalizedEmail = "client@example.test",
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };
        db.ClientProfiles.Add(profile);
        await db.SaveChangesAsync();
        return profile;
    }

    private static async Task<ClientSubscription> AddSubscriptionAsync(MasterAppDbContext db, Guid clientProfileId)
    {
        var offer = new ClientSubscriptionOffer
        {
            ClientProfileId = clientProfileId,
            OwnerAgentUserId = "agent-1",
            PriceType = ClientSubscriptionOfferPriceType.Fixed100,
            MonthlyAmountCents = ClientSubscriptionOfferPricing.Fixed100Cents,
            Currency = "USD",
            BillingAnchorSelectionMode = BillingAnchorSelectionMode.FirstOfMonth,
            Status = ClientSubscriptionOfferStatus.Accepted,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };
        db.ClientSubscriptionOffers.Add(offer);

        var subscription = new ClientSubscription
        {
            ClientProfileId = clientProfileId,
            AcceptedOfferId = offer.Id,
            OwnerAgentUserId = "agent-1",
            Provider = BillingProvider.Square,
            ProviderEnvironment = BillingProviderEnvironment.Sandbox,
            ProviderCustomerId = "customer_legacy",
            ProviderSubscriptionId = "subscription_legacy",
            ProviderPlanVariationId = "plan_legacy",
            MonthlyAmountCents = ClientSubscriptionOfferPricing.Fixed100Cents,
            Currency = "USD",
            BillingTimeZoneId = "UTC",
            Status = ClientSubscriptionStatus.Active,
            PaymentStanding = ClientSubscriptionPaymentStanding.Current,
            IsPlatformManaged = true,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };
        db.ClientSubscriptions.Add(subscription);
        await db.SaveChangesAsync();
        return subscription;
    }
}
