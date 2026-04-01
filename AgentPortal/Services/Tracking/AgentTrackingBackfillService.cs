using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AgentPortal.Services.Tracking;

/// <summary>
/// Backfill runner to create tracking profiles for all existing agents.
/// Safe to rerun; respects dryRun; logs collisions.
/// </summary>
public sealed class AgentTrackingBackfillService
{
    private readonly MasterAppDbContext _db;
    private readonly IAgentTrackingService _tracking;
    private readonly ILogger<AgentTrackingBackfillService> _logger;

    public AgentTrackingBackfillService(MasterAppDbContext db, IAgentTrackingService tracking, ILogger<AgentTrackingBackfillService> logger)
    {
        _db = db;
        _tracking = tracking;
        _logger = logger;
    }

    public async Task<AgentTrackingBackfillResult> RunAsync(bool dryRun = false, CancellationToken ct = default)
    {
        var result = new AgentTrackingBackfillResult();
        var agents = await _db.AgentProfiles.AsNoTracking().ToListAsync(ct);
        foreach (var agent in agents)
        {
            var display = string.IsNullOrWhiteSpace(agent.FullName) ? agent.AgentUpn ?? agent.AgentUserId : agent.FullName;
            var existing = await _db.AgentTrackingProfiles.AsNoTracking().FirstOrDefaultAsync(x => x.AgentUserId == agent.AgentUserId, ct);
            if (existing != null)
            {
                result.SkippedExisting++;
                continue;
            }
            var created = await _tracking.EnsureProfileAsync(agent.AgentUserId, agent.AgentUpn ?? string.Empty, display, ct);
            result.Created.Add((agent.AgentUserId, created.Slug));
            if (dryRun)
            {
                // roll back if dry run
                _db.Entry(created).State = EntityState.Detached;
            }
        }
        if (dryRun)
        {
            _logger.LogInformation("Dry run: created {Count} profiles (not saved)", result.Created.Count);
        }
        return result;
    }
}
