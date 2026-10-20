using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shared.Analytics;

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
        catch
        {
            sent = false;
        }
        finally
        {
            await CompleteAsync(db, lead, sent, cancellationToken);
        }

        return new WebsiteLeadNotificationResult(true, sent, false, recipient);
    }
}


/// <summary>
/// Recovers unsent WebsiteLead notifications from the canonical persisted lead row.
/// Published business inquiries that have a CommerceWebsiteInquiry remain owned by
/// BusinessInquiryNotificationService; business-owned app inquiries use this same lead lease.
/// </summary>
public sealed class WebsiteLeadNotificationRecoveryWorker(
    IServiceScopeFactory scopes,
    ILogger<WebsiteLeadNotificationRecoveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                await RecoverPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Website lead notification recovery pass failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task RecoverPendingAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MasterAppDbContext>();
        var recipients = scope.ServiceProvider.GetRequiredService<WebsiteIntakeRecipientResolver>();
        var sender = scope.ServiceProvider.GetRequiredService<IWebsiteInquiryEmailSender>();
        var retryCutoff = WebsiteLeadSubmission.NotificationRetryCutoff(DateTime.UtcNow);
        var maxId = await db.WebsiteLeads.AsNoTracking().Where(x => !x.IsDeleted)
            .OrderByDescending(x => x.Id).Select(x => (long?)x.Id).FirstOrDefaultAsync(cancellationToken);
        if (maxId is null) return;

        long? afterId = null;
        var claimed = 0;
        while (claimed < 25)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ids = await PendingRecoveryBatch(db, afterId, maxId.Value, retryCutoff)
                .Select(x => x.Id).ToListAsync(cancellationToken);
            if (ids.Count == 0) return;
            afterId = ids[^1];
            foreach (var id in ids)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (claimed >= 25) return;
                var lead = await db.WebsiteLeads.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
                if (lead is null || lead.IsDeleted || lead.NotificationSentUtc.HasValue ||
                    WebsiteLeadCaptureSafety.ShouldSkipWorkstationCapture(lead) ||
                    lead.CommerceBusinessId == Guid.Empty || lead.AgentTrackingProfileId == Guid.Empty ||
                    (lead.CommerceBusinessId.HasValue && lead.AgentTrackingProfileId.HasValue))
                    continue;

                var owner = lead.CommerceBusinessId is { } scopedBusinessId && scopedBusinessId != Guid.Empty
                    ? MarketingOwnerScope.Business(scopedBusinessId)
                    : lead.AgentTrackingProfileId is { } agentId && agentId != Guid.Empty
                        ? MarketingOwnerScope.Agent(agentId)
                        : MarketingOwnerScope.Founder;
                var recipient = await recipients.ResolveAsync(owner, cancellationToken);
                if (string.IsNullOrWhiteSpace(recipient))
                {
                    logger.LogWarning(
                        "Website lead notification recovery has no current scoped recipient for lead {LeadId}.",
                        lead.LeadId);
                    continue;
                }

                var result = await WebsiteLeadNotificationAuthority.DeliverAsync(
                    db,
                    lead,
                    recipient,
                    ct => sender.TrySendAsync(
                        recipient,
                        WebsiteLeadEmailTemplate.SubjectFor(lead),
                        WebsiteLeadEmailTemplate.Build(WebsiteLeadEmailTemplate.SubjectFor(lead), lead),
                        replyToEmail: string.IsNullOrWhiteSpace(lead.Email) ? null : lead.Email,
                        saveToSentItems: true,
                        cancellationToken: ct),
                    cancellationToken);

                if (result.Claimed) claimed++;
                if (!result.Sent)
                {
                    logger.LogWarning(
                        "Website lead notification recovery remains pending for lead {LeadId}.",
                        lead.LeadId);
                }
            }
        }
    }

    private static IQueryable<WebsiteLead> PendingRecoveryBatch(MasterAppDbContext db, long? afterId, long maxId, DateTime retryCutoff)
    {
        var query = db.WebsiteLeads.AsNoTracking().Where(x => !x.IsDeleted && !x.IsInternal &&
            x.Id <= maxId && x.NotificationSentUtc == null &&
            ((x.NotificationAttemptUtc != null && x.NotificationAttemptUtc <= retryCutoff) ||
             (x.NotificationAttemptUtc == null && (x.InterestType == "LegendInquiry" ||
                 x.InterestType == "ProtectionInquiry" || x.InterestType == "BusinessInquiry"))) &&
            !db.Set<CommerceWebsiteInquiry>().Any(inquiry => inquiry.WebsiteLeadId == x.LeadId));
        if (afterId.HasValue) query = query.Where(x => x.Id > afterId.Value);
        return query.OrderBy(x => x.Id).Take(25);
    }
}
