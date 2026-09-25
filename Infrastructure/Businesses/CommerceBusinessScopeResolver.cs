using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Businesses;

public sealed class CommerceBusinessScopeResolver(MasterAppDbContext db)
{
    public Task<CommerceBusiness?> ResolveActiveByKeyAsync(string? key, CancellationToken ct = default)
    {
        var normalized = (key ?? "").Trim().ToLowerInvariant();
        if (normalized.Length == 0) return Task.FromResult<CommerceBusiness?>(null);
        return db.CommerceBusinesses.AsNoTracking().SingleOrDefaultAsync(
            x => x.Key.ToLower() == normalized &&
                 x.IsActive &&
                 x.Status.ToLower() == "active",
            ct);
    }

    public Task<CommerceBusiness?> ResolveActiveByIdAsync(Guid id, CancellationToken ct = default) =>
        db.CommerceBusinesses.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == id &&
                 x.IsActive &&
                 x.Status.ToLower() == "active",
            ct);
}
