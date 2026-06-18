using Domain.Entities;
using Infrastructure.Data;

namespace ProtectWebsite.Services.Tracking;

public static class AnalyticsEventWriter
{
    public static async Task WriteAsync(
        MasterAppDbContext db,
        UnifiedEventContext ctx,
        CancellationToken ct = default)
    {
        var evt = UnifiedEventMapper.ToAnalytics(ctx);

        db.AnalyticsEvents.Add(evt);
        await db.SaveChangesAsync(ct);
    }
}
