using Domain.Billing;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Billing;

internal sealed class BillingEntitlementService : IBillingEntitlementService
{
    private readonly MasterAppDbContext _db;
    private readonly ClientSubscriptionActivationPolicyOptions _activationPolicyOptions;

    public BillingEntitlementService(
        MasterAppDbContext db,
        ClientSubscriptionActivationPolicyOptions activationPolicyOptions)
    {
        _db = db;
        _activationPolicyOptions = activationPolicyOptions;
    }

    public async Task<BillingEntitlementEvaluationResult> EvaluateAsync(BillingEntitlementEvaluationRequest request, CancellationToken cancellationToken = default)
    {
        var profile = await _db.ClientProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == request.ClientProfileId, cancellationToken);

        var subscription = await _db.ClientSubscriptions
            .AsNoTracking()
            .Where(x => x.ClientProfileId == request.ClientProfileId)
            .OrderByDescending(x => x.UpdatedUtc)
            .ThenByDescending(x => x.CreatedUtc)
            .FirstOrDefaultAsync(cancellationToken);

        return BuildEvaluation(profile, subscription, request.EntitlementKey, request.EvaluatedUtc, _activationPolicyOptions);
    }

    public async Task<ClientEntitlement> RefreshAsync(Guid clientProfileId, string entitlementKey, string? reasonCode = null, CancellationToken cancellationToken = default)
    {
        var evaluation = await EvaluateAsync(
            new BillingEntitlementEvaluationRequest(clientProfileId, entitlementKey, DateTime.UtcNow),
            cancellationToken);

        var entitlement = await _db.ClientEntitlements
            .FirstOrDefaultAsync(x => x.ClientProfileId == clientProfileId && x.EntitlementKey == entitlementKey, cancellationToken);

        var nowUtc = DateTime.UtcNow;
        if (entitlement is null)
        {
            entitlement = new ClientEntitlement
            {
                ClientProfileId = clientProfileId,
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
        ClientProfile? profile,
        ClientSubscription? subscription,
        string entitlementKey,
        DateTime evaluatedUtc,
        ClientSubscriptionActivationPolicyOptions activationPolicyOptions)
    {
        if (subscription is null && IsLegacyGrandfatheredProfile(profile, activationPolicyOptions))
        {
            return new BillingEntitlementEvaluationResult(
                ClientEntitlementStatus.Active,
                profile!.CreatedUtc,
                null,
                null,
                "LEGACY_PROFILE_PRE_SUBSCRIPTION_CUTOFF",
                ClientEntitlementSourceType.Manual,
                $"legacy-profile:{profile.Id:N}",
                $"{entitlementKey}: legacy client profile remains active without a subscription.");
        }

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

    private static bool IsLegacyGrandfatheredProfile(
        ClientProfile? profile,
        ClientSubscriptionActivationPolicyOptions activationPolicyOptions)
    {
        if (profile is null)
            return false;

        return EnsureUtc(profile.CreatedUtc) < activationPolicyOptions.SubscriptionRequiredForProfilesCreatedOnOrAfterUtc;
    }

    private static DateTime EnsureUtc(DateTime value)
    {
        return value.Kind == DateTimeKind.Utc
            ? value
            : DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }

}
