using Domain.Enums;
using Infrastructure.Data;
using Infrastructure.Households;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AgentPortal.Services;

public sealed record HouseholdPartnerInvitationDeliveryResult(int Selected, int Sent, int Failed);

/// <summary>
/// Bounded delivery worker for invitations already owned by the household
/// aggregate. It never infers a partner from legacy fields or changes
/// membership status; it only delivers pending invitations whose primary
/// household subscription is now eligible.
/// </summary>
public sealed class HouseholdPartnerInvitationDeliveryService
{
    private readonly MasterAppDbContext _db;
    private readonly IHouseholdMembershipService _households;
    private readonly HouseholdPartnerInvitationEmailService _email;
    private readonly ILogger<HouseholdPartnerInvitationDeliveryService> _logger;

    public HouseholdPartnerInvitationDeliveryService(
        MasterAppDbContext db,
        IHouseholdMembershipService households,
        HouseholdPartnerInvitationEmailService email,
        ILogger<HouseholdPartnerInvitationDeliveryService> logger)
    {
        _db = db;
        _households = households;
        _email = email;
        _logger = logger;
    }

    public async Task<HouseholdPartnerInvitationDeliveryResult> DeliverDueAsync(
        int maxItems,
        CancellationToken cancellationToken = default)
    {
        var nowUtc = DateTime.UtcNow;
        var invitationIds = await _db.HouseholdMemberInvitations
            .AsNoTracking()
            .Where(x => x.Status == HouseholdInvitationStatus.Pending &&
                        x.SentUtc == null &&
                        x.ExpiresUtc > nowUtc)
            .OrderBy(x => x.CreatedUtc)
            .Select(x => x.Id)
            .Take(Math.Clamp(maxItems, 1, 100))
            .ToListAsync(cancellationToken);

        var sent = 0;
        var failed = 0;
        foreach (var invitationId in invitationIds)
        {
            try
            {
                var delivery = await _households.CreatePendingPartnerInvitationForDeliveryAsync(
                    invitationId,
                    cancellationToken);
                if (delivery is null)
                    continue;

                await _email.SendAsync(
                    delivery.Invitation,
                    delivery.PlainTextToken,
                    cancellationToken);
                await _households.MarkPartnerInvitationSentAsync(
                    delivery.Invitation.Id,
                    cancellationToken);
                sent++;
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogError(
                    ex,
                    "Household partner invitation delivery failed. InvitationId={InvitationId}",
                    invitationId);
            }
        }

        return new HouseholdPartnerInvitationDeliveryResult(invitationIds.Count, sent, failed);
    }
}
