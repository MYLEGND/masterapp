using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Domain.Accounts;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Identity;
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
}
