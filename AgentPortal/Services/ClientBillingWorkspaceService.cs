using System.Text.Json.Nodes;
using AgentPortal.Models;
using Domain.Billing;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AgentPortal.Services;

public sealed record ClientBillingWorkspaceContext(Guid ClientProfileId, object? Snapshot);

public sealed class ClientBillingWorkspaceService
{
    private readonly MasterAppDbContext _db;

    public ClientBillingWorkspaceService(MasterAppDbContext db)
    {
        _db = db;
    }

    public async Task<object?> BuildSnapshotAsync(Guid clientProfileId, string agentOid, CancellationToken cancellationToken = default)
    {
        var latestOffer = await _db.ClientSubscriptionOffers
            .AsNoTracking()
            .Where(x => x.ClientProfileId == clientProfileId && x.OwnerAgentUserId == agentOid)
            .OrderByDescending(x => x.CreatedUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (latestOffer is null)
            return null;

        var latestInvitation = await _db.SubscriptionActivationInvitations
            .AsNoTracking()
            .Where(x => x.ClientProfileId == clientProfileId && x.ClientSubscriptionOfferId == latestOffer.Id)
            .OrderByDescending(x => x.CreatedUtc)
            .FirstOrDefaultAsync(cancellationToken);

        var latestSubscription = await _db.ClientSubscriptions
            .AsNoTracking()
            .Where(x => x.ClientProfileId == clientProfileId && x.OwnerAgentUserId == agentOid)
            .OrderByDescending(x => x.CreatedUtc)
            .FirstOrDefaultAsync(cancellationToken);

        var entitlement = await _db.ClientEntitlements
            .AsNoTracking()
            .Where(x => x.ClientProfileId == clientProfileId && x.EntitlementKey == BillingEntitlementKeys.ClientAppFullAccess)
            .OrderByDescending(x => x.UpdatedUtc)
            .FirstOrDefaultAsync(cancellationToken);

        BillingAuditEntry? lastInvitationDeliveryAudit = null;
        if (latestInvitation is not null)
        {
            lastInvitationDeliveryAudit = await _db.BillingAuditEntries
                .AsNoTracking()
                .Where(x =>
                    x.EntityType == "SubscriptionActivationInvitation" &&
                    x.EntityId == latestInvitation.Id.ToString() &&
                    (x.Action == "sent" || x.Action == "send_failed"))
                .OrderByDescending(x => x.OccurredUtc)
                .FirstOrDefaultAsync(cancellationToken);
        }

        var canRevokeInvitation = latestInvitation is not null &&
            latestInvitation.Status is not SubscriptionActivationInvitationStatus.Redeemed &&
            latestInvitation.Status is not SubscriptionActivationInvitationStatus.Revoked &&
            latestInvitation.Status is not SubscriptionActivationInvitationStatus.Superseded;

        var canResendInvitation = latestInvitation is null ||
            latestInvitation.Status is not SubscriptionActivationInvitationStatus.Redeemed;

        var canCancelSubscription = latestSubscription is not null &&
            latestSubscription.Status is not ClientSubscriptionStatus.Canceled &&
            latestSubscription.CancelAtPeriodEnd == false;

        return new
        {
            offer = new
            {
                id = latestOffer.Id,
                status = latestOffer.Status.ToString(),
                priceType = latestOffer.PriceType.ToString(),
                monthlyAmountCents = latestOffer.MonthlyAmountCents,
                currency = latestOffer.Currency,
                billingAnchorSelectionMode = latestOffer.BillingAnchorSelectionMode.ToString(),
                selectedBillingAnchorDay = latestOffer.SelectedBillingAnchorDay,
                effectiveUtc = latestOffer.EffectiveUtc?.ToString("O"),
                expiresUtc = latestOffer.ExpiresUtc?.ToString("O"),
                createdUtc = latestOffer.CreatedUtc.ToString("O")
            },
            invitation = latestInvitation is null ? null : new
            {
                id = latestInvitation.Id,
                status = latestInvitation.Status.ToString(),
                intendedEmail = latestInvitation.IntendedNormalizedEmail,
                expiresUtc = latestInvitation.ExpiresUtc.ToString("O"),
                createdUtc = latestInvitation.CreatedUtc.ToString("O"),
                lastSentUtc = latestInvitation.LastSentUtc?.ToString("O"),
                sendCount = latestInvitation.SendCount,
                lastDeliveryAction = lastInvitationDeliveryAudit?.Action,
                lastDeliveryUtc = lastInvitationDeliveryAudit?.OccurredUtc.ToString("O"),
                lastDeliverySummary = ExtractAuditSummary(lastInvitationDeliveryAudit?.SanitizedMetadataJson)
            },
            subscription = latestSubscription is null ? null : new
            {
                id = latestSubscription.Id,
                status = latestSubscription.Status.ToString(),
                paymentStanding = latestSubscription.PaymentStanding.ToString(),
                monthlyAmountCents = latestSubscription.MonthlyAmountCents,
                currency = latestSubscription.Currency,
                billingAnchorDay = latestSubscription.BillingAnchorDay,
                currentPeriodStartUtc = latestSubscription.CurrentPeriodStartUtc?.ToString("O"),
                currentPeriodEndUtc = latestSubscription.CurrentPeriodEndUtc?.ToString("O"),
                nextBillingDateUtc = latestSubscription.NextBillingDateUtc?.ToString("O"),
                activatedUtc = latestSubscription.ActivatedUtc?.ToString("O"),
                cancelledUtc = latestSubscription.CancelledUtc?.ToString("O"),
                cancelAtPeriodEnd = latestSubscription.CancelAtPeriodEnd
            },
            entitlement = entitlement is null ? null : new
            {
                status = entitlement.Status.ToString(),
                effectiveUtc = entitlement.EffectiveUtc?.ToString("O"),
                expirationUtc = entitlement.ExpirationUtc?.ToString("O"),
                graceOrSuspensionUtc = entitlement.GraceOrSuspensionUtc?.ToString("O"),
                reasonCode = entitlement.ReasonCode
            },
            actions = new
            {
                canResendInvitation,
                canRevokeInvitation,
                canCancelSubscription
            }
        };
    }

    public async Task<ClientBillingWorkspaceContext?> BuildSnapshotForLeadAsync(string? workstationLeadId, string agentOid, CancellationToken cancellationToken = default)
    {
        var clientProfileId = await ResolveLinkedClientProfileIdAsync(workstationLeadId, agentOid, cancellationToken);
        if (!clientProfileId.HasValue)
            return null;

        var snapshot = await BuildSnapshotAsync(clientProfileId.Value, agentOid, cancellationToken);
        return new ClientBillingWorkspaceContext(clientProfileId.Value, snapshot);
    }

    public async Task<Guid?> ResolveLinkedClientProfileIdAsync(string? workstationLeadId, string agentOid, CancellationToken cancellationToken = default)
    {
        var normalizedLeadId = Normalize(workstationLeadId);
        if (string.IsNullOrWhiteSpace(normalizedLeadId))
            return null;

        var directProfileId = await _db.AgentClients
            .AsNoTracking()
            .Where(x => x.AgentUserId == agentOid)
            .Join(
                _db.ClientProfiles.AsNoTracking(),
                link => link.ClientUserId,
                profile => profile.ClientUserId,
                (link, profile) => new { profile.Id, profile.ClientUserId, profile.UpdatedUtc })
            .Where(x => x.ClientUserId == normalizedLeadId)
            .OrderByDescending(x => x.UpdatedUtc)
            .Select(x => (Guid?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (directProfileId.HasValue)
            return directProfileId;

        var candidates = await _db.AgentClients
            .AsNoTracking()
            .Where(x => x.AgentUserId == agentOid)
            .Join(
                _db.ClientProfiles.AsNoTracking(),
                link => link.ClientUserId,
                profile => profile.ClientUserId,
                (link, profile) => new { profile.Id, profile.CrmNotes, profile.UpdatedUtc })
            .OrderByDescending(x => x.UpdatedUtc)
            .ToListAsync(cancellationToken);

        foreach (var candidate in candidates)
        {
            var meta = ClientCrmMetaSerializer.Deserialize(candidate.CrmNotes);
            if (string.Equals(Normalize(meta.SourceWorkstationLeadId), normalizedLeadId, StringComparison.OrdinalIgnoreCase))
                return candidate.Id;
        }

        return null;
    }

    private static string Normalize(string? value) => (value ?? string.Empty).Trim();

    private static string? ExtractAuditSummary(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
            return null;

        try
        {
            var node = JsonNode.Parse(metadataJson);
            var value = node?["summary"]?.GetValue<string>();
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        catch
        {
            return metadataJson;
        }
    }
}
