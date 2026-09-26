using Domain.Entities;
using Infrastructure.Data;

namespace Infrastructure.Leads;

public sealed record WebsiteLeadNotificationResult(
    bool Claimed,
    bool Sent,
    bool AlreadySent,
    string RecipientEmail);

/// <summary>
/// Single source of truth for durable WebsiteLead notification delivery state.
/// Callers provide message content and transport, but lease ownership and persisted
/// sent/failed state are always governed here.
/// </summary>
public static class WebsiteLeadNotificationAuthority
{
    public static Task<bool> TryClaimAsync(
        MasterAppDbContext db,
        WebsiteLead lead,
        CancellationToken cancellationToken = default) =>
        WebsiteLeadSubmission.TryClaimNotificationAsync(db, lead, cancellationToken);

    public static Task CompleteAsync(
        MasterAppDbContext db,
        WebsiteLead lead,
        bool accepted,
        CancellationToken cancellationToken = default) =>
        WebsiteLeadSubmission.CompleteNotificationAsync(db, lead, accepted, cancellationToken);

    public static async Task<WebsiteLeadNotificationResult> DeliverAsync(
        MasterAppDbContext db,
        WebsiteLead lead,
        string recipientEmail,
        Func<CancellationToken, Task<bool>> sendAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(lead);
        ArgumentNullException.ThrowIfNull(sendAsync);

        var recipient = (recipientEmail ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(recipient))
            throw new InvalidOperationException("A scoped lead notification recipient is required.");

        if (lead.NotificationSentUtc.HasValue)
            return new WebsiteLeadNotificationResult(false, true, true, recipient);

        if (!await TryClaimAsync(db, lead, cancellationToken))
        {
            await db.Entry(lead).ReloadAsync(cancellationToken);
            return new WebsiteLeadNotificationResult(
                Claimed: false,
                Sent: lead.NotificationSentUtc.HasValue,
                AlreadySent: lead.NotificationSentUtc.HasValue,
                RecipientEmail: recipient);
        }

        var sent = false;
        try
        {
            sent = await sendAsync(cancellationToken);
        }
        finally
        {
            await CompleteAsync(db, lead, sent, cancellationToken);
        }

        return new WebsiteLeadNotificationResult(true, sent, false, recipient);
    }
}
