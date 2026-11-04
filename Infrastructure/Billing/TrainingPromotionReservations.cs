using Domain.Billing;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Billing;

// One read predicate is shared by advisory catalog presentation and transactional admission.
public static class TrainingPromotionReservations
{
    public static Task<List<Guid>> ReservedClientIdsAsync(MasterAppDbContext db, Guid discountId, DateTime nowUtc, CancellationToken ct = default)
    {
        // Only a closed, expired attempt with affirmative unpaid failure evidence can release a place.
        // A missing subscription, unknown payment, prior activation or accepted offer retains its hold.
        var failedUnpaidOffers = db.ClientSubscriptionOffers.Where(offer =>
            offer.ExpiresUtc != null && offer.ExpiresUtc <= nowUtc &&
            !db.SubscriptionActivationInvitations.Any(i => i.ClientSubscriptionOfferId == offer.Id &&
                (i.RedeemedUtc != null || i.Status == SubscriptionActivationInvitationStatus.Redeemed)) &&
            db.ClientSubscriptions.Any(s => s.AcceptedOfferId == offer.Id) &&
            !db.ClientSubscriptions.Any(s => s.AcceptedOfferId == offer.Id &&
                (s.Status != ClientSubscriptionStatus.ActivationFailed || s.ActivatedUtc != null ||
                 s.LastSuccessfulChargeUtc != null ||
                 (!db.SubscriptionPayments.Any(p => p.ClientSubscriptionId == s.Id) &&
                  db.SubscriptionActivationInvitations.Any(i => i.ClientSubscriptionOfferId == offer.Id &&
                      (i.PaymentStartedUtc != null || i.RedeemedUtc != null ||
                       i.Status == SubscriptionActivationInvitationStatus.PaymentStarted || i.Status == SubscriptionActivationInvitationStatus.Redeemed))) ||
                 (s.ProviderSubscriptionId != null && s.ProviderSubscriptionId != "") ||
                 db.SubscriptionPayments.Any(p => p.ClientSubscriptionId == s.Id &&
                     (p.Status != SubscriptionPaymentStatus.Failed && p.Status != SubscriptionPaymentStatus.Canceled ||
                      p.Retryable || p.CommerceCaptureVerifiedUtc != null)))))
            .Select(offer => offer.Id);
        return db.ClientSubscriptionOffers.Where(x => x.TrainingDiscountId == discountId &&
            (((x.Status == ClientSubscriptionOfferStatus.Draft || x.Status == ClientSubscriptionOfferStatus.Offered) &&
                (!x.ExpiresUtc.HasValue || x.ExpiresUtc > nowUtc)) || x.Status == ClientSubscriptionOfferStatus.Accepted ||
             (!failedUnpaidOffers.Contains(x.Id) &&
                (db.ClientSubscriptions.Any(subscription => subscription.AcceptedOfferId == x.Id) ||
                 db.SubscriptionActivationInvitations.Any(invitation => invitation.ClientSubscriptionOfferId == x.Id &&
                    (invitation.PaymentStartedUtc != null || invitation.RedeemedUtc != null ||
                     invitation.Status == SubscriptionActivationInvitationStatus.PaymentStarted || invitation.Status == SubscriptionActivationInvitationStatus.Redeemed))))))
            .Select(x => x.ClientProfileId).Distinct().ToListAsync(ct);
    }
}
