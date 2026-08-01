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
}

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
        var ownerIds = await _db.ClientSubscriptions
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
