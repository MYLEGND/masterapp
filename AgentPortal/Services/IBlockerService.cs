using Domain.Entities;

namespace AgentPortal.Services;

public interface IBlockerService
{
    Task<Blocker> OpenAsync(Blocker blocker, CancellationToken ct = default);
    Task<Blocker?> ResolveAsync(Guid blockerId, string? notes, CancellationToken ct = default);
    Task<IReadOnlyList<Blocker>> GetOpenByEntityAsync(string relatedEntityId, string relatedEntityType, CancellationToken ct = default);
    Task<IReadOnlyList<Blocker>> GetOpenByOwnerAsync(string ownerId, CancellationToken ct = default);
}
