using Domain.Entities;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Shared.Auth;
using System.Data;

namespace AgentPortal.Services;

    public class ExecutionEngine : IExecutionEngine
    {
        private readonly MasterAppDbContext _db;
        private bool? _actionLogsTableAvailable;
        private readonly Guid? _businessId;
        private IQueryable<ActionItem> Items => _businessId.HasValue
            ? _db.ActionItems.Where(x => x.OwnerType == ActionOwnerType.Business && x.OwnerId == _businessId.Value.ToString() &&
                x.RelatedEntityType == RelatedEntityType.BusinessContact &&
                _db.WorkstationLeadProfiles.Any(contact => contact.LeadId == x.RelatedEntityId &&
                    contact.CommerceBusinessId == _businessId && contact.AgentUserId == ""))
            : _db.ActionItems.Where(x => x.OwnerType != ActionOwnerType.Business);


        public ExecutionEngine(MasterAppDbContext db, Guid? businessId = null)
    {
        _db = db;
        if (businessId == Guid.Empty) throw new ArgumentException("A business owner is required.");
        _businessId = businessId;
    }

    public async Task<ActionItem> CreateActionAsync(ActionItem action, CancellationToken ct = default)
    {
        if (_businessId.HasValue)
        {
            if (action.OwnerType != ActionOwnerType.Business || action.OwnerId != _businessId.Value.ToString() ||
                action.RelatedEntityType != RelatedEntityType.BusinessContact || !string.IsNullOrEmpty(action.EffectiveAgentOid) ||
                !await _db.WorkstationLeadProfiles.AnyAsync(x => x.LeadId == action.RelatedEntityId &&
                    x.CommerceBusinessId == _businessId && x.AgentUserId == "", ct))
                throw new ArgumentException("The action must belong to this business contact.");
        }
        else if (action.OwnerType == ActionOwnerType.Business)
            throw new ArgumentException("Business actions require a business scope.");
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
        var action = await Items.FirstOrDefaultAsync(x => x.Id == actionId, ct);
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
        var action = await Items.FirstOrDefaultAsync(x => x.Id == actionId, ct);
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
        var action = await Items.FirstOrDefaultAsync(x => x.Id == actionId, ct);
        if (action == null) return null;
        if (_businessId.HasValue && (newOwnerType != ActionOwnerType.Business || newOwnerId != _businessId.Value.ToString()))
            throw new ArgumentException("An action cannot move outside its business scope.");
        if (!_businessId.HasValue && newOwnerType == ActionOwnerType.Business)
            throw new ArgumentException("Business actions require a business scope.");
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
        return Items
            .AsNoTracking()
            .Where(x => (_businessId.HasValue || x.OwnerId == normalizedOwnerId || x.EffectiveAgentOid == normalizedOwnerId)
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
        return Items
            .AsNoTracking()
            .Where(x => (_businessId.HasValue || x.OwnerId == normalizedOwnerId || x.EffectiveAgentOid == normalizedOwnerId)
                        && (x.ActionSurface == ActionSurface.CommandCenter || x.IsEscalated)
                        && x.DueDateUtc < nowUtc
                        && x.Status != ActionStatus.Completed && x.Status != ActionStatus.Dismissed)
            .OrderBy(x => x.DueDateUtc)
            .ToListAsync(ct)
            .ContinueWith(t => (IReadOnlyList<ActionItem>)t.Result, ct);
    }

    private static string NormalizeOwnerKey(string ownerId)
        => IdentityKey.Normalize(ownerId);

    private bool MatchesActor(ActionItem action, string actorId)
    {
        var actorKey = NormalizeOwnerKey(actorId);
        if (string.IsNullOrWhiteSpace(actorKey)) return false;
        if (_businessId.HasValue) return action.OwnerType == ActionOwnerType.Business && action.OwnerId == _businessId.Value.ToString();
        return string.Equals(NormalizeOwnerKey(action.OwnerId), actorKey, StringComparison.Ordinal)
            || string.Equals(NormalizeOwnerKey(action.EffectiveAgentOid), actorKey, StringComparison.Ordinal);
    }

    public Task<IReadOnlyList<ActionItem>> GetByRelatedAsync(RelatedEntityType relatedEntityType, string relatedEntityId, string actorId, CancellationToken ct = default)
    {
        var actorKey = NormalizeOwnerKey(actorId);
        if (string.IsNullOrWhiteSpace(actorKey))
        {
            return Task.FromResult<IReadOnlyList<ActionItem>>(Array.Empty<ActionItem>());
        }

        return Items
            .AsNoTracking()
            .Where(x =>
                x.RelatedEntityType == relatedEntityType &&
                x.RelatedEntityId == relatedEntityId &&
                x.Status != ActionStatus.Dismissed &&
                (_businessId.HasValue || (x.OwnerId ?? string.Empty).ToLower() == actorKey ||
                 (x.EffectiveAgentOid ?? string.Empty).ToLower() == actorKey))
            .OrderByDescending(x => x.CreatedUtc)
            .ToListAsync(ct)
            .ContinueWith(t => (IReadOnlyList<ActionItem>)t.Result, ct);
    }

    public Task<ActionItem?> GetByIdAsync(Guid id, string actorId, CancellationToken ct = default)
    {
        var actorKey = NormalizeOwnerKey(actorId);
        if (string.IsNullOrWhiteSpace(actorKey))
        {
            return Task.FromResult<ActionItem?>(null);
        }

        return Items.AsNoTracking().FirstOrDefaultAsync(x =>
            x.Id == id &&
            (_businessId.HasValue || (x.OwnerId ?? string.Empty).ToLower() == actorKey ||
             (x.EffectiveAgentOid ?? string.Empty).ToLower() == actorKey), ct);
    }

    public async Task<ActionItem?> UpdateActionAsync(Guid id, string actorId, string title, string? description, DateTime? dueDateUtc, ActionPriority priority, CancellationToken ct = default)
    {
        var actorKey = NormalizeOwnerKey(actorId);
        if (string.IsNullOrWhiteSpace(actorKey)) return null;

        var action = await Items.FirstOrDefaultAsync(x =>
            x.Id == id &&
            (_businessId.HasValue || (x.OwnerId ?? string.Empty).ToLower() == actorKey ||
             (x.EffectiveAgentOid ?? string.Empty).ToLower() == actorKey), ct);
        if (action == null) return null;

        action.Title = title.Trim();
        action.Description = description?.Trim() ?? string.Empty;
        action.DueDateUtc = dueDateUtc;
        action.Priority = priority;
        action.UpdatedUtc = DateTime.UtcNow;

        await TryAddActionLogAsync(new ActionLog
        {
            ActionId = id,
            ActorId = actorId,
            Verb = "updated",
            PayloadJson = $"{title}|{priority}|{dueDateUtc}",
            OccurredUtc = action.UpdatedUtc.Value
        }, ct);

        await _db.SaveChangesAsync(ct);
        return action;
    }

    public async Task<bool> DeleteActionAsync(Guid id, string actorId, CancellationToken ct = default)
    {
        var actorKey = NormalizeOwnerKey(actorId);
        if (string.IsNullOrWhiteSpace(actorKey)) return false;

        var action = await Items.FirstOrDefaultAsync(x =>
            x.Id == id &&
            (_businessId.HasValue || (x.OwnerId ?? string.Empty).ToLower() == actorKey ||
             (x.EffectiveAgentOid ?? string.Empty).ToLower() == actorKey), ct);
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
