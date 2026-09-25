using System.Security.Cryptography;
using System.Text;
using Shared.Meta;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Leads;

/// <summary>One persisted lead per scoped form submission; retries reuse its public ID.</summary>
public static class WebsiteLeadSubmission
{
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
    // Retry uses the persisted lead and the existing sender. The lease prevents concurrent replays.
    public static async Task<bool> TryClaimNotificationAsync(MasterAppDbContext db, WebsiteLead lead, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var expired = now.AddMinutes(-15);
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
        lead.NotificationSentUtc = accepted ? DateTime.UtcNow : null;
        lead.NotificationAttemptUtc = accepted ? lead.NotificationAttemptUtc : null;
        if (!accepted) lead.Status = "NotificationFailed";
        else if (lead.Status == "NotificationFailed") lead.Status = "New";
        await db.SaveChangesAsync(ct);
    }
}
