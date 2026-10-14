using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.Leads;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public class BusinessInquiryNotificationTests
{
    private static (CommerceBusiness Business, CommerceWebsiteInquiry Inquiry) Seed(MasterAppDbContext db)
    {
        var business = new CommerceBusiness { Key = "notification-fixture", OwnerEmail = "current-owner@example.test" };
        var inquiry = new CommerceWebsiteInquiry { CommerceBusinessId = business.Id, PublishedVersionId = Guid.NewGuid(),
            SubmissionId = Guid.NewGuid(), Name = "Fixture Visitor", Email = "visitor@example.test", Message = "Fixture inquiry" };
        db.CommerceBusinesses.Add(business); db.Set<CommerceWebsiteInquiry>().Add(inquiry);
        return (business, inquiry);
    }

    private static BusinessInquiryNotificationService Service(MasterAppDbContext db, Mock<IWebsiteInquiryEmailSender> sender) =>
        new(db, new WebsiteIntakeRecipientResolver(db, new ConfigurationBuilder().Build()), sender.Object);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedTransportBacksOffThenUsesCurrentScopedRecipientAndDoesNotResend(bool throws)
    {
        using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var (business, inquiry) = Seed(db); await db.SaveChangesAsync();
        var sender = new Mock<IWebsiteInquiryEmailSender>(MockBehavior.Strict);
        var setup = sender.Setup(x => x.TrySendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), inquiry.Email, It.IsAny<bool>(), It.IsAny<CancellationToken>()));
        if (throws) setup.ThrowsAsync(new InvalidOperationException("Fixture transport error")); else setup.ReturnsAsync(false);
        await Service(db, sender).DeliverPendingAsync(default);
        Assert.Equal("RetryPending", inquiry.NotificationStatus); Assert.Equal(1, inquiry.NotificationAttempts);
        Assert.Null(inquiry.NotificationSentUtc); Assert.True(inquiry.NotificationNextAttemptUtc > DateTime.UtcNow);
        var revision = inquiry.NotificationRevision;
        await Service(db, sender).DeliverPendingAsync(default);
        Assert.Equal(revision, inquiry.NotificationRevision); Assert.Equal(1, inquiry.NotificationAttempts);
        business.OwnerEmail = "changed-owner@example.test"; inquiry.NotificationNextAttemptUtc = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync(); setup.ReturnsAsync(true);
        await Service(db, sender).DeliverPendingAsync(default);
        Assert.Equal("Sent", inquiry.NotificationStatus); Assert.NotNull(inquiry.NotificationSentUtc);
        Assert.Null(inquiry.NotificationNextAttemptUtc); Assert.Equal(2, inquiry.NotificationAttempts);
        await Service(db, sender).DeliverPendingAsync(default);
        sender.Verify(x => x.TrySendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), inquiry.Email, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        sender.Verify(x => x.TrySendAsync("changed-owner@example.test", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), inquiry.Email, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Single(await db.Set<CommerceWebsiteInquiry>().ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnavailableScopedRecipientLeavesDurableInquiryWithoutSend(bool inactive)
    {
        using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var (business, inquiry) = Seed(db); if (inactive) business.IsActive = false; else business.OwnerEmail = "";
        await db.SaveChangesAsync(); var sender = new Mock<IWebsiteInquiryEmailSender>(MockBehavior.Strict);
        await Service(db, sender).DeliverPendingAsync(default);
        Assert.Equal("RecipientUnavailable", inquiry.NotificationStatus); Assert.Null(inquiry.NotificationSentUtc);
        Assert.NotNull(inquiry.NotificationNextAttemptUtc); Assert.Single(await db.Set<CommerceWebsiteInquiry>().ToListAsync());
        sender.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SqliteNotificationRevisionExcludesStaleCompetingClaimAndSuccessfulReplay()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=False"); await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).Options;
        await using var first = new MasterAppDbContext(options); await first.Database.EnsureCreatedAsync();
        var (_, inquiry) = Seed(first); await first.SaveChangesAsync();
        await using var second = new MasterAppDbContext(options);
        var competitor = await second.Set<CommerceWebsiteInquiry>().SingleAsync(x => x.Id == inquiry.Id);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstSender = new Mock<IWebsiteInquiryEmailSender>(MockBehavior.Strict);
        firstSender.Setup(x => x.TrySendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), inquiry.Email, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(async (string to, string subject, string html, string? text, string? reply, bool save, CancellationToken ct) =>
            { entered.TrySetResult(); await release.Task.WaitAsync(ct); return true; });
        var secondSender = new Mock<IWebsiteInquiryEmailSender>(MockBehavior.Strict);
        var winner = Service(first, firstSender).DeliverOneAsync(inquiry.Id, default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(await Service(second, secondSender).DeliverOneAsync(inquiry.Id, default));
            Assert.Equal(EntityState.Detached, second.Entry(competitor).State);
        }
        finally { release.TrySetResult(); }
        Assert.True(await winner);
        Assert.True(await Service(second, secondSender).DeliverOneAsync(inquiry.Id, default));
        secondSender.VerifyNoOtherCalls();
        firstSender.Verify(x => x.TrySendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), inquiry.Email, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
        var persisted = await second.Set<CommerceWebsiteInquiry>().SingleAsync();
        Assert.Equal("Sent", persisted.NotificationStatus); Assert.Equal(1, persisted.NotificationAttempts);
    }
}
