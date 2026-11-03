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
        Assert.DoesNotContain((await engine.GetSnapshotAsync(new MessagingActor("owner", "Client"), 50)).Notifications, x => x.Id == work.Id);
    }

}
