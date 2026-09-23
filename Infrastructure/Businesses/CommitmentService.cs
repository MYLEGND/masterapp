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
    private readonly Guid? _businessId;
    private IQueryable<Commitment> Items => _businessId.HasValue
        ? _db.Commitments.Where(x => x.PromisedByType == ActionOwnerType.Business && x.PromisedById == _businessId.Value.ToString() &&
            x.RelatedEntityType == RelatedEntityType.BusinessContact &&
            _db.WorkstationLeadProfiles.Any(contact => contact.LeadId == x.RelatedEntityId &&
                contact.CommerceBusinessId == _businessId && contact.AgentUserId == ""))
        : _db.Commitments.Where(x => x.PromisedByType != ActionOwnerType.Business);

    private static string Normalize(string? value) => IdentityKey.Normalize(value);

    public CommitmentService(MasterAppDbContext db, IExecutionEngine execution, Guid? businessId = null)
    {
        _db = db;
        if (businessId == Guid.Empty) throw new ArgumentException("A business owner is required.");
        _businessId = businessId;
        _execution = execution;
    }

    public async Task<Commitment> CreateCommitmentAsync(CommitmentCreateRequest request, CancellationToken ct = default)
    {
        if (_businessId.HasValue)
        {
            if (request.PromisedByType != ActionOwnerType.Business || request.PromisedById != _businessId.Value.ToString() ||
                request.RelatedEntityType != RelatedEntityType.BusinessContact ||
                !await _db.WorkstationLeadProfiles.AnyAsync(x => x.LeadId == request.RelatedEntityId &&
                    x.CommerceBusinessId == _businessId && x.AgentUserId == "", ct))
                throw new ArgumentException("The commitment must belong to this business contact.");
        }
        else if (request.PromisedByType == ActionOwnerType.Business)
            throw new ArgumentException("Business commitments require a business scope.");
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
                EffectiveAgentOid = _businessId.HasValue ? "" : request.PromisedById,
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

        var c = await Items.FirstOrDefaultAsync(x =>
            x.Id == id &&
            (
                _businessId.HasValue || (x.PromisedById ?? string.Empty).ToLower() == actorKey ||
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

        var c = await Items.FirstOrDefaultAsync(x =>
            x.Id == id &&
            (
                _businessId.HasValue || (x.PromisedById ?? string.Empty).ToLower() == actorKey ||
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

        return await Items.AsNoTracking()
            .FirstOrDefaultAsync(x =>
                x.Id == id &&
                (
                    _businessId.HasValue || (x.PromisedById ?? string.Empty).ToLower() == actorKey ||
                    (x.CreatedBy ?? string.Empty).ToLower() == actorKey
                ), ct);
    }

    public async Task<IReadOnlyList<Commitment>> GetByEntityAsync(RelatedEntityType entityType, string entityId, CancellationToken ct = default)
    {
        var list = await Items.AsNoTracking()
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

        var list = await Items.AsNoTracking()
            .Where(c =>
                c.RelatedEntityType == entityType &&
                c.RelatedEntityId == entityId &&
                (
                    _businessId.HasValue || (c.PromisedById ?? string.Empty).ToLower() == actorKey ||
                    (c.CreatedBy ?? string.Empty).ToLower() == actorKey
                ))
            .OrderBy(c => c.DueDateUtc)
            .ToListAsync(ct);
        return list;
    }
}
