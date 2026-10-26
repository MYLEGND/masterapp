using System.Data;
using System.Net;
using System.Net.Mail;
using Domain.Billing;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.Leads;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace ParfaitApp.Services;

/// <summary>Creates an email-bound invitation into the existing recurring billing flow.</summary>
public sealed class TrainingEnrollmentService(MasterAppDbContext db, IBillingOrchestrator billing,
    IWebsiteInquiryEmailSender mail, IConfiguration configuration)
{
    public async Task RequestAsync(Guid businessId, Guid productId, string email, string? promotion, CancellationToken ct)
    {
        if (!Infrastructure.Billing.ClientSubscriptionActivationPolicyOptions.FromConfiguration(configuration).TrainingSubscriptionsEnabled)
            throw new InvalidOperationException("Training enrollment is temporarily unavailable.");
        var normalized = email.Trim().ToLowerInvariant();
        if (normalized.Length > 254 || !MailAddress.TryCreate(normalized, out var address) || address.Address != normalized)
            throw new ArgumentException("Enter a valid email address.");
        if ((promotion?.Length ?? 0) > 120) throw new ArgumentException("The promotion code is too long.");
        var portal = (configuration["ClientPortal:BaseUrl"] ?? configuration["Provisioning:ClientPortalBaseUrl"] ?? "https://client.mylegnd.com").TrimEnd('/');
        if (!Uri.TryCreate(portal, UriKind.Absolute, out var portalUri) || portalUri.Scheme != "https" ||
            !string.IsNullOrEmpty(portalUri.UserInfo) || !string.IsNullOrEmpty(portalUri.Query))
            throw new InvalidOperationException("Training enrollment is unavailable.");
        ClientSubscriptionOffer? offer = null;
        CreateSubscriptionActivationInvitationResult? invitation = null;
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            offer = null;
            invitation = null;
            db.ChangeTracker.Clear();
            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;
            var product = await db.CommerceProducts.Include(x => x.Discounts).SingleOrDefaultAsync(x =>
                x.Id == productId && x.CommerceBusinessId == businessId, ct)
                ?? throw new ArgumentException("The training plan is unavailable.");
            _ = TrainingPlanTerms.FromCatalog(product, promotion);
            var scope = ClientSubscriptionScope.Training(businessId);
            var client = await db.ClientProfiles.SingleOrDefaultAsync(x => x.NormalizedEmail == normalized || x.Email.ToLower() == normalized, ct);
            if (client is not null)
            {
                if (await db.AccountLifecycleRecords.AsNoTracking().AnyAsync(x => x.ProfileId == client.Id &&
                    x.ParticipantType == Domain.Messaging.MessagingParticipantTypes.Client &&
                    (x.State == Domain.Accounts.AccountLifecycleStates.Closed || x.State == Domain.Accounts.AccountLifecycleStates.DeletionRequested), ct)) return;
                // Anonymous requests never change an existing profile or supersede a live offer/checkout.
                var now = DateTime.UtcNow;
                if (await db.ClientSubscriptions.AnyAsync(x => x.ClientProfileId == client.Id && x.SubscriptionScope == scope &&
                        x.Status != ClientSubscriptionStatus.Canceled && x.Status != ClientSubscriptionStatus.ActivationFailed, ct)) return;
                offer = await db.ClientSubscriptionOffers.OrderByDescending(x => x.CreatedUtc).FirstOrDefaultAsync(x =>
                    x.ClientProfileId == client.Id && x.SubscriptionScope == scope &&
                    (x.Status == ClientSubscriptionOfferStatus.Draft || x.Status == ClientSubscriptionOfferStatus.Offered) &&
                    (!x.ExpiresUtc.HasValue || x.ExpiresUtc > now), ct);
                if (offer is not null)
                {
                    if (offer.CommerceProductId != productId || offer.OwnerAgentUserId != "parfait-training-enrollment" ||
                        !offer.IsEligibleForActivation(now)) return;
                    var prior = await db.SubscriptionActivationInvitations.Where(x => x.ClientSubscriptionOfferId == offer.Id)
                        .OrderByDescending(x => x.CreatedUtc).ToListAsync(ct);
                    if (prior.Count > 0 && (prior[0].Status != SubscriptionActivationInvitationStatus.Pending ||
                        prior[0].SendCount != 0 || prior[0].CreatedUtc > now.AddMinutes(-15))) return;
                    if (prior.Any(x => x.Status is SubscriptionActivationInvitationStatus.Sent or SubscriptionActivationInvitationStatus.Viewed
                            or SubscriptionActivationInvitationStatus.PaymentStarted or SubscriptionActivationInvitationStatus.Redeemed) ||
                        prior.Any(x => x.Status == SubscriptionActivationInvitationStatus.Pending && (x.SendCount != 0 || x.CreatedUtc > now.AddMinutes(-15))) ||
                        (prior.Count == 0 && offer.CreatedUtc > now.AddMinutes(-15))) return;
                    // Only stale unsent Pending invitations are recoverable. Keep the original quoted offer;
                    // the canonical invitation service supersedes the old opaque token.
                }
            }
            else
            {
                client = new ClientProfile { Email = normalized, NormalizedEmail = normalized,
                    ClientUserId = $"lead:{Guid.NewGuid():N}", IsVerified = false,
                    CrmStatus = "Lead", CrmNotes = "{\"recordType\":\"Lead\"}" };
                db.ClientProfiles.Add(client);
                await db.SaveChangesAsync(ct);
            }
            offer ??= await billing.CreateClientSubscriptionOfferAsync(new(client.Id, "parfait-training-enrollment",
                ClientSubscriptionOfferPriceType.Custom, null, "USD", BillingAnchorSelectionMode.FirstOfMonth,
                null, null, DateTime.UtcNow.AddHours(24), TrainingBusinessId: businessId,
                TrainingProductId: productId, TrainingPromotionCode: promotion), ct);
            invitation = await billing.CreateSubscriptionActivationInvitationAsync(new(client.Id, offer.Id,
                client.NormalizedEmail ?? client.Email, "parfait-training-enrollment", offer.ExpiresUtc), ct);
            if (transaction is not null) await transaction.CommitAsync(ct);
        });
        if (offer is null || invitation?.Invitation is null || string.IsNullOrWhiteSpace(invitation.PlainTextToken)) return;
        var record = invitation.Invitation;
        var link = $"{portal}/activate/{Uri.EscapeDataString(invitation.PlainTextToken)}";
        var body = $"You requested {offer.TrainingPlanName}: ${offer.MonthlyAmountCents / 100m:F2} USD monthly. " +
            $"{offer.TrainingBenefits}\nReview the billing schedule and recurring authorization before paying: {link}\n" +
            $"This link expires at {record.ExpiresUtc:yyyy-MM-dd HH:mm} UTC. Training benefits do not include LEGEND membership or full Client Portal access. " +
            "If you did not request this, ignore this email. No payment has been taken.";
        bool sent;
        try { sent = await mail.TrySendAsync(record.IntendedNormalizedEmail, "Review your Parfait training subscription",
            $"<p>{WebUtility.HtmlEncode(body).Replace("\n", "<br />")}</p>", body, cancellationToken: ct); }
        catch (OperationCanceledException) { sent = false; }
        using var deliveryAudit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        if (sent) await billing.MarkSubscriptionActivationInvitationSentAsync(new(record.Id, "parfait-training-enrollment"), deliveryAudit.Token);
        else
        {
            await billing.MarkSubscriptionActivationInvitationSendFailureAsync(new(record.Id,
                "parfait-training-enrollment", "TRAINING_INVITATION_DELIVERY_FAILED", "The invitation delivery is unconfirmed."), deliveryAudit.Token);
            // Do not revoke an unconfirmed delivery: it may arrive. A stale, still-Pending
            // invitation can be superseded on a later request after the bounded quiet period.
            throw new InvalidOperationException("Training invitation delivery is temporarily unavailable. Check your email before retrying in 15 minutes.");
        }
    }
}
