using Domain.Billing;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Identity;

/// <summary>
/// Keeps email-bound client access artifacts aligned with the canonical email on
/// <see cref="Domain.Entities.ClientProfile"/>. Subscription ownership itself is
/// intentionally profile-ID based and is never re-created when an email changes.
/// </summary>
public sealed record ClientSubscriptionIdentitySyncResult(
    bool EmailChanged,
    int SupersededInvitationCount,
    int InvalidatedContinuationCount)
{
    public bool RequiresReplacementInvitation => SupersededInvitationCount > 0;
}

public interface IClientSubscriptionIdentitySyncService
{
    /// <summary>
    /// Invalidates access artifacts delivered to a previous email address. The
    /// caller owns the database transaction and must save the tracked changes.
    /// </summary>
    Task<ClientSubscriptionIdentitySyncResult> SynchronizeAfterEmailChangeAsync(
        Guid clientProfileId,
        string? previousEmail,
        string? currentEmail,
        CancellationToken cancellationToken = default);
}

public sealed class ClientSubscriptionIdentitySyncService : IClientSubscriptionIdentitySyncService
{
    private readonly MasterAppDbContext _db;

    public ClientSubscriptionIdentitySyncService(MasterAppDbContext db)
    {
        _db = db;
    }

    public async Task<ClientSubscriptionIdentitySyncResult> SynchronizeAfterEmailChangeAsync(
        Guid clientProfileId,
        string? previousEmail,
        string? currentEmail,
        CancellationToken cancellationToken = default)
    {
        var previousNormalizedEmail = NormalizeEmail(previousEmail);
        var currentNormalizedEmail = NormalizeEmail(currentEmail);
        if (clientProfileId == Guid.Empty ||
            string.IsNullOrWhiteSpace(currentNormalizedEmail) ||
            string.Equals(previousNormalizedEmail, currentNormalizedEmail, StringComparison.Ordinal))
        {
            return new ClientSubscriptionIdentitySyncResult(false, 0, 0);
        }

        var nowUtc = DateTime.UtcNow;
        var activeInvitationStatuses = new[]
        {
            SubscriptionActivationInvitationStatus.Pending,
            SubscriptionActivationInvitationStatus.Sent,
            SubscriptionActivationInvitationStatus.Viewed,
            SubscriptionActivationInvitationStatus.PaymentStarted
        };

        // An activation link is a credential delivered to the old address. Do
        // not merely relabel it; retire it and let the owner issue a fresh link
        // to the new address.
        var outstandingInvitations = await _db.SubscriptionActivationInvitations
            .Where(invitation => invitation.ClientProfileId == clientProfileId &&
                                  activeInvitationStatuses.Contains(invitation.Status))
            .ToListAsync(cancellationToken);

        foreach (var invitation in outstandingInvitations)
        {
            invitation.Status = SubscriptionActivationInvitationStatus.Superseded;
            invitation.SupersededUtc = nowUtc;
        }

        // Sign-in and post-payment continuations are single-use credentials too.
        // Consuming them makes a pre-change browser or email link unusable.
        var outstandingContinuations = await _db.ClientIdentityContinuations
            .Where(continuation => continuation.ClientProfileId == clientProfileId &&
                                   continuation.ConsumedUtc == null &&
                                   continuation.ExpiresUtc > nowUtc)
            .ToListAsync(cancellationToken);

        foreach (var continuation in outstandingContinuations)
            continuation.ConsumedUtc = nowUtc;

        return new ClientSubscriptionIdentitySyncResult(
            true,
            outstandingInvitations.Count,
            outstandingContinuations.Count);
    }

    private static string NormalizeEmail(string? email) =>
        string.IsNullOrWhiteSpace(email) ? string.Empty : email.Trim().ToLowerInvariant();
}
