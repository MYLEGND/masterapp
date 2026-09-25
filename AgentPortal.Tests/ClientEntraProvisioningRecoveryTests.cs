using System;
using System.Threading;
using System.Threading.Tasks;
using Domain.Billing;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed class ClientEntraProvisioningRecoveryTests
{
    [Fact]
    public async Task Recovery_OnlyRepairsActiveClientsMissingIdentityBinding()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var pending = AddProfile(db, "pending@example.com");
        var alreadyBound = AddProfile(db, "bound@example.com", "bound-object-id");
        var inactive = AddProfile(db, "inactive@example.com");

        db.ClientSubscriptions.AddRange(
            ActiveSubscription(pending.Id),
            ActiveSubscription(alreadyBound.Id),
            new ClientSubscription
            {
                Id = Guid.NewGuid(),
                ClientProfileId = inactive.Id,
                OwnerAgentUserId = "agent-1",
                MonthlyAmountCents = 10000,
                Currency = "USD",
                Status = ClientSubscriptionStatus.Canceled,
                PaymentStanding = ClientSubscriptionPaymentStanding.Current,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            });
        await db.SaveChangesAsync();

        var lifecycle = new Mock<IClientEntraLifecycleService>(MockBehavior.Strict);
        lifecycle
            .Setup(x => x.EnsureClientIdentityAsync(pending.Id, It.IsAny<CancellationToken>()))
            .Returns<Guid, CancellationToken>((_, ct) =>
            {
                pending.ExternalIdentityObjectId = "new-object-id";
                pending.UpdatedUtc = DateTime.UtcNow;
                return PersistAndReturnAsync(db, pending, ct);
            });

        var recovered = await ClientEntraProvisioningRecoveryHostedService.RecoverPendingAsync(
            db,
            lifecycle.Object,
            NullLogger.Instance);

        Assert.Equal(1, recovered);
        Assert.Equal("new-object-id", pending.ExternalIdentityObjectId);
        lifecycle.Verify(
            x => x.EnsureClientIdentityAsync(pending.Id, It.IsAny<CancellationToken>()),
            Times.Once);
        lifecycle.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Recovery_TransientFailureLeavesBillingUntouchedAndRetriesNextCycle()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = AddProfile(db, "retry@example.com");
        var subscription = ActiveSubscription(profile.Id);
        db.ClientSubscriptions.Add(subscription);
        await db.SaveChangesAsync();

        var attempts = 0;
        var lifecycle = new Mock<IClientEntraLifecycleService>(MockBehavior.Strict);
        lifecycle
            .Setup(x => x.EnsureClientIdentityAsync(profile.Id, It.IsAny<CancellationToken>()))
            .Returns<Guid, CancellationToken>((_, ct) =>
            {
                attempts++;
                if (attempts == 1)
                    throw new InvalidOperationException("simulated Graph outage");

                profile.ExternalIdentityObjectId = "recovered-object-id";
                profile.UpdatedUtc = DateTime.UtcNow;
                return PersistAndReturnAsync(db, profile, ct);
            });

        var first = await ClientEntraProvisioningRecoveryHostedService.RecoverPendingAsync(
            db,
            lifecycle.Object,
            NullLogger.Instance);
        Assert.Equal(0, first);
        Assert.Null(profile.ExternalIdentityObjectId);
        Assert.Equal(ClientSubscriptionStatus.Active, subscription.Status);
        Assert.Equal(ClientSubscriptionPaymentStanding.Current, subscription.PaymentStanding);

        var second = await ClientEntraProvisioningRecoveryHostedService.RecoverPendingAsync(
            db,
            lifecycle.Object,
            NullLogger.Instance);
        Assert.Equal(1, second);
        Assert.Equal("recovered-object-id", profile.ExternalIdentityObjectId);
        Assert.Equal(ClientSubscriptionStatus.Active, subscription.Status);
        Assert.Equal(ClientSubscriptionPaymentStanding.Current, subscription.PaymentStanding);
    }

    private static ClientProfile AddProfile(
        MasterAppDbContext db,
        string email,
        string? externalIdentityObjectId = null)
    {
        var profile = new ClientProfile
        {
            Id = Guid.NewGuid(),
            ClientUserId = Guid.NewGuid().ToString("D"),
            ExternalIdentityObjectId = externalIdentityObjectId,
            FirstName = "Test",
            LastName = "Client",
            Email = email,
            NormalizedEmail = email.ToLowerInvariant(),
            Phone = "5550001234",
            MaritalStatus = "Single",
            CrmStatus = "Active",
            CrmNotes = "{\"recordType\":\"Client\",\"pipelineStage\":\"Client\"}",
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };
        db.ClientProfiles.Add(profile);
        return profile;
    }

    private static ClientSubscription ActiveSubscription(Guid clientProfileId) =>
        new()
        {
            Id = Guid.NewGuid(),
            ClientProfileId = clientProfileId,
            OwnerAgentUserId = "agent-1",
            MonthlyAmountCents = 10000,
            Currency = "USD",
            Status = ClientSubscriptionStatus.Active,
            PaymentStanding = ClientSubscriptionPaymentStanding.Current,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

    private static async Task<ClientEntraIdentityResult> PersistAndReturnAsync(
        MasterAppDbContext db,
        ClientProfile profile,
        CancellationToken cancellationToken)
    {
        await db.SaveChangesAsync(cancellationToken);
        return new ClientEntraIdentityResult(
            profile.ExternalIdentityObjectId!,
            profile.NormalizedEmail ?? profile.Email,
            false,
            false);
    }
}
