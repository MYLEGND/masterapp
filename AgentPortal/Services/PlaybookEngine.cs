using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AgentPortal.Services;

/// <summary>
/// Minimal idempotent playbook engine. Uses PlaybookExecutions to prevent duplicate runs.
/// Actual action creation is intentionally minimal for MVP.
/// </summary>
public class PlaybookEngine : IPlaybookEngine
{
    private readonly MasterAppDbContext _db;
    private readonly IExecutionEngine _execution;

    public PlaybookEngine(MasterAppDbContext db, IExecutionEngine execution)
    {
        _db = db;
        _execution = execution;
    }

    public async Task HandleAsync(string eventName, string executionKey, object payload, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(executionKey)) throw new ArgumentException("executionKey required", nameof(executionKey));

        var exists = await _db.PlaybookExecutions.AnyAsync(x => x.ExecutionKey == executionKey, ct);
        if (exists) return; // idempotent skip

        _db.PlaybookExecutions.Add(new PlaybookExecution { ExecutionKey = executionKey, CreatedUtc = DateTime.UtcNow });
        await _db.SaveChangesAsync(ct);

        // TODO: add specific event handling logic; keep lightweight for MVP.
        // Example scaffold (commented):
        // if (eventName == \"proposal-finalized\" && payload is Proposal p) { ... }
    }
}
