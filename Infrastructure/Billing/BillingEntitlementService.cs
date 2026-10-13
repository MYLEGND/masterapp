using Domain.Billing;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.Households;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Billing;

internal sealed class BillingEntitlementService : IBillingEntitlementService
{
    private readonly MasterAppDbContext _db;
    private readonly IHouseholdMembershipService _households;

    public BillingEntitlementService(
        MasterAppDbContext db,
        IHouseholdMembershipService households)
    {
        _db = db;
        _households = households;
    }

    public async Task<BillingEntitlementEvaluationResult> EvaluateAsync(BillingEntitlementEvaluationRequest request, CancellationToken cancellationToken = default)
    {
        var access = await _households.ResolveActiveAccessAsync(
            request.ClientProfileId,
            cancellationToken);
        if (!CanEvaluateSubscription(access))
        {
            return new BillingEntitlementEvaluationResult(
                ClientEntitlementStatus.NotGranted,
                null,
                null,
                null,
                access.ReasonCode ?? "HOUSEHOLD_MEMBERSHIP_REQUIRED",
                ClientEntitlementSourceType.Subscription,
                string.Empty,
                $"{request.EntitlementKey}: household access is not eligible.");
        }

        var ownerProfileId = access.SubscriptionOwnerClientProfileId ?? request.ClientProfileId;
        var subscription = await _db.ClientSubscriptions
            .AsNoTracking()
            .Where(x => x.ClientProfileId == ownerProfileId)
            .OrderByDescending(x => x.UpdatedUtc)
            .ThenByDescending(x => x.CreatedUtc)
            .FirstOrDefaultAsync(cancellationToken);

        return BillingStateMapper.EvaluateSubscription(subscription, request.EntitlementKey, request.EvaluatedUtc);
    }

    public async Task<ClientEntitlement> RefreshAsync(Guid clientProfileId, string entitlementKey, string? reasonCode = null, CancellationToken cancellationToken = default)
    {
        var evaluation = await EvaluateAsync(
            new BillingEntitlementEvaluationRequest(clientProfileId, entitlementKey, DateTime.UtcNow),
            cancellationToken);

        var access = await _households.ResolveActiveAccessAsync(clientProfileId, cancellationToken);
        if (!CanEvaluateSubscription(access))
            throw new InvalidOperationException("Eligible household access is required before entitlement can be refreshed.");

        // Subscription inactivity still refreshes the paid owner's one
        // projection. Explicit membership/household denials cannot enter this
        // writer, and a client with no membership can fall back only to its
        // own subscription.
        var entitlementOwnerProfileId = access.SubscriptionOwnerClientProfileId ?? clientProfileId;
        var isDirectSubscriptionOwner = await _db.ClientSubscriptions
            .AsNoTracking()
            .AnyAsync(x => x.ClientProfileId == entitlementOwnerProfileId, cancellationToken);
        if (!isDirectSubscriptionOwner)
            throw new InvalidOperationException("A household subscription owner is required before entitlement can be refreshed.");

        // One entitlement projection belongs to the paid owner. Partner access
        // is derived from HouseholdMembership, never represented by a copied
        // entitlement record.
        var entitlement = await _db.ClientEntitlements
            .FirstOrDefaultAsync(x => x.ClientProfileId == entitlementOwnerProfileId && x.EntitlementKey == entitlementKey, cancellationToken);

        var nowUtc = DateTime.UtcNow;
        if (entitlement is null)
        {
            entitlement = new ClientEntitlement
            {
                ClientProfileId = entitlementOwnerProfileId,
                EntitlementKey = entitlementKey,
                CreatedUtc = nowUtc
            };
            _db.ClientEntitlements.Add(entitlement);
        }

        entitlement.Status = evaluation.Status;
        entitlement.SourceType = evaluation.SourceType;
        entitlement.SourceId = evaluation.SourceId;
        entitlement.EffectiveUtc = evaluation.EffectiveUtc;
        entitlement.ExpirationUtc = evaluation.ExpirationUtc;
        entitlement.GraceOrSuspensionUtc = evaluation.GraceOrSuspensionUtc;
        entitlement.ReasonCode = reasonCode ?? evaluation.ReasonCode;
        entitlement.UpdatedUtc = nowUtc;

        await _db.SaveChangesAsync(cancellationToken);
        return entitlement;
    }

    private static bool CanEvaluateSubscription(HouseholdAccessResolution access)
    {
        // Only subscription inactivity may fall back to the resolved owner.
        // A missing membership may evaluate only the requesting client's own
        // subscription; explicit membership/household denials stay closed.
        return (access.SubscriptionOwnerClientProfileId.HasValue &&
                (access.HasActiveMembership || access.ReasonCode == "HOUSEHOLD_SUBSCRIPTION_INACTIVE")) ||
               (access.ReasonCode == "HOUSEHOLD_MEMBERSHIP_REQUIRED" &&
                !access.HouseholdAccountId.HasValue &&
                !access.SubscriptionOwnerClientProfileId.HasValue);
    }
}
