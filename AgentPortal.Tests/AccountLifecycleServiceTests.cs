using AgentPortal.Mobile;
using System;
using System.Threading;
using System.Threading.Tasks;
using Domain.Accounts;
using Domain.Billing;
using Domain.Messaging;
using Infrastructure.Identity;
using Infrastructure.Mobile;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed class AccountLifecycleServiceTests
{
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
}
