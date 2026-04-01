using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AgentPortal.Services;

public class DecisionService : IDecisionService
{
    private readonly MasterAppDbContext _db;

    public DecisionService(MasterAppDbContext db)
    {
        _db = db;
    }

    public async Task<DecisionRecord> CreateDecisionAsync(DecisionRecord decision, CancellationToken ct = default)
    {
        decision.CreatedUtc = decision.CreatedUtc == default ? DateTime.UtcNow : decision.CreatedUtc;
        _db.DecisionRecords.Add(decision);
        await _db.SaveChangesAsync(ct);
        return decision;
    }

    public async Task<IReadOnlyList<DecisionRecord>> GetByEntityAsync(string relatedEntityId, string relatedEntityType, CancellationToken ct = default)
    {
        return await _db.DecisionRecords
            .AsNoTracking()
            .Where(d => d.RelatedEntityId == relatedEntityId && d.RelatedEntityType.ToString() == relatedEntityType)
            .OrderByDescending(d => d.CreatedUtc)
            .ToListAsync(ct);
    }

    public async Task<DecisionRecord?> GetLatestByEntityAsync(string relatedEntityId, string relatedEntityType, CancellationToken ct = default)
    {
        return await _db.DecisionRecords
            .AsNoTracking()
            .Where(d => d.RelatedEntityId == relatedEntityId && d.RelatedEntityType.ToString() == relatedEntityType)
            .OrderByDescending(d => d.CreatedUtc)
            .FirstOrDefaultAsync(ct);
    }
}
