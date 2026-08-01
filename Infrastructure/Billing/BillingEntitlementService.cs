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
        if (!access.HasActiveMembership || !access.SubscriptionOwnerClientProfileId.HasValue)
        {
            // Access resolution deliberately closes shared data whenever the
            // paid household subscription is inactive. Still evaluate that
            // one owner subscription here so existing billing reason codes
            // (grace, suspended, past due, cancellation boundary) remain the
            // single entitlement authority for both members.
            var ownerProfileId = access.SubscriptionOwnerClientProfileId ?? request.ClientProfileId;
            var inactiveSubscription = await _db.ClientSubscriptions
                .AsNoTracking()
                .Where(x => x.ClientProfileId == ownerProfileId)
                .OrderByDescending(x => x.UpdatedUtc)
                .ThenByDescending(x => x.CreatedUtc)
                .FirstOrDefaultAsync(cancellationToken);

            return BuildEvaluation(
                inactiveSubscription,
                request.EntitlementKey,
                request.EvaluatedUtc);
        }

        var subscription = await _db.ClientSubscriptions
            .AsNoTracking()
            .Where(x => x.ClientProfileId == access.SubscriptionOwnerClientProfileId.Value)
            .OrderByDescending(x => x.UpdatedUtc)
            .ThenByDescending(x => x.CreatedUtc)
            .FirstOrDefaultAsync(cancellationToken);

        return BuildEvaluation(subscription, request.EntitlementKey, request.EvaluatedUtc);
    }

    public async Task<ClientEntitlement> RefreshAsync(Guid clientProfileId, string entitlementKey, string? reasonCode = null, CancellationToken cancellationToken = default)
    {
        var evaluation = await EvaluateAsync(
            new BillingEntitlementEvaluationRequest(clientProfileId, entitlementKey, DateTime.UtcNow),
            cancellationToken);

        var access = await _households.ResolveActiveAccessAsync(clientProfileId, cancellationToken);
        // A paid owner whose subscription has just lapsed still needs its one
        // existing entitlement projection updated to the terminal state. The
        // partner never reaches this branch with a copied entitlement because
        // only a direct subscription owner can be the fallback.
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

    private static BillingEntitlementEvaluationResult BuildEvaluation(
        ClientSubscription? subscription,
        string entitlementKey,
        DateTime evaluatedUtc)
    {
        if (subscription is null)
        {
            return new BillingEntitlementEvaluationResult(
                ClientEntitlementStatus.NotGranted,
                null,
                null,
                null,
                "NO_SUBSCRIPTION",
                ClientEntitlementSourceType.Subscription,
                string.Empty,
                $"{entitlementKey}: no subscription found.");
        }

        var sourceId = subscription.Id.ToString();

        if (subscription.CancelAtPeriodEnd &&
            subscription.CurrentPeriodEndUtc.HasValue &&
            subscription.CurrentPeriodEndUtc.Value <= evaluatedUtc)
        {
            return new BillingEntitlementEvaluationResult(
                ClientEntitlementStatus.NotGranted,
                subscription.ActivatedUtc ?? subscription.CreatedUtc,
                subscription.CurrentPeriodEndUtc,
                null,
                "SUBSCRIPTION_ENDED_AT_PERIOD_END",
                ClientEntitlementSourceType.Subscription,
                sourceId,
                $"{entitlementKey}: scheduled cancellation reached its period boundary.");
        }

        if (subscription.Status == ClientSubscriptionStatus.Active)
        {
            return new BillingEntitlementEvaluationResult(
                ClientEntitlementStatus.Active,
                subscription.ActivatedUtc ?? subscription.CreatedUtc,
                subscription.CurrentPeriodEndUtc,
                subscription.GracePeriodEndsUtc,
                null,
                ClientEntitlementSourceType.Subscription,
                sourceId,
                $"{entitlementKey}: subscription is active.");
        }

        if (subscription.Status == ClientSubscriptionStatus.GracePeriod ||
            (subscription.GracePeriodEndsUtc.HasValue && subscription.GracePeriodEndsUtc.Value > evaluatedUtc))
        {
            return new BillingEntitlementEvaluationResult(
                ClientEntitlementStatus.GracePeriod,
                subscription.ActivatedUtc ?? subscription.CreatedUtc,
                subscription.CurrentPeriodEndUtc,
                subscription.GracePeriodEndsUtc,
                "SUBSCRIPTION_GRACE_PERIOD",
                ClientEntitlementSourceType.Subscription,
                sourceId,
                $"{entitlementKey}: subscription is in grace period.");
        }

        if (subscription.Status is ClientSubscriptionStatus.Suspended or ClientSubscriptionStatus.Paused)
        {
            return new BillingEntitlementEvaluationResult(
                ClientEntitlementStatus.Suspended,
                subscription.ActivatedUtc ?? subscription.CreatedUtc,
                subscription.CurrentPeriodEndUtc,
                subscription.GracePeriodEndsUtc,
                "SUBSCRIPTION_SUSPENDED",
                ClientEntitlementSourceType.Subscription,
                sourceId,
                $"{entitlementKey}: subscription is suspended.");
        }

        if (subscription.Status == ClientSubscriptionStatus.PastDue)
        {
            return new BillingEntitlementEvaluationResult(
                ClientEntitlementStatus.Restricted,
                subscription.ActivatedUtc ?? subscription.CreatedUtc,
                subscription.CurrentPeriodEndUtc,
                subscription.GracePeriodEndsUtc,
                "SUBSCRIPTION_PAST_DUE",
                ClientEntitlementSourceType.Subscription,
                sourceId,
                $"{entitlementKey}: subscription is past due.");
        }

        return new BillingEntitlementEvaluationResult(
            ClientEntitlementStatus.NotGranted,
            subscription.ActivatedUtc ?? subscription.CreatedUtc,
            subscription.CurrentPeriodEndUtc,
            subscription.GracePeriodEndsUtc,
            $"SUBSCRIPTION_{subscription.Status.ToString().ToUpperInvariant()}",
            ClientEntitlementSourceType.Subscription,
            sourceId,
            $"{entitlementKey}: subscription is not eligible for access.");
    }

}
