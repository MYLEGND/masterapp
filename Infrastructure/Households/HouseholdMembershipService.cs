using System.Security.Cryptography;
using System.Text;
using Domain.Billing;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Data;
using Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Households;

public sealed record HouseholdAccessResolution(
    bool HasActiveMembership,
    Guid? HouseholdAccountId,
    Guid? SubscriptionOwnerClientProfileId,
    HouseholdMemberRole? Role,
    string? ReasonCode);

public sealed record IssuePartnerInvitationCommand(
    Guid PrimaryClientProfileId,
    string PartnerFirstName,
    string PartnerLastName,
    string PartnerEmail,
    string CreatedByUserId,
    DateTime? ExpiresUtc = null);

public sealed record HouseholdPartnerInvitationResult(
    HouseholdMemberInvitation Invitation,
    HouseholdMembership Membership,
    string PlainTextToken);

public sealed record AcceptPartnerInvitationCommand(
    string FirstName,
    string LastName,
    string Email);

public sealed record HouseholdPartnerAcceptanceResult(
    HouseholdAccount Household,
    HouseholdMembership Membership,
    ClientProfile Profile,
    bool ProfileCreated);

public interface IHouseholdMembershipService
{
    Task<HouseholdAccount> EnsurePrimaryHouseholdActiveAsync(
        Guid primaryClientProfileId,
        CancellationToken cancellationToken = default);

    Task<HouseholdAccessResolution> ResolveActiveAccessAsync(
        Guid clientProfileId,
        CancellationToken cancellationToken = default);

    Task<HouseholdPartnerInvitationResult> IssuePartnerInvitationAsync(
        IssuePartnerInvitationCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Captures partner details before the primary subscription is activated.
    /// It creates no Entra identity and grants no access; activation delivery
    /// later replaces this dormant record with a live single-use invitation.
    /// </summary>
    Task<HouseholdPartnerInvitationResult> StagePartnerInvitationAsync(
        IssuePartnerInvitationCommand command,
        CancellationToken cancellationToken = default);

    Task<HouseholdPartnerAcceptanceResult> AcceptPartnerInvitationAsync(
        string plainTextToken,
        AcceptPartnerInvitationCommand command,
        CancellationToken cancellationToken = default);

    Task MarkPartnerInvitationSentAsync(
        Guid invitationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces an undelivered pending invitation with a fresh single-use
    /// token after confirming the primary subscription is still eligible.
    /// Callers use the returned token only for delivery and never persist it.
    /// </summary>
    Task<HouseholdPartnerInvitationResult?> CreatePendingPartnerInvitationForDeliveryAsync(
        Guid invitationId,
        CancellationToken cancellationToken = default);

    Task RemoveMemberAsync(
        Guid clientProfileId,
        string reasonCode,
        string? actorUserId = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The sole authority for household membership. It deliberately does not
/// duplicate subscription state: entitlement is always derived from the one
/// subscription-owner profile resolved here. It also does not copy personal
/// profiles, social graphs, Journey settings, photos, or mobile preferences.
/// </summary>
public sealed class HouseholdMembershipService : IHouseholdMembershipService
{
    private readonly MasterAppDbContext _db;
    private readonly IClientEntraLifecycleService _entraLifecycle;
    private readonly ILogger<HouseholdMembershipService> _logger;

    public HouseholdMembershipService(
        MasterAppDbContext db,
        IClientEntraLifecycleService entraLifecycle,
        ILogger<HouseholdMembershipService> logger)
    {
        _db = db;
        _entraLifecycle = entraLifecycle;
        _logger = logger;
    }

    public async Task<HouseholdAccount> EnsurePrimaryHouseholdActiveAsync(
        Guid primaryClientProfileId,
        CancellationToken cancellationToken = default)
    {
        if (primaryClientProfileId == Guid.Empty)
            throw new ArgumentException("A primary client profile is required.", nameof(primaryClientProfileId));

        var profile = await _db.ClientProfiles
            .SingleOrDefaultAsync(x => x.Id == primaryClientProfileId, cancellationToken)
            ?? throw new InvalidOperationException("The primary client profile was not found.");

        var normalizedEmail = NormalizeEmail(profile.NormalizedEmail ?? profile.Email);
        if (string.IsNullOrWhiteSpace(normalizedEmail))
            throw new InvalidOperationException("The primary client profile requires an email address.");

        var nowUtc = DateTime.UtcNow;
        var household = await _db.HouseholdAccounts
            .SingleOrDefaultAsync(x => x.SubscriptionOwnerClientProfileId == profile.Id, cancellationToken);

        if (household is null)
        {
            household = new HouseholdAccount
            {
                SubscriptionOwnerClientProfileId = profile.Id,
                Status = HouseholdAccountStatus.Active,
                CreatedUtc = nowUtc,
                UpdatedUtc = nowUtc,
                ActivatedUtc = nowUtc
            };
            _db.HouseholdAccounts.Add(household);
        }
        else
        {
            household.Status = HouseholdAccountStatus.Active;
            household.ActivatedUtc ??= nowUtc;
            household.SuspendedUtc = null;
            household.ClosedUtc = null;
            household.StatusReasonCode = null;
            household.UpdatedUtc = nowUtc;
        }

        var membership = await _db.HouseholdMemberships
            .SingleOrDefaultAsync(
                x => x.HouseholdAccountId == household.Id && x.Role == HouseholdMemberRole.PrimaryOwner,
                cancellationToken);

        if (membership is null)
        {
            membership = new HouseholdMembership
            {
                HouseholdAccountId = household.Id,
                ClientProfileId = profile.Id,
                Role = HouseholdMemberRole.PrimaryOwner,
                Status = HouseholdMembershipStatus.Active,
                NormalizedEmail = normalizedEmail,
                ExternalIdentityObjectId = NormalizeToken(profile.ExternalIdentityObjectId),
                CreatedUtc = nowUtc,
                UpdatedUtc = nowUtc,
                ActivatedUtc = nowUtc,
                CreatedByUserId = NormalizeToken(profile.ExternalIdentityObjectId) ?? profile.ClientUserId,
                UpdatedByUserId = NormalizeToken(profile.ExternalIdentityObjectId) ?? profile.ClientUserId
            };
            _db.HouseholdMemberships.Add(membership);
        }
        else
        {
            if (membership.ClientProfileId.HasValue && membership.ClientProfileId != profile.Id)
                throw new InvalidOperationException("The household primary membership is bound to another profile.");

            membership.ClientProfileId = profile.Id;
            membership.NormalizedEmail = normalizedEmail;
            membership.ExternalIdentityObjectId = NormalizeToken(profile.ExternalIdentityObjectId);
            membership.Status = HouseholdMembershipStatus.Active;
            membership.ActivatedUtc ??= nowUtc;
            membership.SuspendedUtc = null;
            membership.RemovedUtc = null;
            membership.StatusReasonCode = null;
            membership.UpdatedUtc = nowUtc;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return household;
    }

    public async Task<HouseholdAccessResolution> ResolveActiveAccessAsync(
        Guid clientProfileId,
        CancellationToken cancellationToken = default)
    {
        if (clientProfileId == Guid.Empty)
            return new HouseholdAccessResolution(false, null, null, null, "CLIENT_PROFILE_REQUIRED");

        var membership = await _db.HouseholdMemberships
            .AsNoTracking()
            .Join(
                _db.HouseholdAccounts.AsNoTracking(),
                member => member.HouseholdAccountId,
                household => household.Id,
                (member, household) => new { member, household })
            .SingleOrDefaultAsync(
                x => x.member.ClientProfileId == clientProfileId,
                cancellationToken);

        if (membership is not null)
        {
            if (membership.member.Status != HouseholdMembershipStatus.Active)
            {
                return new HouseholdAccessResolution(
                    false,
                    membership.household.Id,
                    membership.household.SubscriptionOwnerClientProfileId,
                    membership.member.Role,
                    $"HOUSEHOLD_MEMBERSHIP_{membership.member.Status.ToString().ToUpperInvariant()}");
            }

            if (membership.household.Status != HouseholdAccountStatus.Active)
            {
                return new HouseholdAccessResolution(
                    false,
                    membership.household.Id,
                    membership.household.SubscriptionOwnerClientProfileId,
                    membership.member.Role,
                    $"HOUSEHOLD_{membership.household.Status.ToString().ToUpperInvariant()}");
            }

            if (!await HasUsableOwnerSubscriptionAsync(
                    membership.household.SubscriptionOwnerClientProfileId,
                    cancellationToken))
            {
                // The membership remains historically active, but it grants
                // no shared-data access while the one paid household
                // subscription is lapsed, suspended, or ended.
                return new HouseholdAccessResolution(
                    false,
                    membership.household.Id,
                    membership.household.SubscriptionOwnerClientProfileId,
                    membership.member.Role,
                    "HOUSEHOLD_SUBSCRIPTION_INACTIVE");
            }

            return new HouseholdAccessResolution(
                true,
                membership.household.Id,
                membership.household.SubscriptionOwnerClientProfileId,
                membership.member.Role,
                null);
        }

        // Existing activated subscriptions predate the household aggregate. This
        // narrow, idempotent hydration writes the new authority from the one
        // existing paid subscription; it is not a legacy entitlement fallback.
        var hasSubscription = await HasUsableOwnerSubscriptionAsync(
            clientProfileId,
            cancellationToken);

        if (!hasSubscription)
            return new HouseholdAccessResolution(false, null, null, null, "HOUSEHOLD_MEMBERSHIP_REQUIRED");

        await EnsurePrimaryHouseholdActiveAsync(clientProfileId, cancellationToken);
        _logger.LogInformation(
            "Hydrated household authority for an existing subscription. ClientProfileId={ClientProfileId}",
            clientProfileId);

        return new HouseholdAccessResolution(
            true,
            await _db.HouseholdAccounts
                .Where(x => x.SubscriptionOwnerClientProfileId == clientProfileId)
                .Select(x => (Guid?)x.Id)
                .SingleAsync(cancellationToken),
            clientProfileId,
            HouseholdMemberRole.PrimaryOwner,
            null);
    }

    public async Task<HouseholdPartnerInvitationResult> IssuePartnerInvitationAsync(
        IssuePartnerInvitationCommand command,
        CancellationToken cancellationToken = default)
    {
        var partnerEmail = NormalizeEmail(command.PartnerEmail);
        if (command.PrimaryClientProfileId == Guid.Empty)
            throw new ArgumentException("A primary client profile is required.", nameof(command));
        if (string.IsNullOrWhiteSpace(partnerEmail))
            throw new InvalidOperationException("A partner email is required.");

        var ownerHasActiveEntitlement = await _db.ClientSubscriptions
            .AsNoTracking()
            .AnyAsync(
                x => x.ClientProfileId == command.PrimaryClientProfileId &&
                     (x.Status == ClientSubscriptionStatus.Active ||
                      x.Status == ClientSubscriptionStatus.GracePeriod),
                cancellationToken);
        if (!ownerHasActiveEntitlement)
        {
            throw new InvalidOperationException(
                "A partner invitation can be issued only after the primary subscription is active.");
        }

        var household = await EnsurePrimaryHouseholdActiveAsync(
            command.PrimaryClientProfileId,
            cancellationToken);
        var owner = await _db.ClientProfiles
            .AsNoTracking()
            .SingleAsync(x => x.Id == command.PrimaryClientProfileId, cancellationToken);

        if (string.Equals(partnerEmail, NormalizeEmail(owner.NormalizedEmail ?? owner.Email), StringComparison.Ordinal))
            throw new InvalidOperationException("The partner must use a distinct email address.");

        return await CreatePartnerInvitationAsync(
            command,
            household,
            owner,
            provisionExternalIdentity: true,
            cancellationToken);
    }

    public async Task<HouseholdPartnerInvitationResult> StagePartnerInvitationAsync(
        IssuePartnerInvitationCommand command,
        CancellationToken cancellationToken = default)
    {
        if (command.PrimaryClientProfileId == Guid.Empty)
            throw new ArgumentException("A primary client profile is required.", nameof(command));

        var partnerEmail = NormalizeEmail(command.PartnerEmail);
        if (string.IsNullOrWhiteSpace(partnerEmail))
            throw new InvalidOperationException("A partner email is required.");

        var owner = await _db.ClientProfiles
            .SingleOrDefaultAsync(x => x.Id == command.PrimaryClientProfileId, cancellationToken)
            ?? throw new InvalidOperationException("The primary client profile was not found.");

        if (string.Equals(partnerEmail, NormalizeEmail(owner.NormalizedEmail ?? owner.Email), StringComparison.Ordinal))
            throw new InvalidOperationException("The partner must use a distinct email address.");

        if (await HasUsableOwnerSubscriptionAsync(owner.Id, cancellationToken))
            return await IssuePartnerInvitationAsync(command, cancellationToken);

        var household = await EnsurePrimaryHouseholdPendingActivationAsync(
            owner,
            command.CreatedByUserId,
            cancellationToken);

        return await CreatePartnerInvitationAsync(
            command,
            household,
            owner,
            provisionExternalIdentity: false,
            cancellationToken);
    }

    private async Task<HouseholdPartnerInvitationResult> CreatePartnerInvitationAsync(
        IssuePartnerInvitationCommand command,
        HouseholdAccount household,
        ClientProfile owner,
        bool provisionExternalIdentity,
        CancellationToken cancellationToken)
    {
        var partnerEmail = NormalizeEmail(command.PartnerEmail);

        var nowUtc = DateTime.UtcNow;
        var membership = await _db.HouseholdMemberships
            .SingleOrDefaultAsync(
                x => x.HouseholdAccountId == household.Id && x.Role == HouseholdMemberRole.Partner,
                cancellationToken);

        if (membership is not null && membership.Status == HouseholdMembershipStatus.Active)
            throw new InvalidOperationException("This household already has an active partner membership.");

        if (membership is null)
        {
            membership = new HouseholdMembership
            {
                HouseholdAccountId = household.Id,
                Role = HouseholdMemberRole.Partner,
                CreatedUtc = nowUtc,
                CreatedByUserId = NormalizeToken(command.CreatedByUserId)
            };
            _db.HouseholdMemberships.Add(membership);
        }

        membership.ClientProfileId = null;
        membership.NormalizedEmail = partnerEmail;
        membership.ExternalIdentityObjectId = null;
        membership.Status = HouseholdMembershipStatus.PendingInvitation;
        membership.ActivatedUtc = null;
        membership.SuspendedUtc = null;
        membership.RemovedUtc = null;
        membership.StatusReasonCode = null;
        membership.UpdatedUtc = nowUtc;
        membership.UpdatedByUserId = NormalizeToken(command.CreatedByUserId);

        var staleInvitations = await _db.HouseholdMemberInvitations
            .Where(x => x.HouseholdMembershipId == membership.Id &&
                        x.Status == HouseholdInvitationStatus.Pending)
            .ToListAsync(cancellationToken);
        foreach (var staleInvitation in staleInvitations)
        {
            staleInvitation.Status = HouseholdInvitationStatus.Revoked;
            staleInvitation.RevokedUtc = nowUtc;
        }

        var token = CreateOpaqueToken();
        var invitation = new HouseholdMemberInvitation
        {
            HouseholdAccountId = household.Id,
            HouseholdMembershipId = membership.Id,
            TokenHash = Hash(token),
            IntendedNormalizedEmail = partnerEmail,
            InvitedFirstName = (command.PartnerFirstName ?? string.Empty).Trim(),
            InvitedLastName = (command.PartnerLastName ?? string.Empty).Trim(),
            Status = HouseholdInvitationStatus.Pending,
            ExpiresUtc = command.ExpiresUtc ?? nowUtc.AddDays(14),
            CreatedByUserId = NormalizeToken(command.CreatedByUserId) ?? string.Empty,
            CreatedUtc = nowUtc
        };
        _db.HouseholdMemberInvitations.Add(invitation);

        await _db.SaveChangesAsync(cancellationToken);

        if (provisionExternalIdentity)
        {
            // Entra is a downstream projection. The membership and invitation
            // were written first, so a transient Graph failure cannot create
            // an untracked paid or active partner.
            var identity = await _entraLifecycle.EnsureExternalIdentityAsync(
                command.PartnerFirstName ?? string.Empty,
                command.PartnerLastName ?? string.Empty,
                partnerEmail,
                cancellationToken);
            membership.ExternalIdentityObjectId = identity.ObjectId;
            membership.UpdatedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
        }

        _logger.LogInformation(
            provisionExternalIdentity
                ? "Partner household invitation issued. HouseholdAccountId={HouseholdAccountId} MembershipId={MembershipId}"
                : "Partner household invitation staged pending primary activation. HouseholdAccountId={HouseholdAccountId} MembershipId={MembershipId}",
            household.Id,
            membership.Id);

        return new HouseholdPartnerInvitationResult(invitation, membership, token);
    }

    public async Task<HouseholdPartnerAcceptanceResult> AcceptPartnerInvitationAsync(
        string plainTextToken,
        AcceptPartnerInvitationCommand command,
        CancellationToken cancellationToken = default)
    {
        var tokenHash = Hash(plainTextToken);
        var email = NormalizeEmail(command.Email);
        if (string.IsNullOrWhiteSpace(tokenHash) || string.IsNullOrWhiteSpace(email))
            throw new InvalidOperationException("A valid household invitation and email are required.");

        var invitation = await _db.HouseholdMemberInvitations
            .SingleOrDefaultAsync(x => x.TokenHash == tokenHash, cancellationToken)
            ?? throw new InvalidOperationException("The household invitation is not available.");

        var nowUtc = DateTime.UtcNow;
        if (invitation.Status == HouseholdInvitationStatus.Pending && invitation.ExpiresUtc <= nowUtc)
        {
            invitation.Status = HouseholdInvitationStatus.Expired;
            await _db.SaveChangesAsync(cancellationToken);
        }

        if (invitation.Status != HouseholdInvitationStatus.Pending)
            throw new InvalidOperationException("The household invitation is no longer available.");

        if (!string.Equals(email, invitation.IntendedNormalizedEmail, StringComparison.Ordinal))
            throw new InvalidOperationException("Accept the household invitation with the invited email address.");

        var membership = await _db.HouseholdMemberships
            .SingleAsync(x => x.Id == invitation.HouseholdMembershipId, cancellationToken);
        var household = await _db.HouseholdAccounts
            .SingleAsync(x => x.Id == invitation.HouseholdAccountId, cancellationToken);

        if (membership.Role != HouseholdMemberRole.Partner ||
            membership.Status != HouseholdMembershipStatus.PendingInvitation ||
            household.Status != HouseholdAccountStatus.Active)
        {
            throw new InvalidOperationException("The household invitation is no longer eligible for acceptance.");
        }

        if (!await HasUsableOwnerSubscriptionAsync(
                household.SubscriptionOwnerClientProfileId,
                cancellationToken))
        {
            throw new InvalidOperationException(
                "The primary household subscription is not active, so this invitation cannot be accepted.");
        }

        var identity = await _entraLifecycle.EnsureExternalIdentityAsync(
            command.FirstName,
            command.LastName,
            email,
            cancellationToken);

        var profile = await _db.ClientProfiles
            .SingleOrDefaultAsync(x => x.NormalizedEmail == email, cancellationToken);
        var profileCreated = profile is null;
        if (profile is null)
        {
            profile = new ClientProfile
            {
                ClientUserId = identity.ObjectId,
                ExternalIdentityObjectId = identity.ObjectId,
                FirstName = (command.FirstName ?? string.Empty).Trim(),
                LastName = (command.LastName ?? string.Empty).Trim(),
                Email = email,
                NormalizedEmail = email,
                CreatedUtc = nowUtc,
                UpdatedUtc = nowUtc
            };
            _db.ClientProfiles.Add(profile);
        }
        else
        {
            var existingHousehold = await _db.HouseholdMemberships
                .AsNoTracking()
                .AnyAsync(
                    x => x.ClientProfileId == profile.Id && x.HouseholdAccountId != household.Id &&
                         x.Status != HouseholdMembershipStatus.Removed,
                    cancellationToken);
            if (existingHousehold)
                throw new InvalidOperationException("This person is already part of another household.");

            profile.FirstName = (command.FirstName ?? profile.FirstName ?? string.Empty).Trim();
            profile.LastName = (command.LastName ?? profile.LastName ?? string.Empty).Trim();
            profile.Email = email;
            profile.NormalizedEmail = email;
            profile.ClientUserId = identity.ObjectId;
            profile.ExternalIdentityObjectId = identity.ObjectId;
            profile.UpdatedUtc = nowUtc;
        }

        membership.ClientProfileId = profile.Id;
        membership.ExternalIdentityObjectId = identity.ObjectId;
        membership.NormalizedEmail = email;
        membership.Status = HouseholdMembershipStatus.Active;
        membership.ActivatedUtc = nowUtc;
        membership.UpdatedUtc = nowUtc;
        membership.StatusReasonCode = null;
        membership.UpdatedByUserId = identity.ObjectId;

        invitation.Status = HouseholdInvitationStatus.Accepted;
        invitation.AcceptedUtc = nowUtc;

        // Agent-client links are an access projection for the partner profile;
        // the household membership above remains the single household authority.
        var owner = await _db.ClientProfiles
            .AsNoTracking()
            .SingleAsync(x => x.Id == household.SubscriptionOwnerClientProfileId, cancellationToken);
        var ownerLinks = await _db.AgentClients
            .AsNoTracking()
            .Where(x => x.ClientUserId == owner.ClientUserId)
            .ToListAsync(cancellationToken);
        var existingPartnerAgentIds = await _db.AgentClients
            .Where(x => x.ClientUserId == profile.ClientUserId)
            .Select(x => x.AgentUserId)
            .ToListAsync(cancellationToken);
        foreach (var ownerLink in ownerLinks.Where(x => !existingPartnerAgentIds.Contains(x.AgentUserId, StringComparer.OrdinalIgnoreCase)))
        {
            _db.AgentClients.Add(new AgentClient
            {
                AgentUserId = ownerLink.AgentUserId,
                AgentUpn = ownerLink.AgentUpn,
                ClientUserId = profile.ClientUserId
            });
        }

        await _db.SaveChangesAsync(cancellationToken);
        return new HouseholdPartnerAcceptanceResult(household, membership, profile, profileCreated);
    }

    public async Task MarkPartnerInvitationSentAsync(
        Guid invitationId,
        CancellationToken cancellationToken = default)
    {
        if (invitationId == Guid.Empty)
            throw new ArgumentException("Invitation id is required.", nameof(invitationId));

        var invitation = await _db.HouseholdMemberInvitations
            .SingleOrDefaultAsync(x => x.Id == invitationId, cancellationToken)
            ?? throw new InvalidOperationException("The household invitation was not found.");

        if (invitation.Status != HouseholdInvitationStatus.Pending)
            throw new InvalidOperationException("Only pending household invitations can be marked as sent.");

        invitation.SentUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<HouseholdPartnerInvitationResult?> CreatePendingPartnerInvitationForDeliveryAsync(
        Guid invitationId,
        CancellationToken cancellationToken = default)
    {
        if (invitationId == Guid.Empty)
            throw new ArgumentException("Invitation id is required.", nameof(invitationId));

        var invitation = await _db.HouseholdMemberInvitations
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == invitationId, cancellationToken);
        if (invitation is null ||
            invitation.Status != HouseholdInvitationStatus.Pending ||
            invitation.SentUtc.HasValue ||
            invitation.ExpiresUtc <= DateTime.UtcNow)
        {
            return null;
        }

        var household = await _db.HouseholdAccounts
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == invitation.HouseholdAccountId, cancellationToken);
        if (household is null || household.Status != HouseholdAccountStatus.Active ||
            !await HasUsableOwnerSubscriptionAsync(
                household.SubscriptionOwnerClientProfileId,
                cancellationToken))
        {
            return null;
        }

        return await IssuePartnerInvitationAsync(
            new IssuePartnerInvitationCommand(
                household.SubscriptionOwnerClientProfileId,
                invitation.InvitedFirstName,
                invitation.InvitedLastName,
                invitation.IntendedNormalizedEmail,
                invitation.CreatedByUserId,
                invitation.ExpiresUtc),
            cancellationToken);
    }

    public async Task RemoveMemberAsync(
        Guid clientProfileId,
        string reasonCode,
        string? actorUserId = null,
        CancellationToken cancellationToken = default)
    {
        var membership = await _db.HouseholdMemberships
            .SingleOrDefaultAsync(x => x.ClientProfileId == clientProfileId, cancellationToken);
        if (membership is null)
            return;

        if (membership.Role == HouseholdMemberRole.PrimaryOwner)
        {
            // A primary owner is the FK anchor for the paid household and its
            // shared finance. Removing it is a distinct household-closure
            // operation, not a member-removal side effect. Refuse the generic
            // person-delete path so it cannot orphan a partner or shared data.
            throw new InvalidOperationException(
                "The primary household owner cannot be deleted from the generic client path. Close or transfer the household explicitly first.");
        }
        else
        {
            // A removed partner keeps their separate Entra identity, but it
            // must immediately lose this app's assignment and all sessions.
            // The central lifecycle service is the only Graph authority.
            await _entraLifecycle.RevokeClientApplicationAccessAsync(
                clientProfileId,
                cancellationToken);
            Remove(membership, reasonCode, actorUserId);
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    private static void Remove(HouseholdMembership membership, string reasonCode, string? actorUserId)
    {
        membership.Status = HouseholdMembershipStatus.Removed;
        // Preserve email and external-identity audit evidence while releasing
        // the personal-profile FK so deleting a removed partner cannot damage
        // the primary household or its shared finance.
        membership.ClientProfileId = null;
        membership.RemovedUtc = DateTime.UtcNow;
        membership.StatusReasonCode = reasonCode;
        membership.UpdatedUtc = DateTime.UtcNow;
        membership.UpdatedByUserId = NormalizeToken(actorUserId);
    }

    private async Task<HouseholdAccount> EnsurePrimaryHouseholdPendingActivationAsync(
        ClientProfile owner,
        string? actorUserId,
        CancellationToken cancellationToken)
    {
        var email = NormalizeEmail(owner.NormalizedEmail ?? owner.Email);
        if (string.IsNullOrWhiteSpace(email))
            throw new InvalidOperationException("The primary client profile requires an email address.");

        var nowUtc = DateTime.UtcNow;
        var household = await _db.HouseholdAccounts
            .SingleOrDefaultAsync(x => x.SubscriptionOwnerClientProfileId == owner.Id, cancellationToken);
        if (household is null)
        {
            household = new HouseholdAccount
            {
                SubscriptionOwnerClientProfileId = owner.Id,
                Status = HouseholdAccountStatus.PendingActivation,
                CreatedUtc = nowUtc,
                UpdatedUtc = nowUtc,
                StatusReasonCode = "PRIMARY_SUBSCRIPTION_PENDING"
            };
            _db.HouseholdAccounts.Add(household);
        }

        var membership = await _db.HouseholdMemberships
            .SingleOrDefaultAsync(
                x => x.HouseholdAccountId == household.Id && x.Role == HouseholdMemberRole.PrimaryOwner,
                cancellationToken);
        if (membership is null)
        {
            membership = new HouseholdMembership
            {
                HouseholdAccountId = household.Id,
                ClientProfileId = owner.Id,
                Role = HouseholdMemberRole.PrimaryOwner,
                Status = HouseholdMembershipStatus.Suspended,
                NormalizedEmail = email,
                ExternalIdentityObjectId = NormalizeToken(owner.ExternalIdentityObjectId),
                CreatedUtc = nowUtc,
                UpdatedUtc = nowUtc,
                StatusReasonCode = "PRIMARY_SUBSCRIPTION_PENDING",
                CreatedByUserId = NormalizeToken(actorUserId)
            };
            _db.HouseholdMemberships.Add(membership);
        }

        await _db.SaveChangesAsync(cancellationToken);
        return household;
    }

    private async Task<bool> HasUsableOwnerSubscriptionAsync(
        Guid ownerClientProfileId,
        CancellationToken cancellationToken)
    {
        var subscription = await _db.ClientSubscriptions
            .AsNoTracking()
            .Where(x => x.ClientProfileId == ownerClientProfileId)
            .OrderByDescending(x => x.UpdatedUtc)
            .ThenByDescending(x => x.CreatedUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (subscription is null)
            return false;

        if (subscription.CancelAtPeriodEnd &&
            subscription.CurrentPeriodEndUtc.HasValue &&
            subscription.CurrentPeriodEndUtc.Value <= DateTime.UtcNow)
        {
            return false;
        }

        return subscription.Status is ClientSubscriptionStatus.Active or ClientSubscriptionStatus.GracePeriod;
    }

    private static string NormalizeEmail(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant();

    private static string? NormalizeToken(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string Hash(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim()))).ToLowerInvariant();
    }

    private static string CreateOpaqueToken(int byteLength = 32)
    {
        var bytes = RandomNumberGenerator.GetBytes(byteLength);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
