using Domain.Entities;
using Domain.Accounts;
using System.Reflection;
using Domain.Messaging;
using Infrastructure.Data;
using Infrastructure.Leads;
using Infrastructure.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public class WebsiteLeadAppNotificationTests
{
    private static MasterAppDbContext BuildDb() => new(new DbContextOptionsBuilder<MasterAppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static WebsiteLead Lead(Guid businessId) => new()
    {
        LeadId = Guid.NewGuid(), FirstName = "Fixture", Email = "lead@example.test",
        CommerceBusinessId = businessId, CreatedUtc = DateTime.UtcNow
    };

    private static WebsiteLeadAppNotificationService Service(MasterAppDbContext db, Mock<INotificationEngine> engine, string? founder = null) =>
        new(db, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Founder:Email"] = founder }).Build(), engine.Object, NullLogger<WebsiteLeadAppNotificationService>.Instance);

    [Fact]
    public async Task MatchingEmailWithoutPersistedMembershipCannotReceiveBusinessLead()
    {
        using var db = BuildDb();
        var business = new CommerceBusiness { Key = "fixture", OwnerEmail = "shared@example.test" };
        db.CommerceBusinesses.Add(business);
        db.ClientProfiles.Add(new ClientProfile { ClientUserId = "unrelated", Email = business.OwnerEmail });
        db.WebsiteLeads.Add(Lead(business.Id));
        await db.SaveChangesAsync();
        var engine = new Mock<INotificationEngine>(MockBehavior.Strict);
        await Service(db, engine).DeliverPendingAsync(default);
        engine.Verify(x => x.StageAsync(It.IsAny<MobileActivityNotification>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(await db.MobileActivityNotifications.ToListAsync());
    }

    [Fact]
    public async Task RevokedMemberCannotReceiveBusinessLeadThroughMatchingEmail()
    {
        using var db = BuildDb();
        var business = new CommerceBusiness { Key = "fixture", OwnerEmail = "owner@example.test" };
        var profile = new ClientProfile { ClientUserId = "revoked", Email = business.OwnerEmail };
        db.CommerceBusinesses.Add(business);
        db.ClientProfiles.Add(profile);
        db.CommerceBusinessMembers.Add(new CommerceBusinessMember { CommerceBusinessId = business.Id, ClientProfileId = profile.Id,
            Email = profile.Email, NormalizedEmail = profile.Email, RoleKey = "account", Status = "Revoked" });
        db.WebsiteLeads.Add(Lead(business.Id));
        await db.SaveChangesAsync();
        var engine = new Mock<INotificationEngine>(MockBehavior.Strict);
        await Service(db, engine).DeliverPendingAsync(default);
        engine.Verify(x => x.StageAsync(It.IsAny<MobileActivityNotification>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(await db.MobileActivityNotifications.ToListAsync());
    }
    private static (CommerceBusiness Business, ClientProfile Profile, CommerceBusinessMember Member) AddOwner(MasterAppDbContext db, string key, string user, string role = "account")
    {
        var business = new CommerceBusiness { Key = key, OwnerEmail = key + "@example.test" };
        var profile = new ClientProfile { ClientUserId = user, Email = business.OwnerEmail };
        var member = new CommerceBusinessMember { CommerceBusinessId = business.Id, ClientProfileId = profile.Id,
            Email = profile.Email, NormalizedEmail = profile.Email, RoleKey = role };
        db.CommerceBusinesses.Add(business);
        db.ClientProfiles.Add(profile);
        db.CommerceBusinessMembers.Add(member);
        return (business, profile, member);
    }

    private static Mock<INotificationEngine> LedgerProjection(MasterAppDbContext db)
    {
        var engine = new Mock<INotificationEngine>(MockBehavior.Strict);
        engine.Setup(x => x.StageAsync(It.IsAny<MobileActivityNotification>(), It.IsAny<CancellationToken>()))
            .Callback<MobileActivityNotification, CancellationToken>((row, _) => db.MobileActivityNotifications.Add(row))
            .Returns(Task.CompletedTask);
        engine.Setup(x => x.NotifyCommittedMessages());
        return engine;
    }

    [Theory]
    [InlineData("account")]
    [InlineData("owner")]
    [InlineData("platform-owner")]
    public async Task LinkedPrimaryMemberReceivesLeadAfterProfileEmailChanges(string role)
    {
        using var db = BuildDb();
        var owner = AddOwner(db, "business", "CANONICAL-USER", role);
        owner.Profile.Email = "changed@example.test";
        // An unrelated actor holding the old address must never replace the permanent profile link.
        db.ClientProfiles.Add(new ClientProfile { ClientUserId = "unrelated", Email = owner.Business.OwnerEmail });
        db.WebsiteLeads.Add(Lead(owner.Business.Id));
        await db.SaveChangesAsync();
        var engine = LedgerProjection(db);
        await Service(db, engine).DeliverPendingAsync(default);
        var row = Assert.Single(await db.MobileActivityNotifications.ToListAsync());
        Assert.Equal("canonical-user", row.RecipientUserId);
        Assert.Equal(MessagingParticipantTypes.Client, row.RecipientParticipantType);
        engine.Verify(x => x.NotifyCommittedMessages(), Times.Once);
    }

    [Theory]
    [InlineData("inactive-business")]
    [InlineData("inactive-status")]
    [InlineData("inactive-member")]
    [InlineData("missing-profile")]
    [InlineData("empty-profile")]
    [InlineData("empty-user")]
    [InlineData("non-primary")]
    [InlineData("ambiguous")]
    [InlineData("ambiguous-owner-email")]
    public async Task InvalidBusinessRecipientAuthorityFailsClosed(string defect)
    {
        using var db = BuildDb();
        var owner = AddOwner(db, "business", "owner");
        switch (defect)
        {
            case "inactive-business": owner.Business.IsActive = false; break;
            case "inactive-status": owner.Business.Status = "Inactive"; break;
            case "inactive-member": owner.Member.Status = "Inactive"; break;
            case "missing-profile": owner.Member.ClientProfileId = Guid.NewGuid(); break;
            case "empty-profile": owner.Member.ClientProfileId = Guid.Empty; break;
            case "empty-user": owner.Profile.ClientUserId = " "; break;
            case "non-primary": owner.Member.RoleKey = "staff"; owner.Member.CanManageOrders = false; break;
            default:
                var other = new ClientProfile { ClientUserId = "other", Email = "other@example.test" };
                db.ClientProfiles.Add(other);
                db.CommerceBusinessMembers.Add(new CommerceBusinessMember { CommerceBusinessId = owner.Business.Id, ClientProfileId = other.Id,
                    RoleKey = "owner", Email = defect == "ambiguous-owner-email" ? owner.Member.Email : other.Email,
                    NormalizedEmail = defect == "ambiguous-owner-email" ? owner.Member.NormalizedEmail : other.Email });
                if (defect == "ambiguous") owner.Business.OwnerEmail = "unmatched@example.test";
                break;
        }
        db.WebsiteLeads.Add(Lead(owner.Business.Id));
        await db.SaveChangesAsync();
        var engine = new Mock<INotificationEngine>(MockBehavior.Strict);
        await Service(db, engine).DeliverPendingAsync(default);
        engine.Verify(x => x.StageAsync(It.IsAny<MobileActivityNotification>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(await db.MobileActivityNotifications.ToListAsync());
    }

    [Theory]
    [InlineData(AccountLifecycleStates.Paused)]
    [InlineData(AccountLifecycleStates.DeletionRequested)]
    [InlineData(AccountLifecycleStates.Closed)]
    public async Task ExistingLifecycleAuthorityBlocksBusinessRecipient(string state)
    {
        using var db = BuildDb();
        var owner = AddOwner(db, "business", "owner");
        db.AccountLifecycleRecords.Add(new AccountLifecycleRecord { ProfileId = owner.Profile.Id, UserId = owner.Profile.ClientUserId,
            ParticipantType = MessagingParticipantTypes.Client, State = state });
        db.WebsiteLeads.Add(Lead(owner.Business.Id));
        await db.SaveChangesAsync();
        var engine = new Mock<INotificationEngine>(MockBehavior.Strict);
        await Service(db, engine).DeliverPendingAsync(default);
        engine.Verify(x => x.StageAsync(It.IsAny<MobileActivityNotification>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task OwnerEmailSelectsOnlyAnAlreadyLinkedPrimaryMembership()
    {
        using var db = BuildDb();
        var owner = AddOwner(db, "business", "primary");
        var other = new ClientProfile { ClientUserId = "co-owner", Email = "other@example.test" };
        db.ClientProfiles.Add(other);
        db.CommerceBusinessMembers.Add(new CommerceBusinessMember { CommerceBusinessId = owner.Business.Id, ClientProfileId = other.Id,
            RoleKey = "owner", Email = other.Email, NormalizedEmail = other.Email });
        db.WebsiteLeads.Add(Lead(owner.Business.Id));
        await db.SaveChangesAsync();
        await Service(db, LedgerProjection(db)).DeliverPendingAsync(default);
        Assert.Equal("primary", Assert.Single(await db.MobileActivityNotifications.ToListAsync()).RecipientUserId);
    }

    [Theory]
    [InlineData("empty-business")]
    [InlineData("empty-agent")]
    [InlineData("dual-owner")]
    [InlineData("business-empty-agent")]
    public async Task InvalidTypedOwnerScopeCannotFallThroughToAnotherRecipient(string defect)
    {
        using var db = BuildDb();
        var owner = AddOwner(db, "business", "owner");
        var tracking = new AgentTrackingProfile { AgentUserId = "agent", AgentUpn = "agent@example.test", Slug = "agent" };
        db.AgentTrackingProfiles.Add(tracking);
        db.AgentProfiles.Add(new AgentProfile { AgentUserId = "founder", AgentUpn = "founder@example.test", NormalizedEmail = "founder@example.test" });
        var lead = Lead(owner.Business.Id);
        if (defect == "empty-business") lead.CommerceBusinessId = Guid.Empty;
        if (defect == "empty-agent") { lead.CommerceBusinessId = null; lead.AgentTrackingProfileId = Guid.Empty; }
        if (defect == "dual-owner") lead.AgentTrackingProfileId = tracking.Id;
        if (defect == "business-empty-agent") lead.AgentTrackingProfileId = Guid.Empty;
        db.WebsiteLeads.Add(lead);
        await db.SaveChangesAsync();
        var engine = new Mock<INotificationEngine>(MockBehavior.Strict);
        await Service(db, engine, "founder@example.test").DeliverPendingAsync(default);
        engine.Verify(x => x.StageAsync(It.IsAny<MobileActivityNotification>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingFounderAndAgentRecipientsRemainScoped(bool agentOwned)
    {
        using var db = BuildDb();
        var tracking = new AgentTrackingProfile { AgentUserId = "agent", AgentUpn = "agent@example.test", Slug = "agent" };
        db.AgentTrackingProfiles.Add(tracking);
        db.AgentProfiles.Add(new AgentProfile { AgentUserId = "founder", AgentUpn = "founder@example.test", NormalizedEmail = "founder@example.test" });
        var lead = Lead(Guid.NewGuid()); lead.CommerceBusinessId = null;
        lead.AgentTrackingProfileId = agentOwned ? tracking.Id : null;
        db.WebsiteLeads.Add(lead);
        await db.SaveChangesAsync();
        await Service(db, LedgerProjection(db), "founder@example.test").DeliverPendingAsync(default);
        var row = Assert.Single(await db.MobileActivityNotifications.ToListAsync());
        Assert.Equal(agentOwned ? "agent" : "founder", row.RecipientUserId);
        Assert.Equal(MessagingParticipantTypes.Agent, row.RecipientParticipantType);
    }

    [Fact]
    public async Task TwoBusinessesWithSharedAddressUseLinkedProfilesAndReplayKeepsSameLedger()
    {
        using var db = BuildDb();
        var first = AddOwner(db, "first", "first-user");
        var second = AddOwner(db, "second", "second-user", "owner");
        second.Business.OwnerEmail = first.Business.OwnerEmail;
        second.Profile.Email = first.Profile.Email;
        second.Member.Email = first.Member.Email;
        second.Member.NormalizedEmail = first.Member.NormalizedEmail;
        db.WebsiteLeads.AddRange(Lead(first.Business.Id), Lead(second.Business.Id));
        await db.SaveChangesAsync();
        var engine = LedgerProjection(db);
        await Service(db, engine).DeliverPendingAsync(default);
        var initial = await db.MobileActivityNotifications.OrderBy(x => x.RecipientUserId).ToListAsync();
        Assert.Equal(new[] { "first-user", "second-user" }, initial.Select(x => x.RecipientUserId));
        await Service(db, engine).DeliverPendingAsync(default);
        var replay = await db.MobileActivityNotifications.OrderBy(x => x.RecipientUserId).ToListAsync();
        Assert.Equal(initial.Select(x => x.Id), replay.Select(x => x.Id));
        engine.Verify(x => x.StageAsync(It.IsAny<MobileActivityNotification>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        engine.Verify(x => x.NotifyCommittedMessages(), Times.Exactly(2));
    }

    private static Guid ExistingNotificationId(Guid leadId) => (Guid)typeof(WebsiteLeadAppNotificationService)
        .GetMethod("NotificationId", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { leadId })!;

    [Fact]
    public async Task DeliveredPrefixCannotHideNextPendingLead()
    {
        using var db = BuildDb();
        var owner = AddOwner(db, "business", "owner");
        for (var index = 1; index <= 105; index++)
        {
            var delivered = Lead(owner.Business.Id); delivered.LeadId = new Guid($"00000000-0000-0000-0000-{index:x12}");
            delivered.CreatedUtc = DateTime.UtcNow.AddHours(-1);
            db.WebsiteLeads.Add(delivered);
            db.MobileActivityNotifications.Add(new MobileActivityNotification { Id = ExistingNotificationId(delivered.LeadId),
                RecipientUserId = "owner", RecipientParticipantType = MessagingParticipantTypes.Client, Kind = "WebsiteLead" });
        }
        var pending = Lead(owner.Business.Id); pending.LeadId = new Guid("00000000-0000-0000-0000-000000000106");
        db.WebsiteLeads.Add(pending);
        await db.SaveChangesAsync();
        var engine = LedgerProjection(db);
        await Service(db, engine).DeliverPendingAsync(default);
        engine.Verify(x => x.StageAsync(It.Is<MobileActivityNotification>(row => row.Id == ExistingNotificationId(pending.LeadId)), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(106, await db.MobileActivityNotifications.CountAsync());
    }

    [Fact]
    public async Task EligibleLeadAfterDowntimeDoesNotDisappearByAge()
    {
        using var db = BuildDb();
        var owner = AddOwner(db, "business", "owner");
        var pending = Lead(owner.Business.Id); pending.CreatedUtc = DateTime.UtcNow.AddDays(-10);
        db.WebsiteLeads.Add(pending);
        await db.SaveChangesAsync();
        var engine = LedgerProjection(db);
        await Service(db, engine).DeliverPendingAsync(default);
        Assert.Equal(ExistingNotificationId(pending.LeadId), Assert.Single(await db.MobileActivityNotifications.ToListAsync()).Id);
    }

    [Fact]
    public async Task UnresolvablePrefixAndDeletedLeadDoNotHideEligibleBacklog()
    {
        using var db = BuildDb();
        var owner = AddOwner(db, "business", "owner");
        for (var index = 0; index < 105; index++) db.WebsiteLeads.Add(Lead(Guid.NewGuid()));
        var deleted = Lead(owner.Business.Id); deleted.IsDeleted = true;
        var pending = Lead(owner.Business.Id);
        db.WebsiteLeads.AddRange(deleted, pending);
        await db.SaveChangesAsync();
        var engine = LedgerProjection(db);
        await Service(db, engine).DeliverPendingAsync(default);
        Assert.Equal(ExistingNotificationId(pending.LeadId), Assert.Single(await db.MobileActivityNotifications.ToListAsync()).Id);
        engine.Verify(x => x.StageAsync(It.IsAny<MobileActivityNotification>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EachPassProjectsAtMost100AndNextPassAdvancesWithoutDuplicateLedger()
    {
        using var db = BuildDb();
        var owner = AddOwner(db, "business", "owner");
        for (var index = 0; index < 121; index++) db.WebsiteLeads.Add(Lead(owner.Business.Id));
        await db.SaveChangesAsync();
        var engine = LedgerProjection(db);
        await Service(db, engine).DeliverPendingAsync(default);
        Assert.Equal(100, await db.MobileActivityNotifications.CountAsync());
        await Service(db, engine).DeliverPendingAsync(default);
        Assert.Equal(121, await db.MobileActivityNotifications.CountAsync());
        await Service(db, engine).DeliverPendingAsync(default);
        Assert.Equal(121, await db.MobileActivityNotifications.CountAsync());
        engine.Verify(x => x.StageAsync(It.IsAny<MobileActivityNotification>(), It.IsAny<CancellationToken>()), Times.Exactly(121));
    }

    [Fact]
    public async Task PassStopsAtCapturedIdentityMaximumAndNextPassIncludesNewArrival()
    {
        using var db = BuildDb();
        var owner = AddOwner(db, "business", "owner");
        var initial = Lead(owner.Business.Id); initial.Id = 10;
        var arrival = Lead(owner.Business.Id); arrival.Id = 20;
        db.WebsiteLeads.Add(initial);
        await db.SaveChangesAsync();
        var engine = LedgerProjection(db);
        var inserted = false;
        engine.Setup(x => x.NotifyCommittedMessages()).Callback(() =>
        {
            if (inserted) return;
            inserted = true;
            db.WebsiteLeads.Add(arrival);
            db.SaveChanges(); // Fixture models a committed arrival after the pass captured its maximum.
        });
        await Service(db, engine).DeliverPendingAsync(default);
        Assert.Equal(ExistingNotificationId(initial.LeadId), Assert.Single(await db.MobileActivityNotifications.ToListAsync()).Id);
        Assert.Equal(2, await db.WebsiteLeads.CountAsync());
        await Service(db, engine).DeliverPendingAsync(default);
        Assert.Equal(2, await db.MobileActivityNotifications.CountAsync());
        Assert.True(await db.MobileActivityNotifications.AnyAsync(x => x.Id == ExistingNotificationId(arrival.LeadId)));
    }

    [Fact]
    public async Task CancellationStopsPassAndRetryKeepsCommittedProjectionExactlyOnce()
    {
        using var db = BuildDb();
        var owner = AddOwner(db, "business", "owner");
        db.WebsiteLeads.AddRange(Lead(owner.Business.Id), Lead(owner.Business.Id));
        await db.SaveChangesAsync();
        using var cancellation = new CancellationTokenSource();
        var engine = LedgerProjection(db);
        engine.Setup(x => x.NotifyCommittedMessages()).Callback(cancellation.Cancel);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(db, engine).DeliverPendingAsync(cancellation.Token));
        Assert.Single(await db.MobileActivityNotifications.ToListAsync());
        engine.Setup(x => x.NotifyCommittedMessages());
        await Service(db, engine).DeliverPendingAsync(default);
        Assert.Equal(2, await db.MobileActivityNotifications.CountAsync());
        engine.Verify(x => x.StageAsync(It.IsAny<MobileActivityNotification>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Theory]
    [InlineData(true, "public.example.test")]
    [InlineData(false, "localhost:5000")]
    public async Task ExistingCaptureSafetySuppressesInternalAndLocalLeadProjection(bool internalLead, string host)
    {
        using var db = BuildDb();
        var owner = AddOwner(db, "business", "owner");
        var lead = Lead(owner.Business.Id); lead.IsInternal = internalLead; lead.Host = host;
        db.WebsiteLeads.Add(lead);
        await db.SaveChangesAsync();
        var engine = new Mock<INotificationEngine>(MockBehavior.Strict);
        await Service(db, engine).DeliverPendingAsync(default);
        engine.Verify(x => x.StageAsync(It.IsAny<MobileActivityNotification>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(await db.MobileActivityNotifications.ToListAsync());
    }

    [Fact]
    public void ActualPendingBatchTranslatesBoundedIdentityKeysetForSqlServer()
    {
        using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseSqlServer("Server=(local);Database=translation_only;Integrated Security=true;TrustServerCertificate=true").Options);
        var query = (IQueryable<WebsiteLead>)typeof(WebsiteLeadAppNotificationService)
            .GetMethod("PendingBatch", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object?[] { db, (long?)100, 200L })!;
        var sql = query.ToQueryString();
        Assert.Contains("TOP(", sql);
        Assert.Contains("[Id] <=", sql);
        Assert.Contains("[Id] >", sql);
        Assert.Contains("[IsDeleted]", sql);
        Assert.Contains("ORDER BY", sql);
        Assert.DoesNotContain("[CreatedUtc] >=", sql);
        Console.WriteLine("SQL translation only: canonical pending batch\n" + sql);
    }

}
