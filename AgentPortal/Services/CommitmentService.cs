using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Shared.Auth;

namespace AgentPortal.Services;

public class CommitmentService : ICommitmentService
{
    private readonly MasterAppDbContext _db;
    private readonly IExecutionEngine _execution;
    private static string Normalize(string? value) => IdentityKey.Normalize(value);

    public CommitmentService(MasterAppDbContext db, IExecutionEngine execution)
    {
        _db = db;
        _execution = execution;
    }

    public async Task<Commitment> CreateCommitmentAsync(CommitmentCreateRequest request, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;

        var commitment = new Commitment
        {
            Id = Guid.NewGuid(),
            RelatedEntityType = request.RelatedEntityType,
            RelatedEntityId = request.RelatedEntityId,
            PromisedByType = request.PromisedByType,
            PromisedById = request.PromisedById,
            PromisedToType = request.PromisedToType,
            PromisedToId = request.PromisedToId,
            PromiseText = request.PromiseText,
            DueDateUtc = request.DueDateUtc,
            Status = CommitmentStatus.Open,
            LinkedActionId = null,
            CreatedBy = request.CreatedBy,
            CreatedUtc = now,
            FulfilledAtUtc = null
        };

        // Save commitment first, then create/link the action atomically.
        // This prevents "action-only" saves when commitments are unavailable/misconfigured.
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        _db.Commitments.Add(commitment);
        await _db.SaveChangesAsync(ct);

        if (request.CreateLinkedAction)
        {
            var action = new ActionItem
            {
                RelatedEntityType = request.RelatedEntityType,
                RelatedEntityId = request.RelatedEntityId,
                Title = request.PromiseText,
                Description = "",
                OwnerType = request.PromisedByType,
                OwnerId = request.PromisedById,
                EffectiveAgentOid = request.PromisedById,
                DueDateUtc = request.DueDateUtc.UtcDateTime,
                Status = ActionStatus.Planned,
                Priority = ActionPriority.P2,
                ActionSurface = ActionSurface.CommandCenter,
                Source = "commitment",
                SourceRef = $"commitment-{commitment.Id}",
                CreatedBy = request.CreatedBy,
                CreatedUtc = now.UtcDateTime
            };

            var createdAction = await _execution.CreateActionAsync(action, ct);
            commitment.LinkedActionId = createdAction.Id;
            await _db.SaveChangesAsync(ct);
        }

        await tx.CommitAsync(ct);
        return commitment;
    }

    public async Task<Commitment?> FulfillCommitmentAsync(Guid id, string actorId, CancellationToken ct = default)
    {
        var actorKey = Normalize(actorId);
        if (string.IsNullOrWhiteSpace(actorKey)) return null;

        var c = await _db.Commitments.FirstOrDefaultAsync(x =>
            x.Id == id &&
            (
                (x.PromisedById ?? string.Empty).ToLower() == actorKey ||
                (x.CreatedBy ?? string.Empty).ToLower() == actorKey
            ), ct);
        if (c == null) return null;
        c.Status = CommitmentStatus.Fulfilled;
        c.FulfilledAtUtc = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        if (c.LinkedActionId.HasValue)
        {
            await _execution.CompleteActionAsync(c.LinkedActionId.Value, actorId, ct);
        }
        return c;
    }

    public async Task<Commitment?> BreakCommitmentAsync(Guid id, string actorId, CancellationToken ct = default)
    {
        var actorKey = Normalize(actorId);
        if (string.IsNullOrWhiteSpace(actorKey)) return null;

        var c = await _db.Commitments.FirstOrDefaultAsync(x =>
            x.Id == id &&
            (
                (x.PromisedById ?? string.Empty).ToLower() == actorKey ||
                (x.CreatedBy ?? string.Empty).ToLower() == actorKey
            ), ct);
        if (c == null) return null;
        c.Status = CommitmentStatus.Broken;
        await _db.SaveChangesAsync(ct);
        return c;
    }

    public async Task<Commitment?> GetByIdForActorAsync(Guid id, string actorId, CancellationToken ct = default)
    {
        var actorKey = Normalize(actorId);
        if (string.IsNullOrWhiteSpace(actorKey)) return null;

        return await _db.Commitments.AsNoTracking()
            .FirstOrDefaultAsync(x =>
                x.Id == id &&
                (
                    (x.PromisedById ?? string.Empty).ToLower() == actorKey ||
                    (x.CreatedBy ?? string.Empty).ToLower() == actorKey
                ), ct);
    }

    public async Task<IReadOnlyList<Commitment>> GetByEntityAsync(RelatedEntityType entityType, string entityId, CancellationToken ct = default)
    {
        var list = await _db.Commitments.AsNoTracking()
            .Where(c => c.RelatedEntityType == entityType && c.RelatedEntityId == entityId)
            .OrderBy(c => c.DueDateUtc)
            .ToListAsync(ct);
        return list;
    }

    public async Task<IReadOnlyList<Commitment>> GetByEntityForActorAsync(RelatedEntityType entityType, string entityId, string actorId, CancellationToken ct = default)
    {
        var actorKey = Normalize(actorId);
        if (string.IsNullOrWhiteSpace(actorKey))
        {
            return Array.Empty<Commitment>();
        }

        var list = await _db.Commitments.AsNoTracking()
            .Where(c =>
                c.RelatedEntityType == entityType &&
                c.RelatedEntityId == entityId &&
                (
                    (c.PromisedById ?? string.Empty).ToLower() == actorKey ||
                    (c.CreatedBy ?? string.Empty).ToLower() == actorKey
                ))
            .OrderBy(c => c.DueDateUtc)
            .ToListAsync(ct);
        return list;
    }
}
