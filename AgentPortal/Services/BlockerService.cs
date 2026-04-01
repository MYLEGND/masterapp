using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AgentPortal.Services;

public class BlockerService : IBlockerService
{
    private readonly MasterAppDbContext _db;

    public BlockerService(MasterAppDbContext db)
    {
        _db = db;
    }

    public async Task<Blocker> OpenAsync(Blocker blocker, CancellationToken ct = default)
    {
        blocker.CreatedUtc = blocker.CreatedUtc == default ? DateTime.UtcNow : blocker.CreatedUtc;
        _db.Blockers.Add(blocker);
        await _db.SaveChangesAsync(ct);
        return blocker;
    }

    public async Task<Blocker?> ResolveAsync(Guid blockerId, string? notes, CancellationToken ct = default)
    {
        var blocker = await _db.Blockers.FirstOrDefaultAsync(b => b.Id == blockerId, ct);
        if (blocker == null) return null;
        blocker.ResolvedUtc = DateTime.UtcNow;
        blocker.UpdatedUtc = blocker.ResolvedUtc;
        blocker.Status = Domain.Enums.BlockerStatus.Resolved;
        blocker.Notes = string.IsNullOrWhiteSpace(notes) ? blocker.Notes : notes;
        await _db.SaveChangesAsync(ct);
        return blocker;
    }

    public async Task<IReadOnlyList<Blocker>> GetOpenByEntityAsync(string relatedEntityId, string relatedEntityType, CancellationToken ct = default)
    {
        return await _db.Blockers
            .AsNoTracking()
            .Where(b => b.RelatedEntityId == relatedEntityId && b.RelatedEntityType.ToString() == relatedEntityType && b.Status == Domain.Enums.BlockerStatus.Open)
            .OrderBy(b => b.UnblockDueDateUtc)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Blocker>> GetOpenByOwnerAsync(string ownerId, CancellationToken ct = default)
    {
        return await _db.Blockers
            .AsNoTracking()
            .Where(b => b.BlockerOwnerId == ownerId && b.Status == Domain.Enums.BlockerStatus.Open)
            .OrderBy(b => b.UnblockDueDateUtc)
            .ToListAsync(ct);
    }
}
