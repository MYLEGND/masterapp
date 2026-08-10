using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Domain.Accounts;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Identity;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed class FounderAccountRemovalServiceTests
{
    [Fact]
    public async Task Directory_IncludesUnsubscribedClientsAndInactiveAgentsWithoutChangingPublicEligibility()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var lead = new ClientProfile
        {
            Id = Guid.NewGuid(),
            ClientUserId = "unsubscribed-lead",
            ExternalIdentityObjectId = "unsubscribed-lead-oid",
            FirstName = "Unsubscribed",
            LastName = "Lead",
            Email = "lead@example.test",
            CrmStatus = "Lead",
            CrmNotes = "{\"recordType\":\"Lead\"}"
        };
        var inactiveAgent = new AgentProfile
        {
            Id = Guid.NewGuid(),
            AgentUserId = "inactive-agent",
            AgentUpn = "inactive.agent@example.test",
            FullName = "Inactive Agent",
            IsActive = false
        };
        db.AddRange(lead, inactiveAgent);
        await db.SaveChangesAsync();

        var service = new FounderAccountRemovalService(
            db,
            Mock.Of<IAccountLifecycleService>(),
            Mock.Of<IAccountClosureService>(),
            NullLogger<FounderAccountRemovalService>.Instance);

        var accounts = await service.ListAsync(null, 50);

        Assert.Contains(accounts, account =>
            account.ProfileId == lead.Id &&
            account.ParticipantType == MessagingParticipantTypes.Client &&
            account.UserId == lead.ExternalIdentityObjectId &&
            !account.HasCancelableSubscription);
        Assert.Contains(accounts, account =>
            account.ProfileId == inactiveAgent.Id &&
            account.ParticipantType == MessagingParticipantTypes.Agent &&
            !account.IsActive);
    }

    [Fact]
    public async Task Remove_StartsTheCanonicalLifecycleAndRunsTheClosureImmediately()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = new ClientProfile
        {
            Id = Guid.NewGuid(),
            ClientUserId = "member-client-id",
            ExternalIdentityObjectId = "member-client-oid",
            FirstName = "Member",
            LastName = "Removal",
            Email = "member@example.test"
        };
        db.ClientProfiles.Add(profile);
        await db.SaveChangesAsync();

        var lifecycle = new AccountLifecycleService(
            db,
            Mock.Of<Domain.Billing.IBillingOrchestrator>());
        var closure = new Mock<IAccountClosureService>(MockBehavior.Strict);
        Guid executedRecordId = Guid.Empty;
        closure.Setup(service => service.ProcessAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, CancellationToken>((recordId, _) => executedRecordId = recordId)
            .ReturnsAsync(new AccountClosureExecutionResult(true, true));
        var service = new FounderAccountRemovalService(
            db,
            lifecycle,
            closure.Object,
            NullLogger<FounderAccountRemovalService>.Instance);

        var result = await service.RemoveAsync(new FounderAccountRemovalCommand(
            profile.Id,
            MessagingParticipantTypes.Client,
            "founder-oid",
            "founder-removal-test"));

        Assert.True(result.Succeeded);
        Assert.True(result.Completed);
        var record = await db.AccountLifecycleRecords.SingleAsync();
        Assert.Equal(AccountLifecycleStates.DeletionRequested, record.State);
        Assert.Equal(record.Id, executedRecordId);
        Assert.Contains(await db.AccountLifecycleAuditEntries.ToArrayAsync(), entry =>
            entry.AccountLifecycleRecordId == record.Id &&
            entry.Action == "founder_removal_requested" &&
            entry.ResultCode == "authorized");
        closure.Verify(service => service.ProcessAsync(record.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Remove_RejectsTheFounderOwnAccount()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = new AgentProfile
        {
            Id = Guid.NewGuid(),
            AgentUserId = "founder-oid",
            AgentUpn = "founder@example.test",
            FullName = "Founder",
            IsActive = true
        };
        db.AgentProfiles.Add(founder);
        await db.SaveChangesAsync();

        var closure = new Mock<IAccountClosureService>(MockBehavior.Strict);
        var service = new FounderAccountRemovalService(
            db,
            Mock.Of<IAccountLifecycleService>(),
            closure.Object,
            NullLogger<FounderAccountRemovalService>.Instance);

        var result = await service.RemoveAsync(new FounderAccountRemovalCommand(
            founder.Id,
            MessagingParticipantTypes.Agent,
            "founder-oid"));

        Assert.False(result.Succeeded);
        Assert.Equal("founder_account_removal_self_forbidden", result.ErrorCode);
        closure.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RemoveMany_UsesTheSameProtectedClosureAuthorityForEverySelectedAccount()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var first = new ClientProfile
        {
            Id = Guid.NewGuid(),
            ClientUserId = "bulk-client-one",
            ExternalIdentityObjectId = "bulk-client-one-oid",
            FirstName = "Bulk",
            LastName = "One",
            Email = "bulk-one@example.test"
        };
        var second = new ClientProfile
        {
            Id = Guid.NewGuid(),
            ClientUserId = "bulk-client-two",
            ExternalIdentityObjectId = "bulk-client-two-oid",
            FirstName = "Bulk",
            LastName = "Two",
            Email = "bulk-two@example.test"
        };
        db.AddRange(first, second);
        await db.SaveChangesAsync();

        var closure = new Mock<IAccountClosureService>(MockBehavior.Strict);
        closure.Setup(service => service.ProcessAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountClosureExecutionResult(true, true));
        var service = new FounderAccountRemovalService(
            db,
            new AccountLifecycleService(db, Mock.Of<Domain.Billing.IBillingOrchestrator>()),
            closure.Object,
            NullLogger<FounderAccountRemovalService>.Instance);

        var result = await service.RemoveManyAsync(new FounderAccountRemovalBatchCommand(
            new[]
            {
                new FounderAccountTarget(first.Id, MessagingParticipantTypes.Client),
                new FounderAccountTarget(second.Id, MessagingParticipantTypes.Client)
            },
            "founder-oid"));

        Assert.Equal(2, result.CompletedCount);
        Assert.Equal(0, result.FailedCount);
        closure.Verify(service => service.ProcessAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Directory_SeparatesArchivedAccountsFromAccountsAwaitingRemoval()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var active = new ClientProfile
        {
            Id = Guid.NewGuid(),
            ClientUserId = "active-client",
            ExternalIdentityObjectId = "active-client-oid",
            FirstName = "Active",
            LastName = "Client",
            Email = "active@example.test"
        };
        var archived = new ClientProfile
        {
            Id = Guid.NewGuid(),
            ClientUserId = "archived-client",
            ExternalIdentityObjectId = "archived-client-oid",
            FirstName = "Archived",
            LastName = "Client",
            Email = "archived@example.test"
        };
        db.AddRange(active, archived, new AccountLifecycleRecord
        {
            ProfileId = archived.Id,
            UserId = archived.ExternalIdentityObjectId!,
            ParticipantType = MessagingParticipantTypes.Client,
            State = AccountLifecycleStates.Closed,
            ClosedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var service = new FounderAccountRemovalService(
            db,
            Mock.Of<IAccountLifecycleService>(),
            Mock.Of<IAccountClosureService>(),
            NullLogger<FounderAccountRemovalService>.Instance);

        var accounts = await service.ListAsync(null, 50, FounderAccountDirectoryScope.Active);
        var archive = await service.ListAsync(null, 50, FounderAccountDirectoryScope.Archive);

        Assert.Contains(accounts, account => account.ProfileId == active.Id);
        Assert.DoesNotContain(accounts, account => account.ProfileId == archived.Id);
        Assert.DoesNotContain(archive, account => account.ProfileId == active.Id);
        Assert.Contains(archive, account => account.ProfileId == archived.Id && account.LifecycleState == AccountLifecycleStates.Closed);
    }

    [Fact]
    public async Task PurgeArchived_ErasesOnlyAClosedAccountAndItsAccountProfileRows()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = new ClientProfile
        {
            Id = Guid.NewGuid(),
            ClientUserId = "erased-client",
            ExternalIdentityObjectId = "erased-client-oid",
            FirstName = "Erased",
            LastName = "Client",
            Email = "erased@example.test"
        };
        db.AddRange(profile,
            new AccountLifecycleRecord
            {
                ProfileId = profile.Id,
                UserId = profile.ExternalIdentityObjectId!,
                ParticipantType = MessagingParticipantTypes.Client,
                State = AccountLifecycleStates.Closed,
                ClosedUtc = DateTime.UtcNow
            },
            new MobileProfileSettings
            {
                ProfileId = profile.Id,
                ParticipantType = MessagingParticipantTypes.Client,
                Username = "erased-client"
            });
        await db.SaveChangesAsync();

        var service = new FounderAccountRemovalService(
            db,
            new AccountLifecycleService(db, Mock.Of<Domain.Billing.IBillingOrchestrator>()),
            Mock.Of<IAccountClosureService>(),
            NullLogger<FounderAccountRemovalService>.Instance);

        var result = await service.PurgeArchivedAsync(new FounderAccountPurgeCommand(
            profile.Id,
            MessagingParticipantTypes.Client,
            "founder-oid"));

        Assert.True(result.Succeeded);
        Assert.False(await db.ClientProfiles.AnyAsync(item => item.Id == profile.Id));
        Assert.False(await db.MobileProfileSettings.AnyAsync(item => item.ProfileId == profile.Id));
        Assert.False(await db.AccountLifecycleRecords.AnyAsync(item => item.ProfileId == profile.Id));
    }

    [Fact]
    public async Task PurgeArchived_RejectsAnyAccountThatHasNotCompletedClosure()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = new ClientProfile
        {
            Id = Guid.NewGuid(),
            ClientUserId = "active-purge-client",
            ExternalIdentityObjectId = "active-purge-client-oid",
            FirstName = "Active",
            LastName = "Purge",
            Email = "active-purge@example.test"
        };
        db.ClientProfiles.Add(profile);
        await db.SaveChangesAsync();

        var service = new FounderAccountRemovalService(
            db,
            new AccountLifecycleService(db, Mock.Of<Domain.Billing.IBillingOrchestrator>()),
            Mock.Of<IAccountClosureService>(),
            NullLogger<FounderAccountRemovalService>.Instance);

        var result = await service.PurgeArchivedAsync(new FounderAccountPurgeCommand(
            profile.Id,
            MessagingParticipantTypes.Client,
            "founder-oid"));

        Assert.False(result.Succeeded);
        Assert.Equal("founder_account_purge_not_archived", result.ErrorCode);
        Assert.True(await db.ClientProfiles.AnyAsync(item => item.Id == profile.Id));
    }

    [Fact]
    public async Task PurgeArchived_RemovesAClosedClientAcrossTheRelationalBillingGraph()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new MasterAppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var profile = new ClientProfile
        {
            Id = Guid.NewGuid(),
            ClientUserId = "relational-purge-client",
            ExternalIdentityObjectId = "relational-purge-client-oid",
            FirstName = "Relational",
            LastName = "Purge",
            Email = "relational-purge@example.test"
        };
        var offer = new ClientSubscriptionOffer { ClientProfileId = profile.Id, OwnerAgentUserId = "owner" };
        var paymentMethod = new ClientPaymentMethod
        {
            ClientProfileId = profile.Id,
            ProviderPaymentMethodId = "pm_relational_purge"
        };
        var subscription = new ClientSubscription
        {
            ClientProfileId = profile.Id,
            AcceptedOfferId = offer.Id,
            DefaultPaymentMethodId = paymentMethod.Id,
            OwnerAgentUserId = "owner"
        };
        db.AddRange(profile, offer, paymentMethod, subscription,
            new ClientBillingNotification
            {
                ClientProfileId = profile.Id,
                ClientSubscriptionId = subscription.Id,
                EventKey = "relational-purge-notification",
                Subject = "Removed",
                PlainTextBody = "Removed"
            });
        await db.SaveChangesAsync();
        var lifecycleId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "AccountLifecycleRecords" (
                "Id", "UserId", "ParticipantType", "ProfileId", "State",
                "ClosedUtc", "ClosureAttemptCount", "CreatedUtc", "UpdatedUtc", "RowVersion")
            VALUES (
                {lifecycleId}, {profile.ExternalIdentityObjectId!}, {MessagingParticipantTypes.Client}, {profile.Id}, {AccountLifecycleStates.Closed},
                {now}, {0}, {now}, {now}, X'01')
            """);
        db.ChangeTracker.Clear();

        var service = new FounderAccountRemovalService(
            db,
            new AccountLifecycleService(db, Mock.Of<Domain.Billing.IBillingOrchestrator>()),
            Mock.Of<IAccountClosureService>(),
            NullLogger<FounderAccountRemovalService>.Instance);

        var result = await service.PurgeArchivedAsync(new FounderAccountPurgeCommand(
            profile.Id,
            MessagingParticipantTypes.Client,
            "founder-oid"));

        Assert.True(result.Succeeded);
        Assert.Empty(await db.ClientProfiles.ToArrayAsync());
        Assert.Empty(await db.ClientSubscriptions.ToArrayAsync());
        Assert.Empty(await db.ClientSubscriptionOffers.ToArrayAsync());
        Assert.Empty(await db.ClientPaymentMethods.ToArrayAsync());
        Assert.Empty(await db.ClientBillingNotifications.ToArrayAsync());
    }
}
