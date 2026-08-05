using AgentPortal.Mobile;
using System;
using System.Threading;
using System.Threading.Tasks;
using Domain.Accounts;
using Domain.Billing;
using Domain.Entities;
using Domain.Messaging;
using Domain.Social;
using Infrastructure.Identity;
using Infrastructure.Mobile;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed class AccountLifecycleServiceTests
{
    [Fact]
    public async Task ClosureExecutor_ClosesClientOnlyAfterAuthoritativeOperationsComplete()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = new ClientProfile
        {
            Id = Guid.NewGuid(),
            ClientUserId = "client-stable-id",
            ExternalIdentityObjectId = "client-entra-id",
            FirstName = "Closing",
            LastName = "Client",
            Email = "closing@example.test",
            Phone = "555-0100",
            DOB = new DateTime(1990, 1, 1),
            AgentNotes = "Remove with account presentation data."
        };
        var lifecycle = new AccountLifecycleRecord
        {
            Id = Guid.NewGuid(),
            UserId = "client-entra-id",
            ParticipantType = MessagingParticipantTypes.Client,
            ProfileId = profile.Id,
            State = AccountLifecycleStates.DeletionRequested,
            DeletionRequestedUtc = DateTime.UtcNow
        };
        var subscription = new ClientSubscription
        {
            Id = Guid.NewGuid(),
            ClientProfileId = profile.Id,
            Status = ClientSubscriptionStatus.Active
        };
        var firstDevice = new MobilePushDevice
        {
            UserId = "client-entra-id",
            ParticipantType = MessagingParticipantTypes.Client,
            DeviceToken = "first-token",
            TokenHash = "first-hash"
        };
        var historicalDevice = new MobilePushDevice
        {
            UserId = "client-stable-id",
            ParticipantType = MessagingParticipantTypes.Client,
            DeviceToken = "second-token",
            TokenHash = "second-hash"
        };
        db.AddRange(profile, lifecycle, subscription, firstDevice, historicalDevice);
        db.MobileProfileSettings.Add(new MobileProfileSettings
        {
            ProfileId = profile.Id,
            ParticipantType = MessagingParticipantTypes.Client,
            Username = "closing-client"
        });
        await db.SaveChangesAsync();

        var billing = new Mock<IBillingOrchestrator>();
        billing.Setup(service => service.CancelClientSubscriptionAsync(
                It.IsAny<CancelClientSubscriptionCommand>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CancellationSucceeded());
        var entra = new Mock<IClientEntraLifecycleService>();
        entra.Setup(service => service.DeleteClientIdentityAsync(profile.Id, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var social = new Mock<ISocialFeedService>();
        social.Setup(service => service.RemoveAccountContentForClosureAsync(
                It.IsAny<SocialFeedActor>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(SocialOperationResult<SocialAccountClosureDisposition>.Success(
                new SocialAccountClosureDisposition(0, 0, 0)));
        var executor = new AccountClosureService(
            db,
            billing.Object,
            entra.Object,
            social.Object,
            NullLogger<AccountClosureService>.Instance);

        var result = await executor.ProcessAsync(lifecycle.Id);

        Assert.True(result.Claimed);
        Assert.True(result.Closed);
        var completed = await db.AccountLifecycleRecords.SingleAsync();
        Assert.Equal(AccountLifecycleStates.Closed, completed.State);
        Assert.NotNull(completed.ClosedUtc);
        Assert.Null(completed.ClosureLeaseId);
        Assert.All(await db.MobilePushDevices.ToArrayAsync(), device =>
        {
            Assert.False(device.IsActive);
            Assert.NotNull(device.InvalidatedUtc);
        });
        Assert.Empty(await db.MobileProfileSettings.ToArrayAsync());
        var redacted = await db.ClientProfiles.SingleAsync();
        Assert.Equal("Closed", redacted.FirstName);
        Assert.Equal("Account", redacted.LastName);
        Assert.Empty(redacted.Phone);
        Assert.Null(redacted.DOB);
        Assert.Empty(redacted.AgentNotes);
        Assert.Contains(await db.AccountLifecycleAuditEntries.ToArrayAsync(), entry =>
            entry.Action == "closure_completed" && entry.ResultCode == "closed");
        billing.Verify(service => service.CancelClientSubscriptionAsync(
            It.Is<CancelClientSubscriptionCommand>(command =>
                command.ClientSubscriptionId == subscription.Id &&
                !command.CancelAtPeriodEnd &&
                command.ActorType == BillingActorType.System),
            It.IsAny<CancellationToken>()), Times.Once);
        entra.Verify(service => service.DeleteClientIdentityAsync(profile.Id, It.IsAny<CancellationToken>()), Times.Once);
        social.Verify(service => service.RemoveAccountContentForClosureAsync(
            It.Is<SocialFeedActor>(actor =>
                actor.Identity.UserId == lifecycle.UserId &&
                actor.Identity.ParticipantType == MessagingParticipantTypes.Client &&
                actor.ProfileId == profile.Id),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ClosureExecutor_DefersOnBillingFailureWithoutClosingOrRemovingSocialContent()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = new ClientProfile
        {
            Id = Guid.NewGuid(),
            ClientUserId = "retry-client",
            ExternalIdentityObjectId = "retry-client",
            FirstName = "Retry",
            LastName = "Client",
            Email = "retry@example.test"
        };
        var lifecycle = new AccountLifecycleRecord
        {
            Id = Guid.NewGuid(),
            UserId = "retry-client",
            ParticipantType = MessagingParticipantTypes.Client,
            ProfileId = profile.Id,
            State = AccountLifecycleStates.DeletionRequested
        };
        db.AddRange(profile, lifecycle, new ClientSubscription
        {
            ClientProfileId = profile.Id,
            Status = ClientSubscriptionStatus.Active
        });
        await db.SaveChangesAsync();

        var billing = new Mock<IBillingOrchestrator>();
        billing.Setup(service => service.CancelClientSubscriptionAsync(
                It.IsAny<CancelClientSubscriptionCommand>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CancelClientSubscriptionResult(false, "billing_unavailable", null, null, true, null, null, null!));
        var social = new Mock<ISocialFeedService>();
        var executor = new AccountClosureService(
            db,
            billing.Object,
            Mock.Of<IClientEntraLifecycleService>(),
            social.Object,
            NullLogger<AccountClosureService>.Instance);

        var result = await executor.ProcessAsync(lifecycle.Id);

        Assert.True(result.Claimed);
        Assert.False(result.Closed);
        Assert.Equal("account_closure_billing_incomplete", result.DeferredCode);
        var deferred = await db.AccountLifecycleRecords.SingleAsync();
        Assert.Equal(AccountLifecycleStates.DeletionRequested, deferred.State);
        Assert.Equal("account_closure_billing_incomplete", deferred.LastClosureErrorCode);
        Assert.True(deferred.ClosureLeaseExpiresUtc > DateTime.UtcNow);
        social.Verify(service => service.RemoveAccountContentForClosureAsync(
            It.IsAny<SocialFeedActor>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ClosureExecutor_LeavesAConcurrentLeaseUntouched()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var lifecycle = new AccountLifecycleRecord
        {
            Id = Guid.NewGuid(),
            UserId = "leased-agent",
            ParticipantType = MessagingParticipantTypes.Agent,
            ProfileId = Guid.NewGuid(),
            State = AccountLifecycleStates.DeletionRequested,
            ClosureLeaseId = Guid.NewGuid(),
            ClosureLeaseExpiresUtc = DateTime.UtcNow.AddMinutes(1)
        };
        db.AccountLifecycleRecords.Add(lifecycle);
        await db.SaveChangesAsync();

        var executor = new AccountClosureService(
            db,
            Mock.Of<IBillingOrchestrator>(),
            Mock.Of<IClientEntraLifecycleService>(),
            Mock.Of<ISocialFeedService>(),
            NullLogger<AccountClosureService>.Instance);

        var result = await executor.ProcessAsync(lifecycle.Id);

        Assert.False(result.Claimed);
        Assert.False(result.Closed);
        Assert.Equal(0, (await db.AccountLifecycleRecords.SingleAsync()).ClosureAttemptCount);
        Assert.Empty(await db.AccountLifecycleAuditEntries.ToArrayAsync());
    }

    [Fact]
    public async Task PauseClient_UsesResolvedTypedSubject_AndPersistsOnlyThatLifecycleRecord()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var billing = new Mock<IBillingOrchestrator>();
        billing
            .Setup(service => service.PauseClientSubscriptionAsync(
                It.IsAny<AccountLifecycleSubscriptionCommand>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountLifecycleSubscriptionResult(true, null, "Paused.", null, null));
        var service = new AccountLifecycleService(db, billing.Object);
        var subject = new AccountLifecycleSubject(
            "client-entra-object-id",
            MessagingParticipantTypes.Client,
            Guid.NewGuid());

        var result = await service.PauseAsync(subject, "test-correlation");

        Assert.True(result.Succeeded);
        Assert.Equal(AccountLifecycleStates.Paused, result.Snapshot.State);
        var record = Assert.Single(db.AccountLifecycleRecords);
        Assert.Equal(subject.ProfileId, record.ProfileId);
        Assert.Equal(subject.UserId, record.UserId);
        Assert.Equal(MessagingParticipantTypes.Client, record.ParticipantType);
        billing.Verify(service => service.PauseClientSubscriptionAsync(
            It.Is<AccountLifecycleSubscriptionCommand>(command =>
                command.ClientProfileId == subject.ProfileId &&
                command.ActorId == subject.UserId &&
                command.CorrelationId == "test-correlation"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PauseAgent_DoesNotCreateOrMutateAClientSubscription()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var billing = new Mock<IBillingOrchestrator>();
        var service = new AccountLifecycleService(db, billing.Object);
        var subject = new AccountLifecycleSubject(
            "agent-entra-object-id",
            MessagingParticipantTypes.Agent,
            Guid.NewGuid());

        var result = await service.PauseAsync(subject);

        Assert.True(result.Succeeded);
        Assert.Equal(AccountLifecycleStates.Paused, result.Snapshot.State);
        billing.Verify(service => service.PauseClientSubscriptionAsync(
            It.IsAny<AccountLifecycleSubscriptionCommand>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeletionRequest_BlocksAccessAndCannotBeResumed()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var billing = new Mock<IBillingOrchestrator>();
        var service = new AccountLifecycleService(db, billing.Object);
        var subject = new AccountLifecycleSubject(
            "client-entra-object-id",
            MessagingParticipantTypes.Client,
            Guid.NewGuid());

        var requested = await service.RequestDeletionAsync(subject);
        var resumed = await service.ResumeAsync(subject);

        Assert.True(requested.Succeeded);
        Assert.False(requested.Snapshot.AllowsFullAccess);
        Assert.False(requested.Snapshot.CanResume);
        Assert.False(resumed.Succeeded);
        Assert.Equal("ACCOUNT_CLOSURE_IN_PROGRESS", resumed.ErrorCode);
    }

    [Fact]
    public async Task RepeatedDeletionRequest_IsIdempotentAndPreservesTheOriginalRequest()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var billing = new Mock<IBillingOrchestrator>();
        var service = new AccountLifecycleService(db, billing.Object);
        var subject = new AccountLifecycleSubject(
            "client-entra-object-id",
            MessagingParticipantTypes.Client,
            Guid.NewGuid());

        var first = await service.RequestDeletionAsync(subject);
        var originalRequestedUtc = first.Snapshot.DeletionRequestedUtc;
        var repeated = await service.RequestDeletionAsync(subject);

        Assert.True(first.Succeeded);
        Assert.True(repeated.Succeeded);
        Assert.Equal(AccountLifecycleStates.DeletionRequested, repeated.Snapshot.State);
        Assert.Equal(originalRequestedUtc, repeated.Snapshot.DeletionRequestedUtc);
        Assert.Single(db.AccountLifecycleRecords);
    }

    [Fact]
    public async Task MobileEndpoint_UsesOnlyResolvedActorSubject()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var profileId = Guid.NewGuid();
        var actor = new MobileResolvedActor(
            new MessagingActor("client-entra-object-id", MessagingParticipantTypes.Client),
            profileId,
            "Legend Client");
        var actorResolver = new Mock<IMobileActorResolver>();
        actorResolver
            .Setup(resolver => resolver.ResolveAsync(
                It.IsAny<System.Security.Claims.ClaimsPrincipal>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MobileActorResolution(true, null, null, [actor], actor, false));

        var billing = new Mock<IBillingOrchestrator>();
        billing
            .Setup(service => service.PauseClientSubscriptionAsync(
                It.IsAny<AccountLifecycleSubscriptionCommand>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountLifecycleSubscriptionResult(true, null, "Paused.", null, null));
        var lifecycle = new AccountLifecycleService(db, billing.Object);
        var controller = new MobileAccountLifecycleController(actorResolver.Object, lifecycle)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        var response = await controller.Pause(
            new MobileAccountLifecycleConfirmationRequest("PAUSE"),
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(response);
        var record = Assert.Single(db.AccountLifecycleRecords);
        Assert.Equal(profileId, record.ProfileId);
        Assert.Equal("client-entra-object-id", record.UserId);
    }

    [Fact]
    public async Task MobileBase_RejectsPausedActorBeforeFeatureControllersRun()
    {
        var profileId = Guid.NewGuid();
        var actor = new MobileResolvedActor(
            new MessagingActor("client-entra-object-id", MessagingParticipantTypes.Client),
            profileId,
            "Legend Client");
        var resolver = new Mock<IMobileActorResolver>();
        resolver
            .Setup(service => service.ResolveAsync(
                It.IsAny<System.Security.Claims.ClaimsPrincipal>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MobileActorResolution(true, null, null, [actor], actor, false));
        var lifecycle = new Mock<IAccountLifecycleService>();
        lifecycle
            .Setup(service => service.GetAsync(It.IsAny<AccountLifecycleSubject>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountLifecycleSnapshot(
                AccountLifecycleStates.Paused,
                false,
                true,
                DateTime.UtcNow,
                null,
                null));
        var controller = new LifecycleProbeController(resolver.Object, lifecycle.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        var resolution = await controller.ProbeAsync();

        var response = Assert.IsType<ObjectResult>(resolution.Error);
        Assert.Equal(StatusCodes.Status403Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task MobileBase_RejectsClosedActorBeforeFeatureControllersRun()
    {
        var profileId = Guid.NewGuid();
        var actor = new MobileResolvedActor(
            new MessagingActor("client-entra-object-id", MessagingParticipantTypes.Client),
            profileId,
            "Legend Client");
        var resolver = new Mock<IMobileActorResolver>();
        resolver
            .Setup(service => service.ResolveAsync(
                It.IsAny<System.Security.Claims.ClaimsPrincipal>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MobileActorResolution(true, null, null, [actor], actor, false));
        var lifecycle = new Mock<IAccountLifecycleService>();
        lifecycle
            .Setup(service => service.GetAsync(It.IsAny<AccountLifecycleSubject>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountLifecycleSnapshot(
                AccountLifecycleStates.Closed,
                false,
                false,
                null,
                DateTime.UtcNow.AddDays(-1),
                DateTime.UtcNow));
        var controller = new LifecycleProbeController(resolver.Object, lifecycle.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        var resolution = await controller.ProbeAsync();

        var response = Assert.IsType<ObjectResult>(resolution.Error);
        Assert.Equal(StatusCodes.Status403Forbidden, response.StatusCode);
    }

    private sealed class LifecycleProbeController : MobileApiControllerBase
    {
        public LifecycleProbeController(
            IMobileActorResolver actorResolver,
            IAccountLifecycleService lifecycle)
            : base(actorResolver, lifecycle)
        {
        }

        public Task<MobileActorRequestResolution> ProbeAsync() =>
            ResolveActorAsync(CancellationToken.None);
    }

    private static CancelClientSubscriptionResult CancellationSucceeded() =>
        new(true, null, "Cancelled.", null, false, null, null, null!);
}
