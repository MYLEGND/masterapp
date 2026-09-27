using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Shared.Analytics;

namespace Infrastructure.Analytics;

/// <summary>Resolves historical tracking-profile aliases for one reporting owner.</summary>
public static class AnalyticsTrackingProfileScope
{
    public static async Task<Guid[]?> ResolveAsync(
        MasterAppDbContext db, ScopeContext scope, CancellationToken cancellationToken = default)
    {
        if (scope.ScopeType is not (ScopeType.Agent or ScopeType.Founder))
            return null;

        if (scope.AgentTrackingProfileId is not { } selectedId || selectedId == Guid.Empty ||
            scope.CommerceBusinessId.HasValue)
            return [];

        var upn = await db.AgentTrackingProfiles.AsNoTracking()
            .Where(profile => profile.Id == selectedId)
            .Select(profile => profile.AgentUpn)
            .FirstOrDefaultAsync(cancellationToken);

        // An unresolved identity must never expand to other unassigned profiles.
        if (string.IsNullOrWhiteSpace(upn))
            return [selectedId];

        var ids = await db.AgentTrackingProfiles.AsNoTracking()
            .Where(profile => profile.AgentUpn == upn && profile.Id != Guid.Empty)
            .Select(profile => profile.Id)
            .Distinct()
            .ToListAsync(cancellationToken);
        if (!ids.Contains(selectedId))
            ids.Add(selectedId);
        return ids.ToArray();
    }
}
