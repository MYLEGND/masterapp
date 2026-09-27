using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Analytics;

public static class UnifiedAnalyticsWriter
{
    public const string PipelineStamp = "unified_event_mapper_v1";

    public static void Write(MasterAppDbContext db, AnalyticsEvent analyticsEvent)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(analyticsEvent);

        if (!string.Equals(analyticsEvent.PipelineStamp, PipelineStamp, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "AnalyticsEvent must be created through BuildTrackingContext -> UnifiedEventMapper.ToAnalytics before it can be written.");
        }

        db.AnalyticsEvents.Add(analyticsEvent);
    }
    public enum BrowserWriteResult { Accepted, Duplicate, Conflict }

    /// <summary>One persistence/dedupe policy for browser protocol adapters.</summary>
    public static async Task<BrowserWriteResult> PersistBrowserEventAsync(MasterAppDbContext db,
        AnalyticsEvent row, CancellationToken ct = default)
    {
        if (!row.ClientEventId.HasValue || row.ClientEventId == Guid.Empty)
            throw new ArgumentException("Browser events require a stable client event ID.", nameof(row));
        var prior = await db.AnalyticsEvents.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.ClientEventId == row.ClientEventId, ct);
        if (prior is not null) return ClassifyDuplicate(prior, row);
        // Accepted browser facts keep their original envelope identity across every projection.
        row.EventId = row.ClientEventId.Value;
        Write(db, row);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            db.Entry(row).State = EntityState.Detached;
            prior = await db.AnalyticsEvents.AsNoTracking()
                .FirstOrDefaultAsync(candidate => candidate.ClientEventId == row.ClientEventId, ct);
            if (prior is null) throw;
            return ClassifyDuplicate(prior, row);
        }
        return BrowserWriteResult.Accepted;
    }

    private static BrowserWriteResult ClassifyDuplicate(AnalyticsEvent prior, AnalyticsEvent row)
    {
        var matches = prior.AgentTrackingProfileId == row.AgentTrackingProfileId &&
            prior.CommerceBusinessId == row.CommerceBusinessId &&
            prior.WebsiteContentVersionId == row.WebsiteContentVersionId &&
            prior.WebsiteBindingId == row.WebsiteBindingId &&
            string.Equals(prior.Host, row.Host, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(prior.EventType, row.EventType, StringComparison.OrdinalIgnoreCase) &&
            prior.SessionId == row.SessionId && prior.VisitorId == row.VisitorId && prior.PageKey == row.PageKey;
        if (!matches) return BrowserWriteResult.Conflict;
        // Every adapter's duplicate receipt identifies the already-persisted row.
        row.EventId = prior.EventId;
        return BrowserWriteResult.Duplicate;
    }
}
