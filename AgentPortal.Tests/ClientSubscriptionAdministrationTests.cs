using System;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Controllers;
using AgentPortal.Models;
using AgentPortal.Services;
using Domain.Billing;
using Domain.Entities;
using Infrastructure.Billing;
using Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed class ClientSubscriptionAdministrationTests
{
    [Fact]
    public async Task ConfigureSubscriptionOffer_ForOwnedExistingClient_CreatesInvitationWithoutChangingClientData()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var clientUserId = Guid.NewGuid().ToString();
        var profile = await AddOwnedProfileAsync(db, "agent-1", clientUserId, "client1@example.com");
        profile.CrmNotes = ClientCrmMetaSerializer.Serialize(new ClientCrmMeta
        {
            RecordType = "Client",
            PipelineStage = "Client"
        });
        await db.SaveChangesAsync();

        var emailSender = BuildEmailSender(sendResult: true);
        var controller = ControllerTestHelpers.BuildClientsController(
            db,
            Mock.Of<IExecutionEngine>(),
            Mock.Of<ICommitmentService>(),
            BuildUser("agent-1"),
            billingOrchestrator: BuildInvitationOnlyOrchestrator(db),
            emailSender: emailSender.Object);

        var result = await controller.ConfigureSubscriptionOffer(
            new ClientsController.ConfigureSubscriptionOfferQuickViewRequest
            {
                ClientProfileId = profile.Id,
                SubscriptionPriceType = nameof(ClientSubscriptionOfferPriceType.Fixed50),
                SubscriptionBillingAnchorMode = nameof(BillingAnchorSelectionMode.FirstOfMonth)
            });

        var json = Assert.IsType<JsonResult>(result);
        var payloadJson = JsonSerializer.Serialize(json.Value);
        var persistedProfile = await db.ClientProfiles.SingleAsync(x => x.Id == profile.Id);
        var offer = await db.ClientSubscriptionOffers.SingleAsync(x => x.ClientProfileId == profile.Id);
        var invitation = await db.SubscriptionActivationInvitations.SingleAsync(x => x.ClientProfileId == profile.Id);

        Assert.Contains("\"ok\":true", payloadJson, StringComparison.Ordinal);
        Assert.Equal(clientUserId, persistedProfile.ClientUserId);
        Assert.Equal("client1@example.com", persistedProfile.Email);
        Assert.Equal(ClientSubscriptionOfferPricing.Fixed50Cents, offer.MonthlyAmountCents);
        Assert.Equal(ClientSubscriptionOfferStatus.Offered, offer.Status);
        Assert.Equal(SubscriptionActivationInvitationStatus.Sent, invitation.Status);
        emailSender.Verify(
            x => x.TrySendAsync(
                "client1@example.com",
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>()),
            Times.Once);
    }

    [Fact]
    public async Task BillingWorkspace_ForExistingClientWithoutOffer_RemainsAvailableForSubscriptionSetup()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddOwnedProfileAsync(db, "agent-1", Guid.NewGuid().ToString(), "client1@example.com");

        var snapshot = await new ClientBillingWorkspaceService(db).BuildSnapshotAsync(profile.Id, "agent-1");

        Assert.NotNull(snapshot);
        var snapshotJson = JsonSerializer.Serialize(snapshot);
        Assert.Contains("\"canConfigureSubscription\":true", snapshotJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResendSubscriptionInvitation_UnownedClientProfile_ReturnsForbid()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var ownedProfile = await AddOwnedProfileAsync(db, "agent-owner", "client-1", "client1@example.com");
        await AddOfferAsync(db, ownedProfile.Id, "agent-owner");

        var emailSender = new Mock<IEmailSender>(MockBehavior.Strict);
        var controller = ControllerTestHelpers.BuildClientsController(
            db,
            Mock.Of<IExecutionEngine>(),
            Mock.Of<ICommitmentService>(),
            BuildUser("agent-other"),
            billingOrchestrator: BuildInvitationOnlyOrchestrator(db),
            emailSender: emailSender.Object);

        var result = await controller.ResendSubscriptionInvitation(
            new ClientsController.BillingQuickViewActionRequest { ClientProfileId = ownedProfile.Id });

        Assert.IsType<ForbidResult>(result);
        emailSender.Verify(
            x => x.TrySendAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>()),
            Times.Never);
    }

    [Fact]
    public async Task ResendSubscriptionInvitation_CreatesReplacementInvitation_AndMarksItSent()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddOwnedProfileAsync(db, "agent-1", "client-1", "client1@example.com");
        var offer = await AddOfferAsync(db, profile.Id, "agent-1");
        var originalInvitation = await AddInvitationAsync(
            db,
            profile.Id,
            offer.Id,
            "client1@example.com",
            SubscriptionActivationInvitationStatus.Sent);

        var emailSender = BuildEmailSender(sendResult: true);
        var controller = ControllerTestHelpers.BuildClientsController(
            db,
            Mock.Of<IExecutionEngine>(),
            Mock.Of<ICommitmentService>(),
            BuildUser("agent-1"),
            billingOrchestrator: BuildInvitationOnlyOrchestrator(db),
            emailSender: emailSender.Object);

        var result = await controller.ResendSubscriptionInvitation(
            new ClientsController.BillingQuickViewActionRequest { ClientProfileId = profile.Id });

        var json = Assert.IsType<JsonResult>(result);
        var payloadJson = JsonSerializer.Serialize(json.Value);
        var invitations = await db.SubscriptionActivationInvitations
            .OrderByDescending(x => x.CreatedUtc)
            .ToListAsync();
        var latestInvitation = invitations.First();
        var persistedOriginal = invitations.Single(x => x.Id == originalInvitation.Id);

        Assert.Contains("\"ok\":true", payloadJson, StringComparison.Ordinal);
        Assert.Equal(2, invitations.Count);
        Assert.Equal(SubscriptionActivationInvitationStatus.Superseded, persistedOriginal.Status);
        Assert.Equal(SubscriptionActivationInvitationStatus.Sent, latestInvitation.Status);
        Assert.Equal(1, latestInvitation.SendCount);
        Assert.True(latestInvitation.LastSentUtc.HasValue);
        emailSender.Verify(
            x => x.TrySendAsync(
                "client1@example.com",
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>()),
            Times.Once);
    }

    [Fact]
    public async Task ResendSubscriptionInvitation_EmailFailure_ReturnsDeliveryError_AndPreservesClientProfile()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddOwnedProfileAsync(db, "agent-1", "client-1", "client1@example.com");
        var offer = await AddOfferAsync(db, profile.Id, "agent-1");
        await AddInvitationAsync(
            db,
            profile.Id,
            offer.Id,
            "client1@example.com",
            SubscriptionActivationInvitationStatus.Pending);

        var emailSender = BuildEmailSender(sendResult: false);
        var controller = ControllerTestHelpers.BuildClientsController(
            db,
            Mock.Of<IExecutionEngine>(),
            Mock.Of<ICommitmentService>(),
            BuildUser("agent-1"),
            billingOrchestrator: BuildInvitationOnlyOrchestrator(db),
            emailSender: emailSender.Object);

        var result = await controller.ResendSubscriptionInvitation(
            new ClientsController.BillingQuickViewActionRequest { ClientProfileId = profile.Id });

        var failure = Assert.IsType<ObjectResult>(result);
        var latestInvitation = await db.SubscriptionActivationInvitations
            .OrderByDescending(x => x.CreatedUtc)
            .FirstAsync();
        var sendFailureAudit = await db.BillingAuditEntries
            .OrderByDescending(x => x.OccurredUtc)
            .FirstAsync(x => x.Action == "send_failed");

        Assert.Equal(StatusCodes.Status502BadGateway, failure.StatusCode);
        Assert.NotNull(await db.ClientProfiles.SingleOrDefaultAsync(x => x.Id == profile.Id));
        Assert.Equal(SubscriptionActivationInvitationStatus.Pending, latestInvitation.Status);
        Assert.Equal("INVITATION_EMAIL_FAILED", sendFailureAudit.ReasonCode);
    }

    [Fact]
    public async Task RevokeSubscriptionInvitation_UnownedClientProfile_ReturnsForbid()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddOwnedProfileAsync(db, "agent-owner", "client-1", "client1@example.com");
        var offer = await AddOfferAsync(db, profile.Id, "agent-owner");
        await AddInvitationAsync(db, profile.Id, offer.Id, "client1@example.com", SubscriptionActivationInvitationStatus.Pending);

        var controller = ControllerTestHelpers.BuildClientsController(
            db,
            Mock.Of<IExecutionEngine>(),
            Mock.Of<ICommitmentService>(),
            BuildUser("agent-other"),
            billingOrchestrator: BuildInvitationOnlyOrchestrator(db));

        var result = await controller.RevokeSubscriptionInvitation(
            new ClientsController.BillingQuickViewActionRequest { ClientProfileId = profile.Id });

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task RevokeSubscriptionInvitation_RevokesOwnedInvitation()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddOwnedProfileAsync(db, "agent-1", "client-1", "client1@example.com");
        var offer = await AddOfferAsync(db, profile.Id, "agent-1");
        var invitation = await AddInvitationAsync(
            db,
            profile.Id,
            offer.Id,
            "client1@example.com",
            SubscriptionActivationInvitationStatus.Pending);

        var controller = ControllerTestHelpers.BuildClientsController(
            db,
            Mock.Of<IExecutionEngine>(),
            Mock.Of<ICommitmentService>(),
            BuildUser("agent-1"),
            billingOrchestrator: BuildInvitationOnlyOrchestrator(db));

        var result = await controller.RevokeSubscriptionInvitation(
            new ClientsController.BillingQuickViewActionRequest { ClientProfileId = profile.Id });

        var json = Assert.IsType<JsonResult>(result);
        var payloadJson = JsonSerializer.Serialize(json.Value);
        var updatedInvitation = await db.SubscriptionActivationInvitations.SingleAsync(x => x.Id == invitation.Id);

        Assert.Contains("\"ok\":true", payloadJson, StringComparison.Ordinal);
        Assert.Equal(SubscriptionActivationInvitationStatus.Revoked, updatedInvitation.Status);
        Assert.True(updatedInvitation.RevokedUtc.HasValue);
    }

    [Fact]
    public async Task CancelClientSubscriptionAtPeriodEnd_UsesSharedOrchestrator_AndUpdatesOwnedSubscription()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = await AddOwnedProfileAsync(db, "agent-1", "client-1", "client1@example.com");
        var offer = await AddOfferAsync(db, profile.Id, "agent-1");
        var subscription = await AddSubscriptionAsync(db, profile.Id, offer.Id, "agent-1");
        var gateway = new Mock<IBillingGateway>(MockBehavior.Strict);
        gateway.SetupGet(x => x.Provider).Returns(BillingProvider.Square);
        gateway.SetupGet(x => x.Environment).Returns(BillingProviderEnvironment.Sandbox);
        gateway
            .Setup(x => x.CancelSubscriptionAsync(
                It.Is<BillingSubscriptionCancellationRequest>(request =>
                    request.ProviderSubscriptionId == "sub_123" &&
                    request.CancelAtPeriodEnd),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BillingSubscriptionResult(
                true,
                "sub_123",
                "ACTIVE",
                null,
                "Subscription will cancel at period end.",
                "req_cancel",
                false,
                AmountCents: ClientSubscriptionOfferPricing.Fixed100Cents,
                Currency: "USD",
                CurrentPeriodEndUtc: DateTime.UtcNow.AddDays(14),
                NextBillingDateUtc: DateTime.UtcNow.AddDays(14),
                CancelAtPeriodEnd: true));

        var billingOrchestrator = new MasterAppBillingOrchestrator(db, gateway.Object, BuildEntitlementService(db));
        var controller = ControllerTestHelpers.BuildClientsController(
            db,
            Mock.Of<IExecutionEngine>(),
            Mock.Of<ICommitmentService>(),
            BuildUser("agent-1"),
            billingOrchestrator: billingOrchestrator);

        var result = await controller.CancelClientSubscriptionAtPeriodEnd(
            new ClientsController.BillingQuickViewActionRequest { ClientProfileId = profile.Id });

        var json = Assert.IsType<JsonResult>(result);
        var payloadJson = JsonSerializer.Serialize(json.Value);
        var updatedSubscription = await db.ClientSubscriptions.SingleAsync(x => x.Id == subscription.Id);
        var entitlement = await db.ClientEntitlements.SingleAsync(x => x.ClientProfileId == profile.Id);

        Assert.Contains("\"ok\":true", payloadJson, StringComparison.Ordinal);
        Assert.True(updatedSubscription.CancelAtPeriodEnd);
        Assert.Equal(ClientSubscriptionStatus.Active, updatedSubscription.Status);
        Assert.Equal(ClientEntitlementStatus.Active, entitlement.Status);
    }

    private static ClaimsPrincipal BuildUser(string oid)
    {
        var identity = new ClaimsIdentity(new[] { new Claim("oid", oid) }, "TestAuth");
        return new ClaimsPrincipal(identity);
    }

    private static Mock<IEmailSender> BuildEmailSender(bool sendResult)
    {
        var emailSender = new Mock<IEmailSender>(MockBehavior.Strict);
        emailSender
            .Setup(x => x.TrySendAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>()))
            .ReturnsAsync(sendResult);
        return emailSender;
    }

    private static IBillingOrchestrator BuildInvitationOnlyOrchestrator(MasterAppDbContext db)
    {
        var gateway = new Mock<IBillingGateway>(MockBehavior.Strict);
        gateway.SetupGet(x => x.Provider).Returns(BillingProvider.Square);
        gateway.SetupGet(x => x.Environment).Returns(BillingProviderEnvironment.Sandbox);
        return new MasterAppBillingOrchestrator(db, gateway.Object, BuildEntitlementService(db));
    }

    private static BillingEntitlementService BuildEntitlementService(MasterAppDbContext db)
    {
        return new BillingEntitlementService(db);
    }

    private static async Task<ClientProfile> AddOwnedProfileAsync(
        MasterAppDbContext db,
        string agentUserId,
        string clientUserId,
        string email)
    {
        var profile = new ClientProfile
        {
            ClientUserId = clientUserId,
            FirstName = "Client",
            LastName = "One",
            Email = email,
            NormalizedEmail = email.ToLowerInvariant(),
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        db.ClientProfiles.Add(profile);
        db.AgentClients.Add(new AgentClient
        {
            AgentUserId = agentUserId,
            ClientUserId = clientUserId,
            CreatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        return profile;
    }

    private static async Task<ClientSubscriptionOffer> AddOfferAsync(
        MasterAppDbContext db,
        Guid clientProfileId,
        string ownerAgentUserId)
    {
        var offer = new ClientSubscriptionOffer
        {
            ClientProfileId = clientProfileId,
            OwnerAgentUserId = ownerAgentUserId,
            PriceType = ClientSubscriptionOfferPriceType.Fixed100,
            MonthlyAmountCents = ClientSubscriptionOfferPricing.Fixed100Cents,
            Currency = "USD",
            BillingAnchorSelectionMode = BillingAnchorSelectionMode.FirstOfMonth,
            Status = ClientSubscriptionOfferStatus.Offered,
            CreatedUtc = DateTime.UtcNow.AddMinutes(-1),
            UpdatedUtc = DateTime.UtcNow.AddMinutes(-1)
        };

        db.ClientSubscriptionOffers.Add(offer);
        await db.SaveChangesAsync();
        return offer;
    }

    private static async Task<SubscriptionActivationInvitation> AddInvitationAsync(
        MasterAppDbContext db,
        Guid clientProfileId,
        Guid offerId,
        string email,
        SubscriptionActivationInvitationStatus status)
    {
        var invitation = new SubscriptionActivationInvitation
        {
            ClientProfileId = clientProfileId,
            ClientSubscriptionOfferId = offerId,
            TokenHash = BillingIdempotency.Hash(Guid.NewGuid().ToString("N")),
            IntendedNormalizedEmail = email.ToLowerInvariant(),
            Status = status,
            ExpiresUtc = DateTime.UtcNow.AddDays(7),
            CreatedByAgentUserId = "agent-1",
            CreatedUtc = DateTime.UtcNow.AddHours(-1),
            LastSentUtc = status == SubscriptionActivationInvitationStatus.Sent ? DateTime.UtcNow.AddHours(-1) : null,
            SendCount = status == SubscriptionActivationInvitationStatus.Sent ? 1 : 0
        };

        db.SubscriptionActivationInvitations.Add(invitation);
        await db.SaveChangesAsync();
        return invitation;
    }

    private static async Task<ClientSubscription> AddSubscriptionAsync(
        MasterAppDbContext db,
        Guid clientProfileId,
        Guid offerId,
        string ownerAgentUserId)
    {
        var subscription = new ClientSubscription
        {
            ClientProfileId = clientProfileId,
            AcceptedOfferId = offerId,
            OwnerAgentUserId = ownerAgentUserId,
            Provider = BillingProvider.Square,
            ProviderEnvironment = BillingProviderEnvironment.Sandbox,
            ProviderCustomerId = "cust_123",
            ProviderPaymentMethodId = "card_123",
            ProviderSubscriptionId = "sub_123",
            ProviderPlanVariationId = "plan_123",
            MonthlyAmountCents = ClientSubscriptionOfferPricing.Fixed100Cents,
            Currency = "USD",
            BillingTimeZoneId = "America/Phoenix",
            BillingAnchorDay = 15,
            Status = ClientSubscriptionStatus.Active,
            PaymentStanding = ClientSubscriptionPaymentStanding.Current,
            CurrentPeriodStartUtc = DateTime.UtcNow.AddDays(-1),
            CurrentPeriodEndUtc = DateTime.UtcNow.AddDays(29),
            NextBillingDateUtc = DateTime.UtcNow.AddDays(29),
            ActivatedUtc = DateTime.UtcNow.AddDays(-10),
            CreatedUtc = DateTime.UtcNow.AddDays(-10),
            UpdatedUtc = DateTime.UtcNow
        };

        db.ClientSubscriptions.Add(subscription);
        await db.SaveChangesAsync();
        return subscription;
    }
}
