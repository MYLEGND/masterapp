using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AgentPortal.Services.Analytics;

/// <summary>
/// Read-only derived analytics metrics (conversion, fulfillment SLA) — enabled via feature flag.
/// </summary>
public sealed class DerivedAnalyticsService
{
    private readonly MasterAppDbContext _db;

    public DerivedAnalyticsService(MasterAppDbContext db)
    {
        _db = db;
    }

    public async Task<DerivedAnalyticsSnapshot> GetAsync(string agentId, CancellationToken ct = default)
    {
        var leads = _db.WorkstationLeadProfiles.AsNoTracking().Where(x => x.AgentUserId == agentId);
        var commitments = _db.Commitments.AsNoTracking().Where(x => x.PromisedById == agentId && x.PromisedByType == Domain.Enums.ActionOwnerType.Agent);

        var leadCount = await leads.CountAsync(ct);
        var wonCount = await leads.CountAsync(x => x.CrmStage == "PolicyPlaced", ct);
        var conversion = leadCount == 0 ? 0m : (decimal)wonCount / leadCount;

        var recentCommitments = await commitments
            .Where(c => c.DueDateUtc >= DateTime.UtcNow.AddDays(-30))
            .Select(c => new { c.Status, c.DueDateUtc })
            .ToListAsync(ct);

        var fulfilled = recentCommitments.Count(c => c.Status == Domain.Enums.CommitmentStatus.Fulfilled);
        var open = recentCommitments.Count(c => c.Status == Domain.Enums.CommitmentStatus.Open);
        var onTimeRate = recentCommitments.Count == 0
            ? 0m
            : (decimal)fulfilled / recentCommitments.Count;

        return new DerivedAnalyticsSnapshot(conversion, onTimeRate, leadCount, fulfilled, open);
    }
}

public sealed record DerivedAnalyticsSnapshot(decimal LeadConversionRate, decimal CommitmentFulfillmentRate, int LeadCount, int CommitmentsFulfilled30d, int CommitmentsOpen30d);
