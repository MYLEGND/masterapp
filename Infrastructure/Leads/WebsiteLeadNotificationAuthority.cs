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
/// Retries failed agent/founder WebsiteLead notifications from the canonical persisted lead row.
/// Business inquiries are excluded because BusinessInquiryNotificationService owns their queue.
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
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MasterAppDbContext>();
                var recipients = scope.ServiceProvider.GetRequiredService<WebsiteIntakeRecipientResolver>();
                var sender = scope.ServiceProvider.GetRequiredService<IWebsiteInquiryEmailSender>();
                var retryCutoff = DateTime.UtcNow.AddMinutes(-15);

                var ids = await db.WebsiteLeads.AsNoTracking()
                    .Where(x =>
                        x.CommerceBusinessId == null &&
                        x.NotificationSentUtc == null &&
                        x.Status == "NotificationFailed" &&
                        x.NotificationAttemptUtc != null &&
                        x.NotificationAttemptUtc <= retryCutoff)
                    .OrderBy(x => x.NotificationAttemptUtc)
                    .ThenBy(x => x.CreatedUtc)
                    .Select(x => x.Id)
                    .Take(25)
                    .ToListAsync(stoppingToken);

                foreach (var id in ids)
                {
                    var lead = await db.WebsiteLeads.SingleOrDefaultAsync(x => x.Id == id, stoppingToken);
                    if (lead is null || lead.NotificationSentUtc.HasValue || lead.CommerceBusinessId.HasValue)
                        continue;

                    var owner = lead.AgentTrackingProfileId is { } agentId && agentId != Guid.Empty
                        ? MarketingOwnerScope.Agent(agentId)
                        : MarketingOwnerScope.Founder;
                    var recipient = await recipients.ResolveAsync(owner, stoppingToken);
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
                            "Recovered website lead notification",
                            "<p>A previously captured website lead is ready for follow-up in the CRM.</p>",
                            replyToEmail: string.IsNullOrWhiteSpace(lead.Email) ? null : lead.Email,
                            saveToSentItems: true,
                            cancellationToken: ct),
                        stoppingToken);

                    if (!result.Sent)
                    {
                        logger.LogWarning(
                            "Website lead notification recovery remains pending for lead {LeadId}.",
                            lead.LeadId);
                    }
                }
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
}
