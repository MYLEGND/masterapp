using Domain.Entities;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Infrastructure.Data;
using Infrastructure.Leads;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public class WebsiteLeadNotificationRecoveryTests
{
    private static MasterAppDbContext BuildDb() => new(new DbContextOptionsBuilder<MasterAppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static WebsiteLead Lead(string interest = "LegendInquiry") => new()
    {
        LeadId = Guid.NewGuid(), FirstName = "Fixture", Email = "lead@example.test", Status = "New",
        InterestType = interest, CreatedUtc = DateTime.UtcNow
    };

    private static Mock<IWebsiteInquiryEmailSender> Sender()
    {
        var sender = new Mock<IWebsiteInquiryEmailSender>(MockBehavior.Strict);
        sender.Setup(x => x.TrySendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return sender;
    }

    private static async Task RunPassAsync(MasterAppDbContext db, Mock<IWebsiteInquiryEmailSender> sender)
    {
        using var provider = new ServiceCollection().AddSingleton(db)
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Founder:Email"] = "founder@example.test" }).Build())
            .AddSingleton<WebsiteIntakeRecipientResolver>().AddSingleton(sender.Object).BuildServiceProvider();
        var scope = new Mock<IServiceScope>(); scope.SetupGet(x => x.ServiceProvider).Returns(provider);
        var scopes = new Mock<IServiceScopeFactory>(); scopes.Setup(x => x.CreateScope()).Returns(scope.Object);
        using var worker = new WebsiteLeadNotificationRecoveryWorker(scopes.Object, NullLogger<WebsiteLeadNotificationRecoveryWorker>.Instance);
        await worker.RecoverPendingAsync();
    }

    [Fact]
    public async Task CanonicalInquiryCommittedBeforeFirstAttemptIsRecovered()
    {
        using var db = BuildDb();
        var lead = Lead(); db.WebsiteLeads.Add(lead); await db.SaveChangesAsync();
        var sender = Sender();
        await RunPassAsync(db, sender);
        Assert.NotNull(lead.NotificationSentUtc);
        sender.Verify(x => x.TrySendAsync("founder@example.test", It.IsAny<string>(), It.IsAny<string>(), null, lead.Email, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task InterruptedExpiredAttemptRecoversWithoutNotificationFailedCrmStatus()
    {
        using var db = BuildDb();
        var lead = Lead(); lead.NotificationAttemptUtc = DateTime.UtcNow.AddHours(-1);
        db.WebsiteLeads.Add(lead); await db.SaveChangesAsync();
        var sender = Sender();
        await RunPassAsync(db, sender);
        Assert.NotNull(lead.NotificationSentUtc);
        sender.Verify(x => x.TrySendAsync("founder@example.test", It.IsAny<string>(), It.IsAny<string>(), null, lead.Email, true, It.IsAny<CancellationToken>()), Times.Once);
    }
    [Theory]
    [InlineData("ProtectionInquiry")]
    [InlineData("BusinessInquiry")]
    public async Task OtherCanonicalNeverAttemptedInquiriesRecover(string interest)
    {
        using var db = BuildDb();
        var lead = Lead(interest); db.WebsiteLeads.Add(lead); await db.SaveChangesAsync();
        await RunPassAsync(db, Sender());
        Assert.NotNull(lead.NotificationSentUtc);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExpiredAttemptIsStatusIndependentButFreshLeaseIsPreserved(bool expired)
    {
        using var db = BuildDb();
        var lead = Lead("LegacyForm"); lead.Status = "Contacted";
        lead.NotificationAttemptUtc = DateTime.UtcNow.AddMinutes(expired ? -30 : -1);
        var attempt = lead.NotificationAttemptUtc;
        db.WebsiteLeads.Add(lead); await db.SaveChangesAsync();
        var sender = Sender(); await RunPassAsync(db, sender);
        Assert.Equal(expired, lead.NotificationSentUtc.HasValue);
        Assert.Equal("Contacted", lead.Status);
        if (!expired) Assert.Equal(attempt, lead.NotificationAttemptUtc);
        sender.Verify(x => x.TrySendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), true, It.IsAny<CancellationToken>()), expired ? Times.Once() : Times.Never());
    }

    [Fact]
    public async Task NeverAttemptedUnrelatedHistoricalLeadIsNotEmailed()
    {
        using var db = BuildDb(); var lead = Lead("LifeQuote");
        db.WebsiteLeads.Add(lead); await db.SaveChangesAsync();
        var sender = new Mock<IWebsiteInquiryEmailSender>(MockBehavior.Strict);
        await RunPassAsync(db, sender);
        Assert.Null(lead.NotificationAttemptUtc); Assert.Null(lead.NotificationSentUtc);
    }

    [Theory]
    [InlineData("false", "New")]
    [InlineData("throw", "New")]
    [InlineData("uncertain", "New")]
    [InlineData("false", "Contacted")]
    [InlineData("throw", "Contacted")]
    [InlineData("uncertain", "Contacted")]
    [InlineData("false", "Closed")]
    public async Task FailedOrUncertainSenderPreservesDurableLeadBackoffAndCrmStatus(string outcome, string status)
    {
        using var db = BuildDb(); var lead = Lead(); lead.Status = status;
        db.WebsiteLeads.Add(lead); await db.SaveChangesAsync();
        var sender = Sender();
        var setup = sender.Setup(x => x.TrySendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), true, It.IsAny<CancellationToken>()));
        if (outcome == "false") setup.ReturnsAsync(false);
        else setup.ThrowsAsync(outcome == "uncertain" ? new TimeoutException("Fixture uncertain transport") : new InvalidOperationException("Fixture rejected transport"));
        await RunPassAsync(db, sender);
        Assert.Null(lead.NotificationSentUtc); Assert.NotNull(lead.NotificationAttemptUtc);
        Assert.Equal(status == "New" ? "NotificationFailed" : status, lead.Status);
        var attempt = lead.NotificationAttemptUtc;
        await RunPassAsync(db, sender); // Fresh canonical lease prevents immediate resend.
        Assert.Equal(attempt, lead.NotificationAttemptUtc);
        sender.Verify(x => x.TrySendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), true, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(lead.LeadId, Assert.Single(await db.WebsiteLeads.ToListAsync()).LeadId);
        lead.NotificationAttemptUtc = DateTime.UtcNow.AddHours(-1); await db.SaveChangesAsync();
        setup.ReturnsAsync(true);
        await RunPassAsync(db, sender);
        Assert.NotNull(lead.NotificationSentUtc); Assert.Equal(status, lead.Status);
        await RunPassAsync(db, sender);
        sender.Verify(x => x.TrySendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), true, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task LinkedBusinessQueuePrefixIsExcludedBeforeBatchAndUnavailableOwnersDoNotStarveFounder()
    {
        using var db = BuildDb();
        for (var index = 0; index < 30; index++)
        {
            var linked = Lead("BusinessInquiry"); linked.Status = "NotificationFailed";
            linked.NotificationAttemptUtc = DateTime.UtcNow.AddHours(-1);
            db.WebsiteLeads.Add(linked);
            db.Set<CommerceWebsiteInquiry>().Add(new CommerceWebsiteInquiry { CommerceBusinessId = Guid.NewGuid(), WebsiteLeadId = linked.LeadId });
        }
        for (var index = 0; index < 30; index++)
        {
            var unavailable = Lead(); unavailable.AgentTrackingProfileId = Guid.NewGuid(); db.WebsiteLeads.Add(unavailable);
        }
        var pending = Lead(); db.WebsiteLeads.Add(pending); await db.SaveChangesAsync();
        var sender = Sender(); await RunPassAsync(db, sender);
        Assert.NotNull(pending.NotificationSentUtc);
        Assert.Equal(1, await db.WebsiteLeads.CountAsync(x => x.NotificationSentUtc != null));
        Assert.Equal(30, await db.WebsiteLeads.CountAsync(x => x.NotificationAttemptUtc != null && x.NotificationSentUtc == null));
        sender.Verify(x => x.TrySendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("deleted")]
    [InlineData("internal")]
    [InlineData("local")]
    [InlineData("inactive-business")]
    [InlineData("inactive-agent")]
    [InlineData("dual")]
    [InlineData("empty-business")]
    [InlineData("empty-agent")]
    public async Task UnsafeOrInvalidOwnerRowsNeverClaimOrSend(string defect)
    {
        using var db = BuildDb(); var lead = Lead();
        if (defect == "deleted") lead.IsDeleted = true;
        if (defect == "internal") lead.IsInternal = true;
        if (defect == "local") lead.Host = "localhost:5000";
        if (defect == "inactive-business") { var business = new CommerceBusiness { Key = "inactive", OwnerEmail = "owner@example.test", IsActive = false }; db.CommerceBusinesses.Add(business); lead.CommerceBusinessId = business.Id; }
        if (defect == "inactive-agent") { var agent = new AgentTrackingProfile { AgentUserId = "agent", AgentUpn = "agent@example.test", Slug = "inactive", Status = "disabled" }; db.AgentTrackingProfiles.Add(agent); lead.AgentTrackingProfileId = agent.Id; }
        if (defect == "dual") { lead.CommerceBusinessId = Guid.NewGuid(); lead.AgentTrackingProfileId = Guid.NewGuid(); }
        if (defect == "empty-business") lead.CommerceBusinessId = Guid.Empty;
        if (defect == "empty-agent") lead.AgentTrackingProfileId = Guid.Empty;
        db.WebsiteLeads.Add(lead); await db.SaveChangesAsync();
        await RunPassAsync(db, new Mock<IWebsiteInquiryEmailSender>(MockBehavior.Strict));
        Assert.Null(lead.NotificationAttemptUtc); Assert.Null(lead.NotificationSentUtc);
    }

    [Fact]
    public async Task PassBudgetsActualClaimsAndNextPassAdvancesAfterFailedLease()
    {
        using var db = BuildDb();
        for (var index = 0; index < 30; index++) db.WebsiteLeads.Add(Lead());
        await db.SaveChangesAsync();
        var sender = Sender(); sender.Setup(x => x.TrySendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), true, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        await RunPassAsync(db, sender);
        Assert.Equal(25, await db.WebsiteLeads.CountAsync(x => x.NotificationAttemptUtc != null));
        await RunPassAsync(db, sender);
        Assert.Equal(30, await db.WebsiteLeads.CountAsync(x => x.NotificationAttemptUtc != null));
        await RunPassAsync(db, sender);
        sender.Verify(x => x.TrySendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), true, It.IsAny<CancellationToken>()), Times.Exactly(30));
    }

    [Fact]
    public async Task CapturedIdentityMaximumDefersConcurrentInquiryUntilNextPass()
    {
        using var db = BuildDb(); var initial = Lead(); initial.Id = 10; var arrival = Lead(); arrival.Id = 20;
        db.WebsiteLeads.Add(initial); await db.SaveChangesAsync();
        var sender = Sender(); var inserted = false;
        sender.Setup(x => x.TrySendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), true, It.IsAny<CancellationToken>()))
            .Returns((string to, string subject, string html, string? text, string? reply, bool save, CancellationToken ct) =>
            {
                if (!inserted) { inserted = true; db.WebsiteLeads.Add(arrival); db.SaveChanges(); }
                return Task.FromResult(true);
            });
        await RunPassAsync(db, sender);
        Assert.NotNull(initial.NotificationSentUtc); Assert.Null(arrival.NotificationSentUtc);
        await RunPassAsync(db, sender);
        Assert.NotNull(arrival.NotificationSentUtc);
    }

    [Fact]
    public async Task SqliteCanonicalLeaseExcludesCompetingSenderAndSuccessfulReplay()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=False"); await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).Options;
        await using var first = new MasterAppDbContext(options); await first.Database.EnsureCreatedAsync();
        var lead = Lead(); first.WebsiteLeads.Add(lead); await first.SaveChangesAsync();
        await using var second = new MasterAppDbContext(options);
        var competitor = await second.WebsiteLeads.SingleAsync(x => x.Id == lead.Id);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var delivery = WebsiteLeadNotificationAuthority.DeliverAsync(first, lead, "founder@example.test", async ct =>
        {
            calls++; entered.TrySetResult(); await release.Task.WaitAsync(ct); return true;
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var competing = await WebsiteLeadNotificationAuthority.DeliverAsync(second, competitor, "founder@example.test", ct => { calls++; return Task.FromResult(true); });
        Assert.False(competing.Claimed); Assert.False(competing.Sent);
        release.TrySetResult(); Assert.True((await delivery).Sent);
        var replay = await WebsiteLeadNotificationAuthority.DeliverAsync(second, competitor, "founder@example.test", ct => { calls++; return Task.FromResult(true); });
        Assert.True(replay.AlreadySent); Assert.False(replay.Claimed); Assert.Equal(1, calls);
        Assert.Single(await second.WebsiteLeads.ToListAsync());
    }

    [Fact]
    public void ActualRecoveryBatchTranslatesLeaseEligibilityAndLinkedQueueExclusionBeforeLimit()
    {
        using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseSqlServer("Server=(local);Database=translation_only;Integrated Security=true;TrustServerCertificate=true").Options);
        var query = (IQueryable<WebsiteLead>)typeof(WebsiteLeadNotificationRecoveryWorker)
            .GetMethod("PendingRecoveryBatch", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object?[] { db, (long?)100, 200L, DateTime.UtcNow })!;
        var sql = query.ToQueryString();
        Assert.Contains("TOP(", sql); Assert.Contains("NOT EXISTS", sql); Assert.Contains("[WebsiteLeadId]", sql);
        Assert.Contains("[NotificationAttemptUtc] IS NULL", sql); Assert.Contains("[NotificationAttemptUtc] <=", sql);
        Assert.Contains("LegendInquiry", sql); Assert.Contains("ProtectionInquiry", sql); Assert.Contains("BusinessInquiry", sql);
        Assert.Contains("[Id] >", sql); Assert.Contains("[Id] <=", sql); Assert.Contains("ORDER BY", sql);
        Assert.DoesNotContain("NotificationFailed", sql);
        Console.WriteLine("SQL translation only: canonical email recovery batch\n" + sql);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task SqliteLateClaimCompletionCannotOverwriteNewerWinner(bool lateAccepted, bool winnerCompleted)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=False"); await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).Options;
        await using var oldDb = new MasterAppDbContext(options); await oldDb.Database.EnsureCreatedAsync();
        var oldLead = Lead(); oldDb.WebsiteLeads.Add(oldLead); await oldDb.SaveChangesAsync();
        Assert.True(await WebsiteLeadNotificationAuthority.TryClaimAsync(oldDb, oldLead));
        await using var newDb = new MasterAppDbContext(options);
        await newDb.WebsiteLeads.Where(x => x.Id == oldLead.Id).ExecuteUpdateAsync(update => update
            .SetProperty(x => x.NotificationAttemptUtc, DateTime.UtcNow.AddHours(-1)).SetProperty(x => x.Status, "Contacted"));
        var winner = await newDb.WebsiteLeads.SingleAsync(x => x.Id == oldLead.Id);
        Assert.True(await WebsiteLeadNotificationAuthority.TryClaimAsync(newDb, winner));
        if (winnerCompleted) await WebsiteLeadNotificationAuthority.CompleteAsync(newDb, winner, true);
        var winnerSent = winner.NotificationSentUtc; var winnerAttempt = winner.NotificationAttemptUtc;
        await WebsiteLeadNotificationAuthority.CompleteAsync(oldDb, oldLead, lateAccepted);
        var persisted = await newDb.WebsiteLeads.AsNoTracking().SingleAsync(x => x.Id == oldLead.Id);
        Assert.Equal(winnerSent, persisted.NotificationSentUtc);
        Assert.Equal(winnerAttempt, persisted.NotificationAttemptUtc);
        Assert.Equal("Contacted", persisted.Status);
        Assert.Equal(winnerSent, oldLead.NotificationSentUtc); // Stale tracked claim was reloaded.
        oldLead.FirstName = "Unrelated later edit"; await oldDb.SaveChangesAsync();
        persisted = await newDb.WebsiteLeads.AsNoTracking().SingleAsync(x => x.Id == oldLead.Id);
        Assert.Equal(winnerSent, persisted.NotificationSentUtc); Assert.Equal(winnerAttempt, persisted.NotificationAttemptUtc);
        Assert.Equal("Contacted", persisted.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SqliteValidClaimCompletionPreservesDatabaseCurrentClosedCrmStatus(bool accepted)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=False"); await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).Options;
        await using var claimedDb = new MasterAppDbContext(options); await claimedDb.Database.EnsureCreatedAsync();
        var lead = Lead(); lead.Status = accepted ? "NotificationFailed" : "New";
        claimedDb.WebsiteLeads.Add(lead); await claimedDb.SaveChangesAsync();
        Assert.True(await WebsiteLeadNotificationAuthority.TryClaimAsync(claimedDb, lead));
        await using var crmDb = new MasterAppDbContext(options);
        await crmDb.WebsiteLeads.Where(x => x.Id == lead.Id).ExecuteUpdateAsync(update => update.SetProperty(x => x.Status, "Closed"));
        await WebsiteLeadNotificationAuthority.CompleteAsync(claimedDb, lead, accepted);
        Assert.Equal("Closed", lead.Status); Assert.Equal(accepted, lead.NotificationSentUtc.HasValue);
        Assert.Equal("Closed", (await crmDb.WebsiteLeads.AsNoTracking().SingleAsync()).Status);
    }

}
