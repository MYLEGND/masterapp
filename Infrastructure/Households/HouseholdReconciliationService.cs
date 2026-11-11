using Domain.Billing;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Households;

public sealed class HouseholdReconciliationResult
{
    public int SubscriptionOwnersScanned { get; internal set; }
    public int PrimaryHouseholdsCreated { get; internal set; }
    public int ExistingHouseholds { get; internal set; }
    public int PartnerInviteCandidates { get; internal set; }
    public List<string> Collisions { get; } = new();
    public List<string> InvalidLegacyRecords { get; } = new();
    public List<HouseholdPartnerReconciliationReview> PartnerReviews { get; } = new();
}

public sealed record HouseholdPartnerReconciliationReview(Guid MembershipId, Guid HouseholdAccountId,
    Guid? ClientProfileId, string Classification, bool BillingChangeProposed = false);

/// <summary>
/// Explicit, operator-triggered reconciliation for accounts that predate the
/// household aggregate. It is dry-run safe, idempotent, never removes legacy
/// data, and never creates or activates a partner profile from legacy spouse
/// fields. Partner rows are reported for a separately reviewed invitation.
/// </summary>
public sealed class HouseholdReconciliationService
{
    private readonly MasterAppDbContext _db;
    private readonly IHouseholdMembershipService _households;
    private readonly ILogger<HouseholdReconciliationService> _logger;

    public HouseholdReconciliationService(
        MasterAppDbContext db,
        IHouseholdMembershipService households,
        ILogger<HouseholdReconciliationService> logger)
    {
        _db = db;
        _households = households;
        _logger = logger;
    }

    public async Task<HouseholdReconciliationResult> RunAsync(
        bool dryRun = true,
        CancellationToken cancellationToken = default)
    {
        var result = new HouseholdReconciliationResult();
        var ownerIds = await _db.ClientSubscriptions.Where(scope => scope.SubscriptionScope == Domain.Billing.ClientSubscriptionScope.LegendMembership)
            .AsNoTracking()
            .Where(x => x.Status == ClientSubscriptionStatus.Active ||
                        x.Status == ClientSubscriptionStatus.GracePeriod)
            .Select(x => x.ClientProfileId)
            .Distinct()
            .ToListAsync(cancellationToken);

        foreach (var ownerId in ownerIds)
        {
            result.SubscriptionOwnersScanned++;
            var profile = await _db.ClientProfiles
                .AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == ownerId, cancellationToken);
            if (profile is null)
            {
                result.Collisions.Add($"Subscription owner profile {ownerId} was not found.");
                continue;
            }

            var existingHousehold = await _db.HouseholdAccounts
                .AsNoTracking()
                .AnyAsync(x => x.SubscriptionOwnerClientProfileId == ownerId, cancellationToken);
            if (existingHousehold)
                result.ExistingHouseholds++;
            else if (dryRun)
                result.PrimaryHouseholdsCreated++;
            else
            {
                await _households.EnsurePrimaryHouseholdActiveAsync(ownerId, cancellationToken);
                result.PrimaryHouseholdsCreated++;
            }

            await AddLegacyPartnerCandidateAsync(profile, result, cancellationToken);
            var partners = await (from member in _db.HouseholdMemberships.AsNoTracking()
                                  join household in _db.HouseholdAccounts.AsNoTracking() on member.HouseholdAccountId equals household.Id
                                  where household.SubscriptionOwnerClientProfileId == ownerId && member.Role == HouseholdMemberRole.Partner
                                  select member).ToListAsync(cancellationToken);
            foreach (var partner in partners)
            {
                var person = partner.ClientProfileId.HasValue ? await _db.ClientProfiles.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.Id == partner.ClientProfileId.Value, cancellationToken) : null;
                var hasOwnBilling = person is not null && await _db.ClientSubscriptions.AsNoTracking().AnyAsync(x =>
                    x.ClientProfileId == person.Id && x.SubscriptionScope == ClientSubscriptionScope.LegendMembership, cancellationToken);
                var classification = hasOwnBilling ? "EXISTING_PAYER_RECONCILIATION_REQUIRED" :
                    partner.Status == HouseholdMembershipStatus.Removed ? "REMOVED_PRESERVE_HISTORY" :
                    partner.Status == HouseholdMembershipStatus.Suspended ? "SUSPENDED_REVIEW_REQUIRED" :
                    person is null ? "INVITATION_VERIFICATION_REQUIRED" :
                    string.IsNullOrWhiteSpace(person.ExternalIdentityObjectId) ||
                    !string.Equals(person.ExternalIdentityObjectId, partner.ExternalIdentityObjectId, StringComparison.OrdinalIgnoreCase)
                        ? "IDENTITY_RECONCILIATION_REQUIRED" :
                    partner.Status == HouseholdMembershipStatus.Active ? "VERIFY_CANONICAL_ENTITLEMENT" :
                    "INACTIVE_LINK_REVIEW_REQUIRED";
                result.PartnerReviews.Add(new(partner.Id, partner.HouseholdAccountId, partner.ClientProfileId, classification));
            }
        }

        _logger.LogInformation(
            "Household reconciliation completed. DryRun={DryRun} Scanned={Scanned} Created={Created} Existing={Existing} Candidates={Candidates} Collisions={Collisions}",
            dryRun,
            result.SubscriptionOwnersScanned,
            result.PrimaryHouseholdsCreated,
            result.ExistingHouseholds,
            result.PartnerInviteCandidates,
            result.Collisions.Count);

        return result;
    }

    private async Task AddLegacyPartnerCandidateAsync(
        Domain.Entities.ClientProfile profile,
        HouseholdReconciliationResult result,
        CancellationToken cancellationToken)
    {
        var email = NormalizeEmail(profile.SignificantOtherEmail);
        var firstName = (profile.SignificantOtherFirstName ?? string.Empty).Trim();
        var lastName = (profile.SignificantOtherLastName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(email) && string.IsNullOrWhiteSpace(firstName) && string.IsNullOrWhiteSpace(lastName))
            return;

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(firstName) || string.IsNullOrWhiteSpace(lastName))
        {
            result.InvalidLegacyRecords.Add(
                $"Primary profile {profile.Id} has incomplete legacy partner detail; no invitation candidate was created.");
            return;
        }

        var emailBelongsToProfile = await _db.ClientProfiles
            .AsNoTracking()
            .AnyAsync(x => x.Id != profile.Id && x.NormalizedEmail == email, cancellationToken);
        if (emailBelongsToProfile)
        {
            result.Collisions.Add(
                $"Legacy partner email {email} for primary profile {profile.Id} already belongs to a client profile; operator review is required.");
            return;
        }

        var claimedByOtherHousehold = await _db.HouseholdMemberships
            .AsNoTracking()
            .AnyAsync(
                x => x.NormalizedEmail == email && x.Status != HouseholdMembershipStatus.Removed,
                cancellationToken);
        if (claimedByOtherHousehold)
        {
            result.Collisions.Add(
                $"Legacy partner email {email} for primary profile {profile.Id} already belongs to a household membership; operator review is required.");
            return;
        }

        result.PartnerInviteCandidates++;
    }

    private static string NormalizeEmail(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant();
}
