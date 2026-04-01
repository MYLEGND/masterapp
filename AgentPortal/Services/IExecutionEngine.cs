using Domain.Entities;
using Domain.Enums;

namespace AgentPortal.Services;

public interface IExecutionEngine
{
    Task<ActionItem> CreateActionAsync(ActionItem action, CancellationToken ct = default);
    Task<ActionItem?> CompleteActionAsync(Guid actionId, string actorId, CancellationToken ct = default);
    Task<ActionItem?> DismissActionAsync(Guid actionId, string actorId, string reason, CancellationToken ct = default);
    Task<ActionItem?> ReassignAsync(Guid actionId, ActionOwnerType newOwnerType, string newOwnerId, CancellationToken ct = default);
    Task<IReadOnlyList<ActionItem>> GetTodayAsync(string ownerId, CancellationToken ct = default);
    Task<IReadOnlyList<ActionItem>> GetOverdueAsync(string ownerId, CancellationToken ct = default);
    Task<IReadOnlyList<ActionItem>> GetByRelatedAsync(RelatedEntityType relatedEntityType, string relatedEntityId, CancellationToken ct = default);
    Task<ActionItem?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<ActionItem?> UpdateActionAsync(Guid id, string title, string? description, DateTime? dueDateUtc, ActionPriority priority, CancellationToken ct = default);
    Task<bool> DeleteActionAsync(Guid id, CancellationToken ct = default);
}
