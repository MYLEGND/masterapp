using System.Security.Claims;
using System.Text.Json;
using AgentPortal.Models;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace AgentPortal.Services;

/// <summary>
/// The one conversation-scoped persistence authority for Founder LEGEND AI
/// meaning observations. It deliberately stores structural analysis only,
/// never raw prompts, response text, provider output, or a derived answer.
/// Curriculum authority remains in Legend Connect; this service neither
/// learns globally nor selects a response.
/// </summary>
public sealed class LegendFounderAiDiscourseStateService
{
    private const int MaximumTurnsPerConversation = 48;
    private const int MaximumConcurrentWriteAttempts = 3;
    private readonly MasterAppDbContext _db;
    private readonly AgentProfileAccessResolver _profiles;

    public LegendFounderAiDiscourseStateService(
        MasterAppDbContext db,
        AgentProfileAccessResolver profiles)
    {
        _db = db;
        _profiles = profiles;
    }

    public async Task RecordObservationAsync(
        ClaimsPrincipal founder,
        string? conversationId,
        string role,
        LegendConnectUtteranceMeaningGraphSnapshot meaning,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(meaning);

        if (!Guid.TryParse(conversationId, out var parsedConversationId) ||
            role is not ("user" or "assistant"))
        {
            return;
        }

        var profile = await _profiles.ResolveCurrentAsync(
            founder,
            requireActive: true,
            cancellationToken);
        var actor = profile?.AgentUserId?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(actor))
            return;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await RecordObservationOnceAsync(
                    actor,
                    parsedConversationId,
                    role,
                    meaning,
                    cancellationToken);
                return;
            }
            catch (Exception exception)
                when (attempt < MaximumConcurrentWriteAttempts && IsRetryableConcurrencyFailure(exception))
            {
                _db.ChangeTracker.Clear();
                await Task.Delay(TimeSpan.FromMilliseconds(25 * attempt), cancellationToken);
            }
        }
    }

    private async Task RecordObservationOnceAsync(
        string actor,
        Guid parsedConversationId,
        string role,
        LegendConnectUtteranceMeaningGraphSnapshot meaning,
        CancellationToken cancellationToken)
    {
        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken)
            : null;
        var conversation = await _db.LegendFounderAiDiscourseConversations
            .SingleOrDefaultAsync(item => item.FounderAgentUserId == actor &&
                item.ConversationId == parsedConversationId, cancellationToken);
        if (conversation is null)
        {
            conversation = new LegendFounderAiDiscourseConversation
            {
                Id = Guid.NewGuid(),
                FounderAgentUserId = actor,
                ConversationId = parsedConversationId,
                NextTurnSequence = 0,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            };
            _db.LegendFounderAiDiscourseConversations.Add(conversation);
        }

        conversation.NextTurnSequence++;
        conversation.UpdatedUtc = DateTime.UtcNow;
        _db.LegendFounderAiDiscourseTurns.Add(new LegendFounderAiDiscourseTurn
        {
            Id = Guid.NewGuid(),
            DiscourseConversationId = conversation.Id,
            SequenceNumber = conversation.NextTurnSequence,
            Role = role,
            MeaningGraphJson = JsonSerializer.Serialize(WithoutRawSurfaceComponents(meaning)),
            AnalysisReasonCode = meaning.ReasonCode,
            CreatedUtc = DateTime.UtcNow
        });
        await _db.SaveChangesAsync(cancellationToken);

        var stale = await _db.LegendFounderAiDiscourseTurns
            .Where(item => item.DiscourseConversationId == conversation.Id)
            .OrderByDescending(item => item.SequenceNumber)
            .Skip(MaximumTurnsPerConversation)
            .Select(item => item.Id)
            .ToArrayAsync(cancellationToken);
        if (stale.Length > 0)
        {
            if (_db.Database.IsRelational())
            {
                await _db.LegendFounderAiDiscourseTurns
                    .Where(item => stale.Contains(item.Id))
                    .ExecuteDeleteAsync(cancellationToken);
            }
            else
            {
                var staleEntities = await _db.LegendFounderAiDiscourseTurns
                    .Where(item => stale.Contains(item.Id))
                    .ToListAsync(cancellationToken);
                _db.LegendFounderAiDiscourseTurns.RemoveRange(staleEntities);
                await _db.SaveChangesAsync(cancellationToken);
            }
        }

        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
    }

    internal async Task<IReadOnlyList<LegendFounderAiDiscourseTurn>> GetTurnsAsync(
        string founderAgentUserId,
        Guid conversationId,
        CancellationToken cancellationToken = default) =>
        await _db.LegendFounderAiDiscourseConversations.AsNoTracking()
            .Where(item => item.FounderAgentUserId == founderAgentUserId && item.ConversationId == conversationId)
            .Join(_db.LegendFounderAiDiscourseTurns.AsNoTracking(),
                conversation => conversation.Id,
                turn => turn.DiscourseConversationId,
                (_, turn) => turn)
            .OrderBy(item => item.SequenceNumber)
            .ToListAsync(cancellationToken);

    private static LegendConnectUtteranceMeaningGraphSnapshot WithoutRawSurfaceComponents(
        LegendConnectUtteranceMeaningGraphSnapshot meaning) =>
        new(
            meaning.IsComposed,
            meaning.Nodes,
            meaning.Relations,
            [],
            meaning.ReasonCode);

    private static bool IsRetryableConcurrencyFailure(Exception exception) =>
        exception.GetBaseException() is SqlException sqlException &&
        sqlException.Number is 1205 or 2601 or 2627;
}
