using System;
using System.Linq;
using System.Threading.Tasks;
using Domain.Entities;
using Domain.Messaging;
using Microsoft.EntityFrameworkCore;
using Moq;
using Infrastructure.Notifications;
using Xunit;

namespace AgentPortal.Tests;

public sealed class NotificationChannelTests
{
    [Theory]
    [InlineData("WebsiteLead", "work")]
    [InlineData("BusinessInquiry", "work")]
    [InlineData("StoreOrder", "work")]
    [InlineData("OrderFulfillment", "work")]
    [InlineData("Engineering", "engineering")]
    [InlineData("Message", "personal")]
    [InlineData("ControlledResourceApproved", "personal")]
    [InlineData("UnknownFutureKind", "personal")]
    public void ExistingLedgerProjectsOneChannel(string kind, string expected) =>
        Assert.Equal(expected, NotificationChannel.ForKind(kind));
    [Fact]
    public async Task WorkQueryFiltersBeforeLimitAndCannotReadAnotherRecipient()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:;Foreign Keys=False");
        await connection.OpenAsync();
        await using var db = new Infrastructure.Data.MasterAppDbContext(new DbContextOptionsBuilder<Infrastructure.Data.MasterAppDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        foreach (var i in Enumerable.Range(0, 101)) db.MobileActivityNotifications.Add(new MobileActivityNotification {
            RecipientUserId = "owner", RecipientParticipantType = "Client", Kind = "Account", OccurredUtc = DateTime.UtcNow.AddMinutes(i) });
        db.MobileActivityNotifications.Add(new MobileActivityNotification { RecipientUserId = "owner", RecipientParticipantType = "Client", Kind = "Message", ConversationId = Guid.NewGuid(), OccurredUtc = DateTime.UtcNow });
        var work = new MobileActivityNotification { RecipientUserId = "owner", RecipientParticipantType = "Client", Kind = "WebsiteLead", OccurredUtc = DateTime.UtcNow.AddDays(-1) };
        db.MobileActivityNotifications.AddRange(work, new MobileActivityNotification {
            RecipientUserId = "outsider", RecipientParticipantType = "Client", Kind = "StoreOrder", OccurredUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var engine = new NotificationEngine(db, Mock.Of<Infrastructure.Messaging.IMessagingProfileImageResolver>(),
            Mock.Of<INotificationRealtimePublisher>(), new ApplePushDeliverySignal(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<NotificationEngine>.Instance);
        var snapshot = await engine.GetChannelSnapshotAsync(new MessagingActor("owner", "Client"), 1, "work");
        Assert.Equal(work.Id, Assert.Single(snapshot.Notifications).Id);
        Assert.Equal("work", snapshot.Notifications[0].Channel);
        Assert.Equal(102, snapshot.ChannelUnreadCounts["personal"]);
        Assert.Equal(101, snapshot.ChannelUnreadCounts["activity"]);
        Assert.Equal(1, snapshot.ChannelUnreadCounts["work"]);
        Assert.DoesNotContain((await engine.GetSnapshotAsync(new MessagingActor("owner", "Client"), 50)).Notifications, x => x.Id == work.Id);
    }

    [Fact]
    public async Task EngineeringDismissalPreservesOtherRecipientsChannelsAndAuditRow()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:;Foreign Keys=False");
        await connection.OpenAsync();
        await using var db = new Infrastructure.Data.MasterAppDbContext(new DbContextOptionsBuilder<Infrastructure.Data.MasterAppDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var own = new MobileActivityNotification { RecipientUserId = "founder", RecipientParticipantType = "Agent", Kind = "Engineering", OccurredUtc = DateTime.UtcNow };
        var other = new MobileActivityNotification { RecipientUserId = "other", RecipientParticipantType = "Agent", Kind = "Engineering", OccurredUtc = DateTime.UtcNow };
        var personal = new MobileActivityNotification { RecipientUserId = "founder", RecipientParticipantType = "Agent", Kind = "Account", OccurredUtc = DateTime.UtcNow };
        db.MobileActivityNotifications.AddRange(own, other, personal);
        await db.SaveChangesAsync();
        var engine = new NotificationEngine(db, Mock.Of<Infrastructure.Messaging.IMessagingProfileImageResolver>(),
            Mock.Of<INotificationRealtimePublisher>(), new ApplePushDeliverySignal(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<NotificationEngine>.Instance);
        var actor = new MessagingActor("founder", "Agent");
        Assert.Equal(personal.Id, Assert.Single((await engine.GetChannelSnapshotAsync(actor, 100, "personal")).Notifications).Id);
        Assert.Equal(own.Id, Assert.Single((await engine.GetChannelSnapshotAsync(actor, 100, "engineering")).Notifications).Id);
        await engine.DismissEngineeringNotificationAsync(actor, other.Id);
        await engine.DismissEngineeringNotificationAsync(actor, personal.Id);
        Assert.False(other.IsCleared);
        Assert.False(personal.IsCleared);
        await engine.MarkReadAndPublishAsync(actor, own.Id);
        Assert.Empty((await engine.GetSnapshotAsync(actor, 100)).ChannelUnreadCounts.Where(x => x.Key == "engineering"));
        await engine.DismissEngineeringNotificationAsync(actor, own.Id);
        await engine.DismissEngineeringNotificationAsync(actor, own.Id);
        Assert.True(own.IsRead && own.IsCleared);
        Assert.NotNull(own.ClearedUtc);
        Assert.Equal(3, await db.MobileActivityNotifications.CountAsync());
        Assert.Empty((await engine.GetChannelSnapshotAsync(actor, 100, "engineering")).Notifications);
        var endpoint = typeof(AgentPortal.Mobile.MobileNotificationsController).GetMethod("DismissEngineering")!;
        Assert.NotNull(Attribute.GetCustomAttribute(endpoint, typeof(AgentPortal.Security.FounderOnlyAttribute)));
    }

}
