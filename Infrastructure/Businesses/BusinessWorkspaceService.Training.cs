using Domain.Billing;
using Microsoft.EntityFrameworkCore;
using Shared.Crm;

namespace Infrastructure.Businesses;

public sealed partial class BusinessWorkspaceService
{
    public async Task<BusinessTrainingCohort> TrainingAsync(Guid businessId, string? search, int page,
        Guid? profileId, CancellationToken ct)
    {
        var model = new BusinessTrainingCohort { BusinessId = businessId, Search = (search ?? "").Trim(), Page = Math.Clamp(page, 1, 10000) };
        if (model.Search.Length > 120) model.Search = model.Search[..120];
        if (businessId == Guid.Empty) return model;
        var scope = ClientSubscriptionScope.Training(businessId);
        var offers = db.ClientSubscriptionOffers.AsNoTracking().Where(x => x.CommerceBusinessId == businessId && x.SubscriptionScope == scope);
        var subscriptions = db.ClientSubscriptions.AsNoTracking().Where(x => x.CommerceBusinessId == businessId && x.SubscriptionScope == scope);
        var ids = offers.Select(x => x.ClientProfileId).Union(subscriptions.Select(x => x.ClientProfileId));
        var profiles = db.ClientProfiles.AsNoTracking().Where(x => ids.Contains(x.Id));
        if (profileId.HasValue) profiles = profiles.Where(x => x.Id == profileId.Value);
        if (model.Search.Length > 0) profiles = profiles.Where(x => x.FirstName.Contains(model.Search) || x.LastName.Contains(model.Search) || x.Email.Contains(model.Search) || x.Phone.Contains(model.Search));
        model.Total = await profiles.CountAsync(ct);
        var people = await profiles.OrderBy(x => x.LastName).ThenBy(x => x.FirstName).ThenBy(x => x.Id)
            .Skip((model.Page - 1) * 50).Take(50).Select(x => new { x.Id, x.FirstName, x.LastName, x.Email, x.Phone }).ToListAsync(ct);
        var selected = people.Select(x => x.Id).ToArray();
        var terms = await offers.Where(x => selected.Contains(x.ClientProfileId)).ToListAsync(ct);
        var subs = await subscriptions.Where(x => selected.Contains(x.ClientProfileId)).ToListAsync(ct);
        // Financial rows are scoped through the existing subscription owner; never infer a tenant from email.
        var payments = await db.SubscriptionPayments.AsNoTracking().Where(p => p.ClientSubscriptionId.HasValue && subscriptions.Select(x => x.Id).Contains(p.ClientSubscriptionId.Value))
            .Select(p => new { p.Id, p.ClientSubscriptionId, p.Status, p.AmountCents, p.Currency, p.ProviderRefundId }).ToListAsync(ct);
        var paymentIds = payments.Select(x => x.Id).ToArray();
        var refunds = await db.SubscriptionPaymentRefunds.AsNoTracking().Where(x => paymentIds.Contains(x.SubscriptionPaymentId)).ToListAsync(ct);
        var paidSubscriptionIds = payments.Where(p => p.Status is SubscriptionPaymentStatus.Completed or SubscriptionPaymentStatus.PartiallyRefunded or SubscriptionPaymentStatus.Refunded)
            .Select(p => p.ClientSubscriptionId!.Value).Distinct().ToArray();
        var paidProfiles = await subscriptions.Where(x => paidSubscriptionIds.Contains(x.Id)).Select(x => x.ClientProfileId).Distinct().ToListAsync(ct);
        model.PaidCustomers = paidProfiles.Count;
        foreach (var payment in payments)
        {
            if (payment.Status is SubscriptionPaymentStatus.Failed or SubscriptionPaymentStatus.Canceled) continue;
            if (payment.Status is not (SubscriptionPaymentStatus.Completed or SubscriptionPaymentStatus.PartiallyRefunded or SubscriptionPaymentStatus.Refunded)) { model.UnsettledPayments++; continue; }
            var ledger = refunds.Where(x => x.SubscriptionPaymentId == payment.Id).ToArray();
            var refunded = ledger.Sum(x => x.AmountMinor);
            var valid = payment.AmountCents > 0 && !string.IsNullOrWhiteSpace(payment.Currency) &&
                ledger.All(x => x.State == "Succeeded" && x.AmountMinor > 0 && x.Currency == payment.Currency) &&
                refunded <= payment.AmountCents &&
                (string.IsNullOrEmpty(payment.ProviderRefundId) || ledger.Any(x => x.ProviderReference == payment.ProviderRefundId)) &&
                (payment.Status != SubscriptionPaymentStatus.PartiallyRefunded || refunded > 0 && refunded < payment.AmountCents) &&
                (payment.Status != SubscriptionPaymentStatus.Refunded || refunded == payment.AmountCents);
            if (!valid) { model.UnsettledPayments++; continue; }
            model.NetCollectedMinor[payment.Currency] = model.NetCollectedMinor.GetValueOrDefault(payment.Currency) + payment.AmountCents - refunded;
        }
        model.Contacts = people.Select(person => new BusinessTrainingContact(person.Id,
            string.IsNullOrWhiteSpace(person.FirstName + person.LastName) ? (string.IsNullOrWhiteSpace(person.Email) ? "Training contact" : person.Email) : (person.FirstName + " " + person.LastName).Trim(), person.Email, person.Phone, paidProfiles.Contains(person.Id),
            terms.Where(x => x.ClientProfileId == person.Id).OrderByDescending(x => x.CreatedUtc)
                .Select(x => new BusinessTrainingTerm(x.Id, x.TrainingPlanName ?? "Training", x.MonthlyAmountCents, x.Currency,
                    x.Status.ToString(), subs.Where(s => s.AcceptedOfferId == x.Id).OrderByDescending(s => s.CreatedUtc).Select(s => s.Status.ToString()).FirstOrDefault())).ToList())).ToList();
        return model;
    }
}
