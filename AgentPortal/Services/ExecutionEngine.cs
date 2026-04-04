using Domain.Entities;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using System.Data;

namespace AgentPortal.Services;

    public class ExecutionEngine : IExecutionEngine
    {
        private readonly MasterAppDbContext _db;
        private bool? _actionLogsTableAvailable;

        public ExecutionEngine(MasterAppDbContext db)
    {
        _db = db;
    }

    public async Task<ActionItem> CreateActionAsync(ActionItem action, CancellationToken ct = default)
    {
        if (action.ActionSurface == default)
        {
            action.ActionSurface = ActionSurface.CrmOnly;
        }
        if (action.ActionCategory == default)
        {
            action.ActionCategory = ActionCategory.Other;
        }
        action.CreatedUtc = action.CreatedUtc == default ? DateTime.UtcNow : action.CreatedUtc;
        _db.ActionItems.Add(action);
        await TryAddActionLogAsync(new ActionLog
        {
            ActionId = action.Id,
            ActorId = action.CreatedBy,
            Verb = "created",
            OccurredUtc = action.CreatedUtc,
            PayloadJson = null
        }, ct);
        await _db.SaveChangesAsync(ct);
        return action;
    }

    public async Task<ActionItem?> CompleteActionAsync(Guid actionId, string actorId, CancellationToken ct = default)
    {
        var action = await _db.ActionItems.FirstOrDefaultAsync(x => x.Id == actionId, ct);
        if (action == null) return null;
        if (!MatchesActor(action, actorId)) return null;
        action.Status = ActionStatus.Completed;
        action.CompletedAtUtc = DateTime.UtcNow;
        action.UpdatedUtc = action.CompletedAtUtc;
        await TryAddActionLogAsync(new ActionLog
        {
            ActionId = actionId,
            ActorId = actorId,
            Verb = "completed",
            OccurredUtc = action.CompletedAtUtc.Value
        }, ct);
        await _db.SaveChangesAsync(ct);
        return action;
    }

    public async Task<ActionItem?> DismissActionAsync(Guid actionId, string actorId, string reason, CancellationToken ct = default)
    {
        var action = await _db.ActionItems.FirstOrDefaultAsync(x => x.Id == actionId, ct);
        if (action == null) return null;
        if (!MatchesActor(action, actorId)) return null;
        action.Status = ActionStatus.Dismissed;
        action.DismissedReason = reason;
        action.UpdatedUtc = DateTime.UtcNow;
        await TryAddActionLogAsync(new ActionLog
        {
            ActionId = actionId,
            ActorId = actorId,
            Verb = "dismissed",
            PayloadJson = reason,
            OccurredUtc = action.UpdatedUtc.Value
        }, ct);
        await _db.SaveChangesAsync(ct);
        return action;
    }

    public async Task<ActionItem?> ReassignAsync(Guid actionId, ActionOwnerType newOwnerType, string newOwnerId, CancellationToken ct = default)
    {
        var action = await _db.ActionItems.FirstOrDefaultAsync(x => x.Id == actionId, ct);
        if (action == null) return null;
        action.OwnerType = newOwnerType;
        action.OwnerId = newOwnerId;
        action.UpdatedUtc = DateTime.UtcNow;
        await TryAddActionLogAsync(new ActionLog
        {
            ActionId = actionId,
            ActorId = newOwnerId,
            Verb = "reassigned",
            PayloadJson = newOwnerType.ToString(),
            OccurredUtc = action.UpdatedUtc.Value
        }, ct);
        await _db.SaveChangesAsync(ct);
        return action;
    }

    public Task<IReadOnlyList<ActionItem>> GetTodayAsync(string ownerId, CancellationToken ct = default)
    {
        var todayUtc = DateTime.UtcNow.Date;
        var normalizedOwnerId = NormalizeOwnerKey(ownerId);
        return _db.ActionItems
            .AsNoTracking()
            .Where(x => (x.OwnerId == normalizedOwnerId || x.EffectiveAgentOid == normalizedOwnerId)
                        && (x.ActionSurface == ActionSurface.CommandCenter || x.IsEscalated)
                        && (x.DueDateUtc == null ||
                            (x.DueDateUtc >= todayUtc && x.DueDateUtc < todayUtc.AddDays(1)))
                        && x.Status != ActionStatus.Completed && x.Status != ActionStatus.Dismissed)
            .OrderBy(x => x.DueDateUtc)
            .ToListAsync(ct)
            .ContinueWith(t => (IReadOnlyList<ActionItem>)t.Result, ct);
    }

    public Task<IReadOnlyList<ActionItem>> GetOverdueAsync(string ownerId, CancellationToken ct = default)
    {
        var nowUtc = DateTime.UtcNow;
        var normalizedOwnerId = NormalizeOwnerKey(ownerId);
        return _db.ActionItems
            .AsNoTracking()
            .Where(x => (x.OwnerId == normalizedOwnerId || x.EffectiveAgentOid == normalizedOwnerId)
                        && (x.ActionSurface == ActionSurface.CommandCenter || x.IsEscalated)
                        && x.DueDateUtc < nowUtc
                        && x.Status != ActionStatus.Completed && x.Status != ActionStatus.Dismissed)
            .OrderBy(x => x.DueDateUtc)
            .ToListAsync(ct)
            .ContinueWith(t => (IReadOnlyList<ActionItem>)t.Result, ct);
    }

    private static string NormalizeOwnerKey(string ownerId)
        => (ownerId ?? string.Empty).Trim().ToLowerInvariant();

    private static bool MatchesActor(ActionItem action, string actorId)
    {
        var actorKey = NormalizeOwnerKey(actorId);
        if (string.IsNullOrWhiteSpace(actorKey)) return false;
        return string.Equals(NormalizeOwnerKey(action.OwnerId), actorKey, StringComparison.Ordinal)
            || string.Equals(NormalizeOwnerKey(action.EffectiveAgentOid), actorKey, StringComparison.Ordinal);
    }

    public Task<IReadOnlyList<ActionItem>> GetByRelatedAsync(RelatedEntityType relatedEntityType, string relatedEntityId, CancellationToken ct = default)
    {
        return _db.ActionItems
            .AsNoTracking()
            .Where(x => x.RelatedEntityType == relatedEntityType && x.RelatedEntityId == relatedEntityId && x.Status != ActionStatus.Dismissed)
            .OrderByDescending(x => x.CreatedUtc)
            .ToListAsync(ct)
            .ContinueWith(t => (IReadOnlyList<ActionItem>)t.Result, ct);
    }

    public Task<ActionItem?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return _db.ActionItems.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
    }

    public async Task<ActionItem?> UpdateActionAsync(Guid id, string title, string? description, DateTime? dueDateUtc, ActionPriority priority, CancellationToken ct = default)
    {
        var action = await _db.ActionItems.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (action == null) return null;

        action.Title = title.Trim();
        action.Description = description?.Trim() ?? string.Empty;
        action.DueDateUtc = dueDateUtc;
        action.Priority = priority;
        action.UpdatedUtc = DateTime.UtcNow;

        await TryAddActionLogAsync(new ActionLog
        {
            ActionId = id,
            ActorId = action.OwnerId,
            Verb = "updated",
            PayloadJson = $"{title}|{priority}|{dueDateUtc}",
            OccurredUtc = action.UpdatedUtc.Value
        }, ct);

        await _db.SaveChangesAsync(ct);
        return action;
    }

    public async Task<bool> DeleteActionAsync(Guid id, CancellationToken ct = default)
    {
        var action = await _db.ActionItems.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (action == null) return false;

        _db.ActionItems.Remove(action);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    private async Task TryAddActionLogAsync(ActionLog log, CancellationToken ct)
    {
        if (await IsActionLogsTableAvailableAsync(ct))
        {
            _db.ActionLogs.Add(log);
        }
    }

    private async Task<bool> IsActionLogsTableAvailableAsync(CancellationToken ct)
    {
        if (_actionLogsTableAvailable.HasValue) return _actionLogsTableAvailable.Value;

        var provider = _db.Database.ProviderName ?? string.Empty;
        if (!provider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            _actionLogsTableAvailable = true;
            return true;
        }

        var connection = _db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        try
        {
            if (shouldClose) await connection.OpenAsync(ct);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='ActionLogs' LIMIT 1;";
            var scalar = await cmd.ExecuteScalarAsync(ct);
            _actionLogsTableAvailable = scalar != null && scalar != DBNull.Value;
            return _actionLogsTableAvailable.Value;
        }
        catch
        {
            // If schema probing fails, fail soft by disabling action-log writes.
            _actionLogsTableAvailable = false;
            return false;
        }
        finally
        {
            if (shouldClose && connection.State == ConnectionState.Open)
            {
                await connection.CloseAsync();
            }
        }
    }
}
