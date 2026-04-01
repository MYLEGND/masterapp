using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Domain.Enums;

namespace AgentPortal.Services;

public record CommitmentCreateRequest(
    RelatedEntityType RelatedEntityType,
    string RelatedEntityId,
    ActionOwnerType PromisedByType,
    string PromisedById,
    ActionOwnerType PromisedToType,
    string PromisedToId,
    string PromiseText,
    DateTimeOffset DueDateUtc,
    string CreatedBy,
    bool CreateLinkedAction = true);

public interface ICommitmentService
{
    Task<Commitment> CreateCommitmentAsync(CommitmentCreateRequest request, CancellationToken ct = default);
    Task<Commitment?> FulfillCommitmentAsync(Guid id, string actorId, CancellationToken ct = default);
    Task<Commitment?> BreakCommitmentAsync(Guid id, string actorId, CancellationToken ct = default);
    Task<IReadOnlyList<Commitment>> GetByEntityAsync(RelatedEntityType entityType, string entityId, CancellationToken ct = default);
}
