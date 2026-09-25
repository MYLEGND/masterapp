using System.Net;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.Leads;
using Microsoft.EntityFrameworkCore;
using Shared.Analytics;

namespace ProtectWebsite.Services.Communication;

// The persisted inquiry is the durable delivery queue; no second copy of customer data.
public sealed class BusinessInquiryNotificationService(MasterAppDbContext db,
    WebsiteIntakeRecipientResolver recipients, IProtectEmailSender sender)
{
    public async Task DeliverPendingAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var ids = await db.Set<CommerceWebsiteInquiry>().AsNoTracking()
            .Where(x => x.NotificationSentUtc == null && (x.NotificationNextAttemptUtc == null || x.NotificationNextAttemptUtc <= now))
            .OrderBy(x => x.CreatedUtc)
            .Select(x => x.Id)
            .Take(25)
            .ToListAsync(ct);

        foreach (var id in ids)
            await DeliverOneAsync(id, ct);
    }

    public async Task<bool> DeliverOneAsync(Guid inquiryId, CancellationToken ct)
    {
        var row = db.Set<CommerceWebsiteInquiry>().Local.FirstOrDefault(x => x.Id == inquiryId)
            ?? await db.Set<CommerceWebsiteInquiry>().SingleOrDefaultAsync(x => x.Id == inquiryId, ct);
        if (row is null) return false;
        if (row.NotificationSentUtc.HasValue) return true;
        if (row.NotificationNextAttemptUtc.HasValue && row.NotificationNextAttemptUtc > DateTime.UtcNow &&
            !string.Equals(row.NotificationStatus, "Pending", StringComparison.Ordinal))
            return false;

        row.NotificationRevision = Guid.NewGuid();
        row.NotificationNextAttemptUtc = DateTime.UtcNow.AddMinutes(5);
        row.NotificationStatus = "Sending";
        row.NotificationAttempts++;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException)
        {
            db.Entry(row).State = EntityState.Detached;
            return false;
        }

        // Re-resolve the current scoped assignment for every attempt. A revoked or
        // changed recipient is never reused from the original submission.
        var recipient = await recipients.ResolveAsync(MarketingOwnerScope.Business(row.CommerceBusinessId), ct);
        var sent = false;
        try
        {
            if (recipient is not null)
            {
                var linkedLead = row.WebsiteLeadId.HasValue
                    ? await db.Set<WebsiteLead>().AsNoTracking().SingleOrDefaultAsync(x => x.LeadId == row.WebsiteLeadId.Value, ct)
                    : null;
                var phone = linkedLead?.Phone;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                sent = await sender.TrySendAsync(recipient, "New website inquiry",
                    $"<p>{WebUtility.HtmlEncode(row.Name)} · {WebUtility.HtmlEncode(row.Email)}{(string.IsNullOrWhiteSpace(phone) ? "" : " · " + WebUtility.HtmlEncode(phone))}</p><p>{WebUtility.HtmlEncode(row.Message).Replace("\n", "<br>")}</p><p>Page: {WebUtility.HtmlEncode(row.SourcePath)}</p>",
                    replyToEmail: row.Email, cancellationToken: timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        catch (Exception) when (!ct.IsCancellationRequested) { }

        row.NotificationSentUtc = sent ? DateTime.UtcNow : null;
        row.NotificationStatus = sent ? "Sent" : recipient is null ? "RecipientUnavailable" : "RetryPending";
        row.NotificationNextAttemptUtc = sent ? null : DateTime.UtcNow.AddMinutes(Math.Min(60, Math.Pow(2, Math.Min(row.NotificationAttempts, 6))));
        row.NotificationRevision = Guid.NewGuid();
        await db.SaveChangesAsync(ct);
        return sent;
    }

}

public sealed class BusinessInquiryNotificationWorker(IServiceScopeFactory scopes,
    ILogger<BusinessInquiryNotificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<BusinessInquiryNotificationService>().DeliverPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception) { logger.LogError("Business inquiry delivery did not complete; persisted inquiries will be retried."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
