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

    [Fact]
    public async Task SQLiteBootstrapper_StampsMessagingReplyAuthorityForLegacyInternalMessagesSchema()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new MasterAppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await CreateMigrationHistoryBeforeMessagingReplyAuthorityAsync(db);
        await RecreateLegacyInternalMessagesSchemaAsync(db);

        Assert.Equal(0, await GetReplyToMessageIdColumnCountAsync(connection));
        Assert.Equal(0, await GetReplyToMessageIdIndexCountAsync(connection));
        Assert.Equal(0, await GetMessagingReplyAuthorityMigrationStampCountAsync(connection));
        Assert.Equal(1, await GetLegacyInternalMessageCountAsync(connection));

        await MasterAppSqliteSchemaBootstrapper.InitializeAsync(
            db,
            NullLogger.Instance);

        Assert.Equal(1, await GetReplyToMessageIdColumnCountAsync(connection));
        Assert.Equal(1, await GetReplyToMessageIdIndexCountAsync(connection));
        Assert.Equal(1, await GetMessagingReplyAuthorityMigrationStampCountAsync(connection));
        Assert.Equal(1, await GetLegacyInternalMessageCountAsync(connection));

        await MasterAppSqliteSchemaBootstrapper.InitializeAsync(
            db,
            NullLogger.Instance);

        Assert.Equal(1, await GetReplyToMessageIdColumnCountAsync(connection));
        Assert.Equal(1, await GetReplyToMessageIdIndexCountAsync(connection));
        Assert.Equal(1, await GetMessagingReplyAuthorityMigrationStampCountAsync(connection));
        Assert.Equal(1, await GetLegacyInternalMessageCountAsync(connection));
    }

    [Fact]
    public async Task SQLiteBootstrapper_StampsMessagingReplyAuthorityForTheCurrentMessagingSchema()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new MasterAppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await CreateMigrationHistoryBeforeMessagingReplyAuthorityAsync(db);

        Assert.Equal(1, await GetReplyToMessageIdColumnCountAsync(connection));
        Assert.Equal(1, await GetReplyToMessageIdIndexCountAsync(connection));
        Assert.Equal(0, await GetMessagingReplyAuthorityMigrationStampCountAsync(connection));

        await MasterAppSqliteSchemaBootstrapper.InitializeAsync(
            db,
            NullLogger.Instance);

        Assert.Equal(1, await GetReplyToMessageIdColumnCountAsync(connection));
        Assert.Equal(1, await GetReplyToMessageIdIndexCountAsync(connection));
        Assert.Equal(1, await GetMessagingReplyAuthorityMigrationStampCountAsync(connection));

        await MasterAppSqliteSchemaBootstrapper.InitializeAsync(
            db,
            NullLogger.Instance);

        Assert.Equal(1, await GetReplyToMessageIdColumnCountAsync(connection));
        Assert.Equal(1, await GetReplyToMessageIdIndexCountAsync(connection));
        Assert.Equal(1, await GetMessagingReplyAuthorityMigrationStampCountAsync(connection));
    }

    private static async Task CreateMigrationHistoryBeforeMessagingReplyAuthorityAsync(MasterAppDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE "__EFMigrationsHistory" (
                "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
                "ProductVersion" TEXT NOT NULL
            )
            """);
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
            VALUES ('20260728035648_AddSocialEngagementMetrics', '10.0.2')
            """);
    }

    private static async Task RecreateLegacyInternalMessagesSchemaAsync(MasterAppDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF");
        await db.Database.ExecuteSqlRawAsync("DROP INDEX IF EXISTS \"IX_InternalMessages_ReplyToMessageId\"");
        await db.Database.ExecuteSqlRawAsync("DROP TABLE \"InternalMessages\"");
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE "InternalMessages" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_InternalMessages" PRIMARY KEY,
                "ConversationId" TEXT NOT NULL,
                "SenderUserId" TEXT NOT NULL,
                "SenderType" TEXT NOT NULL,
                "Body" TEXT NOT NULL,
                "SentUtc" TEXT NOT NULL,
                "EditedUtc" TEXT NULL,
                "DeletedUtc" TEXT NULL,
                "IsDeleted" INTEGER NOT NULL,
                "ClientMessageId" TEXT NULL,
                "RowVersion" BLOB NOT NULL DEFAULT X'',
                CONSTRAINT "FK_InternalMessages_MessageConversations_ConversationId"
                    FOREIGN KEY ("ConversationId") REFERENCES "MessageConversations" ("Id") ON DELETE CASCADE
            )
            """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE UNIQUE INDEX "IX_InternalMessages_ClientMessageId"
            ON "InternalMessages" ("ClientMessageId")
            WHERE "ClientMessageId" IS NOT NULL
            """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE INDEX "IX_InternalMessages_ConversationId_SentUtc"
            ON "InternalMessages" ("ConversationId", "SentUtc")
            """);
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON");

        var conversation = new MessageConversation
        {
            ConversationType = "Direct",
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
            CreatedByUserId = "legacy-agent"
        };
        db.MessageConversations.Add(conversation);
        await db.SaveChangesAsync();

        var messageId = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "InternalMessages" (
                "Id",
                "ConversationId",
                "SenderUserId",
                "SenderType",
                "Body",
                "SentUtc",
                "IsDeleted",
                "ClientMessageId",
                "RowVersion")
            VALUES (
                {messageId},
                {conversation.Id},
                {"legacy-client"},
                {"Client"},
                {"Legacy message survives"},
                {DateTime.UtcNow},
                {false},
                {"legacy-client-message"},
                {Array.Empty<byte>()})
            """);

    }

    private static Task<int> GetReplyToMessageIdColumnCountAsync(SqliteConnection connection) =>
        ExecuteScalarIntAsync(connection, """
            SELECT COUNT(*)
            FROM pragma_table_info('InternalMessages')
            WHERE name = 'ReplyToMessageId'
            """);

    private static Task<int> GetReplyToMessageIdIndexCountAsync(SqliteConnection connection) =>
        ExecuteScalarIntAsync(connection, """
            SELECT COUNT(*)
            FROM pragma_index_list('InternalMessages')
            WHERE name = 'IX_InternalMessages_ReplyToMessageId'
            """);

    private static Task<int> GetMessagingReplyAuthorityMigrationStampCountAsync(SqliteConnection connection) =>
        ExecuteScalarIntAsync(connection, """
            SELECT COUNT(*)
            FROM "__EFMigrationsHistory"
            WHERE "MigrationId" = '20260729063438_AddMessagingReplyAuthority'
            """);

    private static Task<int> GetLegacyInternalMessageCountAsync(SqliteConnection connection) =>
        ExecuteScalarIntAsync(connection, """
            SELECT COUNT(*)
            FROM "InternalMessages"
            WHERE "Body" = 'Legacy message survives'
            """);

    private static async Task<int> ExecuteScalarIntAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
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
