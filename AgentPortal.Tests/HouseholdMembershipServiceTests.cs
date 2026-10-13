using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Domain.Billing;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Billing;
using Infrastructure.Data;
using Infrastructure.Households;
using Infrastructure.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed class HouseholdMembershipServiceTests
{
    [Fact]
    public async Task PartnerAcceptance_CreatesDistinctProfile_AndSharesOnlyOwnerSubscriptionEntitlement()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var owner = await AddOwnerAsync(db);
        var service = BuildService(db, "partner-entra-id");
        await AddActiveSubscriptionAsync(db, owner.Id);

        await service.EnsurePrimaryHouseholdActiveAsync(owner.Id);
        var invitation = await service.IssuePartnerInvitationAsync(new IssuePartnerInvitationCommand(
            owner.Id,
            "Partner",
            "One",
            "partner@example.com",
            "agent-1"));

        var accepted = await service.AcceptPartnerInvitationAsync(
            invitation.PlainTextToken,
            new AcceptPartnerInvitationCommand("Partner", "One", "partner@example.com"));

        Assert.True(accepted.ProfileCreated);
        Assert.NotEqual(owner.Id, accepted.Profile.Id);
        Assert.Equal("partner-entra-id", accepted.Profile.ExternalIdentityObjectId);
        Assert.Equal(HouseholdMembershipStatus.Active, accepted.Membership.Status);
        Assert.Equal(HouseholdMemberRole.Partner, accepted.Membership.Role);

        var entitlements = new BillingEntitlementService(db, service);
        var evaluation = await entitlements.EvaluateAsync(new BillingEntitlementEvaluationRequest(
            accepted.Profile.Id,
            BillingEntitlementKeys.ClientAppFullAccess,
            DateTime.UtcNow));
        await entitlements.RefreshAsync(accepted.Profile.Id, BillingEntitlementKeys.ClientAppFullAccess);

        Assert.Equal(ClientEntitlementStatus.Active, evaluation.Status);
        var stored = Assert.Single(db.ClientEntitlements);
        Assert.Equal(owner.Id, stored.ClientProfileId);
        Assert.Empty(db.ClientSubscriptions.Where(x => x.ClientProfileId == accepted.Profile.Id));
    }

    [Fact]
    public async Task PartnerInvitation_RejectsSecondActivePartnerWithoutCreatingAnotherHouseholdMember()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var owner = await AddOwnerAsync(db);
        var service = BuildService(db, "partner-entra-id");
        await AddActiveSubscriptionAsync(db, owner.Id);

        await service.EnsurePrimaryHouseholdActiveAsync(owner.Id);
        var invitation = await service.IssuePartnerInvitationAsync(new IssuePartnerInvitationCommand(
            owner.Id, "Partner", "One", "partner@example.com", "agent-1"));
        await service.AcceptPartnerInvitationAsync(
            invitation.PlainTextToken,
            new AcceptPartnerInvitationCommand("Partner", "One", "partner@example.com"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.IssuePartnerInvitationAsync(new IssuePartnerInvitationCommand(
                owner.Id, "Second", "Partner", "second@example.com", "agent-1")));

        Assert.Equal("This household already has an active partner membership.", exception.Message);
        Assert.Equal(2, db.HouseholdMemberships.Count());
        Assert.Single(db.HouseholdAccounts);
    }

    [Fact]
    public async Task RemovedPartner_CannotResolveSubscriptionAccess()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var owner = await AddOwnerAsync(db);
        var service = BuildService(db, "partner-entra-id");
        await AddActiveSubscriptionAsync(db, owner.Id);
        await service.EnsurePrimaryHouseholdActiveAsync(owner.Id);
        var invitation = await service.IssuePartnerInvitationAsync(new IssuePartnerInvitationCommand(
            owner.Id, "Partner", "One", "partner@example.com", "agent-1"));
        var accepted = await service.AcceptPartnerInvitationAsync(
            invitation.PlainTextToken,
            new AcceptPartnerInvitationCommand("Partner", "One", "partner@example.com"));

        await service.RemoveMemberAsync(accepted.Profile.Id, "PARTNER_REMOVED", "owner-entra-id");
        var access = await service.ResolveActiveAccessAsync(accepted.Profile.Id);

        Assert.False(access.HasActiveMembership);
        Assert.Equal("HOUSEHOLD_MEMBERSHIP_REQUIRED", access.ReasonCode);
        Assert.Null(access.SubscriptionOwnerClientProfileId);
        var entitlements = new BillingEntitlementService(db, service);
        var evaluation = await entitlements.EvaluateAsync(new BillingEntitlementEvaluationRequest(
            accepted.Profile.Id, BillingEntitlementKeys.ClientAppFullAccess, DateTime.UtcNow));
        Assert.Equal(ClientEntitlementStatus.NotGranted, evaluation.Status);
        Assert.Equal("NO_SUBSCRIPTION", evaluation.ReasonCode);
        await Assert.ThrowsAsync<InvalidOperationException>(() => entitlements.RefreshAsync(
            accepted.Profile.Id, BillingEntitlementKeys.ClientAppFullAccess));
    }

    [Fact]
    public async Task PartnerStagedBeforeActivation_RemainsPending_WithoutAnEntraIdentityOrSubscription()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var owner = await AddOwnerAsync(db);
        var lifecycle = new Mock<IClientEntraLifecycleService>(MockBehavior.Strict);
        var service = new HouseholdMembershipService(
            db,
            lifecycle.Object,
            NullLogger<HouseholdMembershipService>.Instance);

        var staged = await service.StagePartnerInvitationAsync(
            new IssuePartnerInvitationCommand(
                owner.Id,
                "Partner",
                "One",
                "partner@example.com",
                "agent-1"));

        Assert.Equal(HouseholdAccountStatus.PendingActivation, db.HouseholdAccounts.Single().Status);
        Assert.Equal(HouseholdMembershipStatus.PendingInvitation, staged.Membership.Status);
        Assert.Null(staged.Membership.ClientProfileId);
        Assert.Null(staged.Membership.ExternalIdentityObjectId);
        Assert.Empty(db.ClientSubscriptions.Where(x => x.ClientProfileId != owner.Id));
        lifecycle.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task LapsedPrimarySubscription_RevokesPartnerSharedAccess_ButKeepsTheirDistinctProfile()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var owner = await AddOwnerAsync(db);
        var service = BuildService(db, "partner-entra-id");
        await AddActiveSubscriptionAsync(db, owner.Id);
        await service.EnsurePrimaryHouseholdActiveAsync(owner.Id);
        var invitation = await service.IssuePartnerInvitationAsync(new IssuePartnerInvitationCommand(
            owner.Id, "Partner", "One", "partner@example.com", "agent-1"));
        var accepted = await service.AcceptPartnerInvitationAsync(
            invitation.PlainTextToken,
            new AcceptPartnerInvitationCommand("Partner", "One", "partner@example.com"));

        var subscription = db.ClientSubscriptions.Single();
        subscription.Status = ClientSubscriptionStatus.PastDue;
        subscription.UpdatedUtc = DateTime.UtcNow.AddMinutes(1);
        await db.SaveChangesAsync();

        var access = await service.ResolveActiveAccessAsync(accepted.Profile.Id);

        Assert.False(access.HasActiveMembership);
        Assert.Equal("HOUSEHOLD_SUBSCRIPTION_INACTIVE", access.ReasonCode);
        Assert.NotNull(await db.ClientProfiles.FindAsync(accepted.Profile.Id));
        Assert.Empty(db.ClientSubscriptions.Where(x => x.ClientProfileId == accepted.Profile.Id));
    }

    [Fact]
    public async Task StagedPartnerInvitation_IsMadeDeliverableOnlyAfterPrimaryActivation()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var owner = await AddOwnerAsync(db);
        var service = BuildService(db, "partner-entra-id");
        var staged = await service.StagePartnerInvitationAsync(new IssuePartnerInvitationCommand(
            owner.Id, "Partner", "One", "partner@example.com", "agent-1"));

        Assert.Null(await service.CreatePendingPartnerInvitationForDeliveryAsync(staged.Invitation.Id));

        await AddActiveSubscriptionAsync(db, owner.Id);
        await service.EnsurePrimaryHouseholdActiveAsync(owner.Id);
        var deliverable = await service.CreatePendingPartnerInvitationForDeliveryAsync(staged.Invitation.Id);

        Assert.NotNull(deliverable);
        Assert.NotEqual(staged.Invitation.Id, deliverable!.Invitation.Id);
        Assert.Equal(HouseholdInvitationStatus.Revoked, db.HouseholdMemberInvitations.Single(x => x.Id == staged.Invitation.Id).Status);
        Assert.Equal("partner-entra-id", db.HouseholdMemberships.Single(x => x.Role == HouseholdMemberRole.Partner).ExternalIdentityObjectId);
    }

    [Fact]
    public async Task RemovingPartner_RevokesOnlyTheirApplicationAccess_AndLeavesPrimarySubscriptionIntact()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var owner = await AddOwnerAsync(db);
        await AddActiveSubscriptionAsync(db, owner.Id);
        var lifecycle = new Mock<IClientEntraLifecycleService>();
        lifecycle
            .Setup(x => x.EnsureExternalIdentityAsync("Partner", "One", "partner@example.com", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClientEntraIdentityResult("partner-entra-id", "partner@example.com", false, false));
        lifecycle
            .Setup(x => x.RevokeClientApplicationAccessAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var service = new HouseholdMembershipService(
            db,
            lifecycle.Object,
            NullLogger<HouseholdMembershipService>.Instance);

        await service.EnsurePrimaryHouseholdActiveAsync(owner.Id);
        var invitation = await service.IssuePartnerInvitationAsync(new IssuePartnerInvitationCommand(
            owner.Id, "Partner", "One", "partner@example.com", "agent-1"));
        var accepted = await service.AcceptPartnerInvitationAsync(
            invitation.PlainTextToken,
            new AcceptPartnerInvitationCommand("Partner", "One", "partner@example.com"));

        await service.RemoveMemberAsync(accepted.Profile.Id, "PARTNER_REMOVED", "owner-entra-id");

        lifecycle.Verify(
            x => x.RevokeClientApplicationAccessAsync(accepted.Profile.Id, It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.Equal(ClientSubscriptionStatus.Active, db.ClientSubscriptions.Single().Status);
        Assert.Equal(HouseholdMembershipStatus.Removed, db.HouseholdMemberships.Single(x => x.Role == HouseholdMemberRole.Partner).Status);
    }

    [Fact]
    public async Task Reconciliation_DryRunReportsPartnerCandidate_WithoutCreatingOrActivatingPartner()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var owner = await AddOwnerAsync(db);
        owner.SignificantOtherFirstName = "Partner";
        owner.SignificantOtherLastName = "One";
        owner.SignificantOtherEmail = "partner@example.com";
        await AddActiveSubscriptionAsync(db, owner.Id);

        var service = BuildService(db, "partner-entra-id");
        var reconciliation = new HouseholdReconciliationService(
            db,
            service,
            NullLogger<HouseholdReconciliationService>.Instance);

        var dryRun = await reconciliation.RunAsync(dryRun: true);

        Assert.Equal(1, dryRun.SubscriptionOwnersScanned);
        Assert.Equal(1, dryRun.PrimaryHouseholdsCreated);
        Assert.Equal(1, dryRun.PartnerInviteCandidates);
        Assert.Empty(db.HouseholdAccounts);
        Assert.Empty(db.HouseholdMemberships);

        var executed = await reconciliation.RunAsync(dryRun: false);

        Assert.Equal(1, executed.PrimaryHouseholdsCreated);
        var primary = Assert.Single(db.HouseholdMemberships);
        Assert.Equal(HouseholdMemberRole.PrimaryOwner, primary.Role);
        Assert.Equal(HouseholdMembershipStatus.Active, primary.Status);
    }

    [Theory]
    [InlineData(HouseholdMembershipStatus.Suspended, HouseholdAccountStatus.Active, "HOUSEHOLD_MEMBERSHIP_SUSPENDED")]
    [InlineData(HouseholdMembershipStatus.Removed, HouseholdAccountStatus.Active, "HOUSEHOLD_MEMBERSHIP_REMOVED")]
    [InlineData(HouseholdMembershipStatus.PendingInvitation, HouseholdAccountStatus.Active, "HOUSEHOLD_MEMBERSHIP_PENDINGINVITATION")]
    [InlineData(HouseholdMembershipStatus.Active, HouseholdAccountStatus.Suspended, "HOUSEHOLD_SUSPENDED")]
    [InlineData(HouseholdMembershipStatus.Active, HouseholdAccountStatus.Closed, "HOUSEHOLD_CLOSED")]
    [InlineData(HouseholdMembershipStatus.Active, HouseholdAccountStatus.PendingActivation, "HOUSEHOLD_PENDINGACTIVATION")]
    public async Task DeniedPartner_CannotUseActiveOwnerSubscriptionOrOverwriteOwnerEntitlement(
        HouseholdMembershipStatus membershipStatus,
        HouseholdAccountStatus householdStatus,
        string reasonCode)
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var owner = await AddOwnerAsync(db);
        var service = BuildService(db, "partner-entra-id");
        await AddActiveSubscriptionAsync(db, owner.Id);
        var invitation = await service.IssuePartnerInvitationAsync(new IssuePartnerInvitationCommand(
            owner.Id, "Partner", "One", "partner@example.com", "agent-1"));
        var accepted = await service.AcceptPartnerInvitationAsync(invitation.PlainTextToken,
            new AcceptPartnerInvitationCommand("Partner", "One", "partner@example.com"));
        var entitlements = new BillingEntitlementService(db, service);
        var projection = await entitlements.RefreshAsync(owner.Id, BillingEntitlementKeys.ClientAppFullAccess);
        var updatedUtc = projection.UpdatedUtc;
        accepted.Membership.Status = membershipStatus;
        accepted.Household.Status = householdStatus;
        await db.SaveChangesAsync();

        var result = await entitlements.EvaluateAsync(new BillingEntitlementEvaluationRequest(
            accepted.Profile.Id, BillingEntitlementKeys.ClientAppFullAccess, DateTime.UtcNow));

        Assert.Equal(ClientEntitlementStatus.NotGranted, result.Status);
        Assert.Equal(reasonCode, result.ReasonCode);
        await Assert.ThrowsAsync<InvalidOperationException>(() => entitlements.RefreshAsync(
            accepted.Profile.Id, BillingEntitlementKeys.ClientAppFullAccess));
        Assert.Equal(ClientEntitlementStatus.Active, projection.Status);
        Assert.Equal(updatedUtc, projection.UpdatedUtc);
        Assert.Single(db.ClientEntitlements);
        Assert.Equal(owner.Id, projection.ClientProfileId);
    }

    [Theory]
    [InlineData(ClientSubscriptionStatus.GracePeriod, 60, true)]
    [InlineData(ClientSubscriptionStatus.PastDue, 60, true)]
    [InlineData(ClientSubscriptionStatus.GracePeriod, null, false)]
    [InlineData(ClientSubscriptionStatus.GracePeriod, 0, false)]
    [InlineData(ClientSubscriptionStatus.GracePeriod, -60, false)]
    [InlineData(ClientSubscriptionStatus.Canceled, 60, false)]
    [InlineData(ClientSubscriptionStatus.Suspended, 60, false)]
    [InlineData(ClientSubscriptionStatus.Paused, 60, false)]
    public async Task PartnerAccessAndInvitations_UseTheSameSubscriptionDeadline(
        ClientSubscriptionStatus status, int? graceMinutes, bool expectedAccess)
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var owner = await AddOwnerAsync(db);
        var service = BuildService(db, "partner-entra-id");
        await AddActiveSubscriptionAsync(db, owner.Id);
        var invitation = await service.IssuePartnerInvitationAsync(new IssuePartnerInvitationCommand(
            owner.Id, "Partner", "One", "partner@example.com", "agent-1"));
        var accepted = await service.AcceptPartnerInvitationAsync(invitation.PlainTextToken,
            new AcceptPartnerInvitationCommand("Partner", "One", "partner@example.com"));
        var subscription = db.ClientSubscriptions.Single();
        var evaluatedUtc = DateTime.UtcNow;
        subscription.Status = status;
        subscription.GracePeriodEndsUtc = graceMinutes.HasValue ? evaluatedUtc.AddMinutes(graceMinutes.Value) : null;
        await db.SaveChangesAsync();

        var access = await service.ResolveActiveAccessAsync(accepted.Profile.Id);
        var evaluation = await new BillingEntitlementService(db, service).EvaluateAsync(
            new BillingEntitlementEvaluationRequest(accepted.Profile.Id,
                BillingEntitlementKeys.ClientAppFullAccess, evaluatedUtc));
        Assert.Equal(expectedAccess, access.HasActiveMembership);
        Assert.Equal(expectedAccess, evaluation.Status is ClientEntitlementStatus.Active or ClientEntitlementStatus.GracePeriod);
        Assert.Equal(status, subscription.Status);

        if (!expectedAccess)
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.IssuePartnerInvitationAsync(
                new IssuePartnerInvitationCommand(owner.Id, "New", "Partner", "new@example.com", "agent-1")));
            Assert.Equal("A partner invitation can be issued only after the primary subscription is active.", exception.Message);
            Assert.Equal("HOUSEHOLD_SUBSCRIPTION_INACTIVE", access.ReasonCode);
        }
    }

    [Theory]
    [InlineData(ClientSubscriptionStatus.Active)]
    [InlineData(ClientSubscriptionStatus.GracePeriod)]
    [InlineData(ClientSubscriptionStatus.PastDue)]
    public async Task EligibleOwner_CanIssuePartnerInvitationDuringActiveOrUnexpiredGrace(
        ClientSubscriptionStatus status)
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var owner = await AddOwnerAsync(db);
        var service = BuildService(db, "partner-entra-id");
        await AddActiveSubscriptionAsync(db, owner.Id);
        var subscription = db.ClientSubscriptions.Single();
        subscription.Status = status;
        subscription.GracePeriodEndsUtc = DateTime.UtcNow.AddHours(1);
        await db.SaveChangesAsync();

        var invitation = await service.IssuePartnerInvitationAsync(new IssuePartnerInvitationCommand(
            owner.Id, "Partner", "One", "partner@example.com", "agent-1"));

        Assert.Equal(HouseholdInvitationStatus.Pending, invitation.Invitation.Status);
        Assert.Equal(HouseholdMembershipStatus.PendingInvitation, invitation.Membership.Status);
        Assert.Equal(HouseholdAccountStatus.Active, Assert.Single(db.HouseholdAccounts).Status);
    }

    [Fact]
    public async Task InactiveHouseholdOwner_CannotBypassHouseholdDenialWithActiveSubscription()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var owner = await AddOwnerAsync(db);
        var service = BuildService(db, "partner-entra-id");
        await AddActiveSubscriptionAsync(db, owner.Id);
        var household = await service.EnsurePrimaryHouseholdActiveAsync(owner.Id);
        household.Status = HouseholdAccountStatus.Suspended;
        await db.SaveChangesAsync();

        var evaluation = await new BillingEntitlementService(db, service).EvaluateAsync(
            new BillingEntitlementEvaluationRequest(owner.Id, BillingEntitlementKeys.ClientAppFullAccess, DateTime.UtcNow));

        Assert.Equal(ClientEntitlementStatus.NotGranted, evaluation.Status);
        Assert.Equal("HOUSEHOLD_SUSPENDED", evaluation.ReasonCode);
        Assert.Equal(HouseholdAccountStatus.Suspended, household.Status);
    }

    [Theory]
    [InlineData(HouseholdAccountStatus.Suspended, "HOUSEHOLD_SUSPENDED")]
    [InlineData(HouseholdAccountStatus.Closed, "HOUSEHOLD_CLOSED")]
    [InlineData(HouseholdAccountStatus.PendingActivation, "HOUSEHOLD_PENDINGACTIVATION")]
    public async Task InactiveHouseholdOwner_MissingPrimaryMembershipDoesNotReactivateHousehold(
        HouseholdAccountStatus status, string reasonCode)
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var owner = await AddOwnerAsync(db);
        var service = BuildService(db, "partner-entra-id");
        await AddActiveSubscriptionAsync(db, owner.Id);
        var household = new HouseholdAccount { SubscriptionOwnerClientProfileId = owner.Id, Status = status };
        db.HouseholdAccounts.Add(household);
        await db.SaveChangesAsync();

        var evaluation = await new BillingEntitlementService(db, service).EvaluateAsync(
            new BillingEntitlementEvaluationRequest(owner.Id, BillingEntitlementKeys.ClientAppFullAccess, DateTime.UtcNow));

        Assert.Equal(ClientEntitlementStatus.NotGranted, evaluation.Status);
        Assert.Equal(reasonCode, evaluation.ReasonCode);
        Assert.Equal(status, household.Status);
        Assert.Empty(db.HouseholdMemberships);
    }

    [Fact]
    public async Task LegacyActiveOwner_StillHydratesOnePrimaryHouseholdAndMembership()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var owner = await AddOwnerAsync(db);
        var service = BuildService(db, "partner-entra-id");
        await AddActiveSubscriptionAsync(db, owner.Id);
        var entitlements = new BillingEntitlementService(db, service);

        var first = await entitlements.EvaluateAsync(new BillingEntitlementEvaluationRequest(
            owner.Id, BillingEntitlementKeys.ClientAppFullAccess, DateTime.UtcNow));
        var second = await entitlements.EvaluateAsync(new BillingEntitlementEvaluationRequest(
            owner.Id, BillingEntitlementKeys.ClientAppFullAccess, DateTime.UtcNow));

        Assert.Equal(ClientEntitlementStatus.Active, first.Status);
        Assert.Equal(first.SourceId, second.SourceId);
        Assert.Equal(HouseholdAccountStatus.Active, Assert.Single(db.HouseholdAccounts).Status);
        Assert.Equal(HouseholdMembershipStatus.Active, Assert.Single(db.HouseholdMemberships).Status);
    }

    private static HouseholdMembershipService BuildService(MasterAppDbContext db, string partnerObjectId)
    {
        var lifecycle = new Mock<IClientEntraLifecycleService>();
        lifecycle
            .Setup(x => x.EnsureExternalIdentityAsync("Partner", "One", "partner@example.com", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClientEntraIdentityResult(partnerObjectId, "partner@example.com", true, true));
        return new HouseholdMembershipService(db, lifecycle.Object, NullLogger<HouseholdMembershipService>.Instance);
    }

    private static async Task<ClientProfile> AddOwnerAsync(MasterAppDbContext db)
    {
        var owner = new ClientProfile
        {
            ClientUserId = "owner-entra-id",
            ExternalIdentityObjectId = "owner-entra-id",
            FirstName = "Owner",
            LastName = "One",
            Email = "owner@example.com",
            NormalizedEmail = "owner@example.com",
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };
        db.ClientProfiles.Add(owner);
        await db.SaveChangesAsync();
        return owner;
    }

    private static async Task AddActiveSubscriptionAsync(MasterAppDbContext db, Guid ownerProfileId)
    {
        db.ClientSubscriptions.Add(new ClientSubscription
        {
            ClientProfileId = ownerProfileId,
            AcceptedOfferId = Guid.NewGuid(),
            OwnerAgentUserId = "agent-1",
            Status = ClientSubscriptionStatus.Active,
            PaymentStanding = ClientSubscriptionPaymentStanding.Current,
            ActivatedUtc = DateTime.UtcNow,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }
}
