using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.WebsitePublishing;

/// <summary>Deletes expired snapshot payloads, never the publication identities referenced by business records.</summary>
public static class WebsiteSnapshotRetention
{
    public const int Limit = 50;

    // Caller saves this and the tracked state in ONE SaveChanges transaction. The
    // state's revision concurrency token prevents cleanup racing a publisher.
    public static async Task<int> StageAsync(MasterAppDbContext db, WebsiteContentState state,
        CancellationToken cancellationToken = default)
    {
        var added = db.ChangeTracker.Entries<WebsiteContentVersion>()
            .Count(e => e.State == EntityState.Added && e.Entity.StateId == state.Id);
        if (added > 1) throw new InvalidOperationException("Only one publication may be committed at a time.");
        var expired = await db.Set<WebsiteContentVersion>().AsNoTracking()
            .Where(v => v.StateId == state.Id && v.SnapshotRetiredUtc == null)
            .OrderByDescending(v => v.Revision).ThenByDescending(v => v.Id)
            .Skip(Limit - added).Select(v => v.Id).ToListAsync(cancellationToken);
        if (state.PublishedVersionId.HasValue && expired.Contains(state.PublishedVersionId.Value))
            throw new InvalidOperationException("The active publication is outside retention order; reconcile before cleanup.");
        if (expired.Count == 0) return 0;
        var retiredUtc = DateTime.UtcNow;
        foreach (var id in expired)
        {
            var version = db.Set<WebsiteContentVersion>().Local.FirstOrDefault(v => v.Id == id);
            if (version is null)
            {
                version = new WebsiteContentVersion { Id = id };
                db.Attach(version);
            }
            var entry = db.Entry(version);
            entry.Property(v => v.DocumentJson).CurrentValue = "{}";
            entry.Property(v => v.DocumentJson).IsModified = true;
            entry.Property(v => v.CompiledPagesJson).CurrentValue = null;
            entry.Property(v => v.CompiledPagesJson).IsModified = true;
            entry.Property(v => v.ImportReportJson).CurrentValue = null;
            entry.Property(v => v.ImportReportJson).IsModified = true;
            entry.Property(v => v.SnapshotRetiredUtc).CurrentValue = retiredUtc;
            entry.Property(v => v.SnapshotRetiredUtc).IsModified = true;
        }
        db.Entry(state).Property(s => s.Revision).IsModified = true;
        return expired.Count;
    }
}
