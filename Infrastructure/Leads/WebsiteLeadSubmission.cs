using System.Security.Cryptography;
using System.Text;
using Shared.Meta;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.WebsiteEditing;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Leads;

/// <summary>One persisted lead per scoped form submission; retries reuse its public ID.</summary>
public static class WebsiteLeadSubmission
{
    public static readonly TimeSpan NotificationRetryDelay = TimeSpan.FromMinutes(15);

    public static DateTime NotificationRetryCutoff(DateTime utcNow) =>
        utcNow - NotificationRetryDelay;
    public static Guid ResolveId(WebsiteLead lead, string? submissionId)
    {
        if (!Guid.TryParse(submissionId, out var token) || token == Guid.Empty) return lead.LeadId;
        var key = $"{token:D}|{lead.AgentTrackingProfileId}|{lead.SourcePageKey}|{lead.Email.Trim().ToLowerInvariant()}";
        if (lead.CommerceBusinessId.HasValue)
        {
            if (lead.CommerceBusinessId == Guid.Empty || lead.AgentTrackingProfileId.HasValue)
                throw new InvalidOperationException("A website submission must have exactly one permanent owner.");
            key = $"business:v1|{lead.CommerceBusinessId:N}|{key}";
        }
        return new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(key)).AsSpan(0, 16));
    }

    public static async Task<bool> TryCreateAsync(MasterAppDbContext db, WebsiteLead lead,
        string? submissionId, CancellationToken ct = default, Func<CancellationToken, Task>? persistHandoff = null)
    {
        await AttachPublishedProtectLineageAsync(db, lead, ct);
        lead.LeadId = ResolveId(lead, submissionId);
        if (await db.WebsiteLeads.AsNoTracking().AnyAsync(x => x.LeadId == lead.LeadId, ct)) return false;
        await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction == null
            ? await db.Database.BeginTransactionAsync(ct) : null;
        lead.MetadataJson = MetaLeadTrackingJson.Upsert(lead.MetadataJson, state => state.EventId ??= "lead_" + lead.LeadId.ToString("N"));
        db.WebsiteLeads.Add(lead);
        try
        {
            await db.SaveChangesAsync(ct);
            if (persistHandoff != null) await persistHandoff(ct);
            if (transaction != null) await transaction.CommitAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            if (transaction != null) await transaction.RollbackAsync(ct);
            db.Entry(lead).State = EntityState.Detached;
            if (await db.WebsiteLeads.AsNoTracking().AnyAsync(x => x.LeadId == lead.LeadId, ct)) return false;
            throw;
        }
    }
    private static async Task AttachPublishedProtectLineageAsync(
        MasterAppDbContext db,
        WebsiteLead lead,
        CancellationToken ct)
    {
        if (lead.CommerceBusinessId.HasValue ||
            lead.AgentTrackingProfileId is not Guid profileId || profileId == Guid.Empty ||
            !WebsiteSystemTemplateAuthority.IsProtectedRuntimeSource(lead.SourcePageKey))
            return;

        var ownerKey = await db.AgentTrackingProfiles.AsNoTracking()
            .Where(profile => profile.Id == profileId)
            .Select(profile => profile.AgentUserId)
            .SingleOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(ownerKey)) return;

        var version = await (
            from state in db.Set<WebsiteContentState>().AsNoTracking()
            join published in db.Set<WebsiteContentVersion>().AsNoTracking()
                on state.PublishedVersionId equals published.Id
            where state.SiteKey == WebsiteEditorSiteKeys.Protect &&
                  state.OwnerKey == ownerKey.Trim() &&
                  published.StateId == state.Id
            select published).SingleOrDefaultAsync(ct);
        if (version is null) return;

        lead.WebsiteContentVersionId ??= version.Id;
        lead.WebsiteBindingId ??= WebsiteSystemTemplateAuthority.ResolvePublishedRuntimeFormElementId(
            version,
            lead.SourcePageKey);
    }

    // Retry uses the persisted lead and the existing sender. The lease prevents concurrent replays.
    public static async Task<bool> TryClaimNotificationAsync(MasterAppDbContext db, WebsiteLead lead, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var expired = NotificationRetryCutoff(now);
        if (db.Database.IsRelational())
        {
            var claimed = await db.WebsiteLeads.Where(x => x.Id == lead.Id && x.NotificationSentUtc == null &&
                (x.NotificationAttemptUtc == null || x.NotificationAttemptUtc <= expired))
                .ExecuteUpdateAsync(x => x.SetProperty(p => p.NotificationAttemptUtc, now), ct);
            if (claimed == 0) return false;
            await db.Entry(lead).ReloadAsync(ct);
        }
        else
        {
            if (lead.NotificationSentUtc != null || lead.NotificationAttemptUtc > expired) return false;
            lead.NotificationAttemptUtc = now;
            await db.SaveChangesAsync(ct);
        }
        return true;
    }

    public static async Task CompleteNotificationAsync(MasterAppDbContext db, WebsiteLead lead, bool accepted, CancellationToken ct = default)
    {
        var claimAttempt = lead.NotificationAttemptUtc;
        var completedUtc = DateTime.UtcNow;
        if (db.Database.IsRelational())
        {
            if (claimAttempt.HasValue)
            {
                var claim = db.WebsiteLeads.Where(x => x.Id == lead.Id && x.NotificationSentUtc == null &&
                    x.NotificationAttemptUtc == claimAttempt);
                if (accepted)
                    await claim.ExecuteUpdateAsync(update => update
                        .SetProperty(x => x.NotificationSentUtc, completedUtc)
                        .SetProperty(x => x.Status, x => x.Status == "NotificationFailed" ? "New" : x.Status), ct);
                else
                    await claim.ExecuteUpdateAsync(update => update
                        .SetProperty(x => x.NotificationAttemptUtc, completedUtc)
                        .SetProperty(x => x.Status, x => x.Status == "New" ? "NotificationFailed" : x.Status), ct);
            }
            // Completion can lose to a reclaimed lease. Discard stale tracked values in either case.
            await db.Entry(lead).ReloadAsync(ct);
            return;
        }

        var current = await db.WebsiteLeads.AsNoTracking().Where(x => x.Id == lead.Id)
            .Select(x => new { x.NotificationAttemptUtc, x.NotificationSentUtc, x.Status }).SingleOrDefaultAsync(ct);
        if (!claimAttempt.HasValue || current is null || current.NotificationSentUtc.HasValue ||
            current.NotificationAttemptUtc != claimAttempt)
        {
            await db.Entry(lead).ReloadAsync(ct);
            return;
        }
        lead.NotificationSentUtc = accepted ? completedUtc : null;
        // Keep the failed claim timestamp for the canonical retry delay.
        lead.NotificationAttemptUtc = accepted ? current.NotificationAttemptUtc : completedUtc;
        lead.Status = !accepted && current.Status == "New" ? "NotificationFailed" :
            accepted && current.Status == "NotificationFailed" ? "New" : current.Status;
        await db.SaveChangesAsync(ct);
    }
}
