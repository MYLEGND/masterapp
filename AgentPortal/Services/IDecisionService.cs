using Domain.Entities;

namespace AgentPortal.Services;

public interface IDecisionService
{
    Task<DecisionRecord> CreateDecisionAsync(DecisionRecord decision, CancellationToken ct = default);
    Task<IReadOnlyList<DecisionRecord>> GetByEntityAsync(string relatedEntityId, string relatedEntityType, CancellationToken ct = default);
    Task<DecisionRecord?> GetLatestByEntityAsync(string relatedEntityId, string relatedEntityType, CancellationToken ct = default);
}
