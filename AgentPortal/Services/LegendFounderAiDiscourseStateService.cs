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
    private readonly ILegendConnectOperations? _operations;

    public LegendFounderAiDiscourseStateService(
        MasterAppDbContext db,
        AgentProfileAccessResolver profiles,
        ILegendConnectOperations? operations = null)
    {
        _db = db;
        _profiles = profiles;
        _operations = operations;
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
        var priorTurns = await _db.LegendFounderAiDiscourseTurns
            .Where(item => item.DiscourseConversationId == conversation.Id)
            .OrderBy(item => item.SequenceNumber)
            .ToListAsync(cancellationToken);
        var bindings = await ResolveReferenceBindingsAsync(
            role,
            meaning,
            priorTurns,
            cancellationToken);
        _db.LegendFounderAiDiscourseTurns.Add(new LegendFounderAiDiscourseTurn
        {
            Id = Guid.NewGuid(),
            DiscourseConversationId = conversation.Id,
            SequenceNumber = conversation.NextTurnSequence,
            Role = role,
            MeaningGraphJson = JsonSerializer.Serialize(ToPersistedMeaningGraph(meaning)),
            ResolvedBindingsJson = JsonSerializer.Serialize(bindings),
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

    internal async Task<IReadOnlyList<LegendFounderAiDiscourseReferenceBinding>> GetLatestBindingsAsync(
        string founderAgentUserId,
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        var latest = await GetTurnsAsync(founderAgentUserId, conversationId, cancellationToken);
        return latest.Count == 0
            ? []
            : DeserializeBindings(latest[^1].ResolvedBindingsJson);
    }

    internal async Task<IReadOnlyList<LegendFounderAiDiscourseReferenceBinding>> GetActiveBindingsAsync(
        string founderAgentUserId,
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        var turns = await GetTurnsAsync(founderAgentUserId, conversationId, cancellationToken);
        var active = new Dictionary<string, LegendFounderAiDiscourseReferenceBinding>(StringComparer.Ordinal);
        foreach (var binding in turns.SelectMany(item => DeserializeBindings(item.ResolvedBindingsJson))
                     .Where(item => item.ResolutionState == "bound"))
        {
            // A later governed binding is the current entity for that semantic
            // dimension; ReplaceActiveBinding records an explicit discourse
            // correction rather than changing resolution behavior by text.
            active[binding.EntitySemanticDimension] = binding;
        }
        return active.Values.ToArray();
    }

    /// <summary>
    /// Returns the conversation's persisted, structural state for an
    /// observational semantic planner.  The state contains governed graph and
    /// binding identities only; it never reads transcript or response text.
    /// </summary>
    internal async Task<LegendConnectDiscourseStateSnapshot?> GetStateAsync(
        ClaimsPrincipal founder,
        string? conversationId,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(conversationId, out var parsedConversationId))
            return null;
        var profile = await _profiles.ResolveCurrentAsync(founder, requireActive: true, cancellationToken);
        var actor = profile?.AgentUserId?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(actor))
            return null;

        var turns = await GetTurnsAsync(actor, parsedConversationId, cancellationToken);
        return new LegendConnectDiscourseStateSnapshot(
            turns.Select(turn =>
            {
                var graph = DeserializeMeaning(turn.MeaningGraphJson);
                return new LegendConnectDiscourseTurnStateSnapshot(
                    turn.SequenceNumber,
                    turn.Role,
                    graph.IsComposed,
                    graph.Nodes,
                    graph.Relations,
                    DeserializeBindings(turn.ResolvedBindingsJson)
                        .Select(binding => new LegendConnectDiscourseReferenceBindingSnapshot(
                            binding.ResolutionState,
                            binding.ReasonCode,
                            binding.EntitySemanticDimension,
                            binding.EntitySemanticSignature,
                            binding.EntitySemanticValue,
                            binding.EntityTurnSequence,
                            binding.EntityNodeIndex,
                            binding.ReplacesActiveBinding))
                        .ToArray());
            }).ToArray());
    }

    private async Task<IReadOnlyList<LegendFounderAiDiscourseReferenceBinding>> ResolveReferenceBindingsAsync(
        string role,
        LegendConnectUtteranceMeaningGraphSnapshot meaning,
        IReadOnlyList<LegendFounderAiDiscourseTurn> priorTurns,
        CancellationToken cancellationToken)
    {
        if (_operations is null || !meaning.IsComposed || meaning.Nodes.Count == 0)
            return [];

        var selectorSignatures = meaning.Nodes.Select(item => item.SemanticSignature)
            .Distinct(StringComparer.Ordinal).ToArray();
        var rules = await _operations.GetProductionDiscourseReferenceRulesAsync(
            "en", selectorSignatures, cancellationToken);
        if (rules.Count == 0)
            return [];

        var results = new List<LegendFounderAiDiscourseReferenceBinding>();
        foreach (var selector in meaning.Nodes.OrderBy(item => item.StartTokenIndex).ThenBy(item => item.SemanticSignature))
        {
            var matching = rules.Where(item => item.SelectorSemanticSignature == selector.SemanticSignature)
                .OrderBy(item => item.EntitySemanticDimension, StringComparer.Ordinal)
                .ThenBy(item => item.ResolutionMode, StringComparer.Ordinal)
                .ThenBy(item => item.SelectionRank)
                .ToArray();
            if (matching.Length == 0)
                continue;
            if (matching.Length > 1)
            {
                results.Add(Unresolved(selector, null, "reference_rule_ambiguous"));
                continue;
            }

            var rule = matching[0];
            var candidates = priorTurns
                .Where(item => rule.AllowedSourceRoles.Contains(item.Role, StringComparer.Ordinal))
                .OrderBy(item => item.SequenceNumber)
                .SelectMany(item => DeserializeMeaning(item.MeaningGraphJson).Nodes
                    .Select((node, nodeIndex) => new DiscourseEntityCandidate(item, node, nodeIndex)))
                .Where(item => string.Equals(item.Node.SemanticDimension, rule.EntitySemanticDimension,
                    StringComparison.Ordinal))
                .OrderBy(item => item.Turn.SequenceNumber)
                .ThenBy(item => item.Node.StartTokenIndex)
                .ThenBy(item => item.NodeIndex)
                .ToArray();

            DiscourseEntityCandidate? selected = rule.ResolutionMode switch
            {
                "ordinal" when rule.SelectionRank is > 0 && candidates.Length >= rule.SelectionRank.Value =>
                    candidates[rule.SelectionRank.Value - 1],
                "unique" when candidates.Length == 1 => candidates[0],
                _ => null
            };
            if (selected is null)
            {
                var reason = candidates.Length == 0 ? "reference_candidate_missing" :
                    rule.ResolutionMode == "unique" ? "reference_candidate_ambiguous" :
                    "reference_candidate_rank_unavailable";
                results.Add(Unresolved(selector, rule, reason));
                continue;
            }

            results.Add(new LegendFounderAiDiscourseReferenceBinding(
                "bound",
                "governed_reference_resolved",
                selector.SemanticSignature,
                rule.EntitySemanticDimension,
                selected.Node.SemanticSignature,
                selected.Node.SemanticValue,
                selected.Turn.Id,
                selected.Turn.SequenceNumber,
                selected.NodeIndex,
                rule.ReplacesActiveBinding));
        }
        return results;
    }

    private static LegendFounderAiDiscourseReferenceBinding Unresolved(
        LegendConnectUtteranceMeaningNode selector,
        LegendConnectDiscourseReferenceRuleSnapshot? rule,
        string reason) =>
        new(
            "unresolved",
            reason,
            selector.SemanticSignature,
            rule?.EntitySemanticDimension ?? string.Empty,
            null,
            null,
            null,
            null,
            null,
            rule?.ReplacesActiveBinding ?? false);

    private static LegendConnectUtteranceMeaningGraphSnapshot DeserializeMeaning(string json)
    {
        try
        {
            var persisted = JsonSerializer.Deserialize<PersistedMeaningGraph>(json);
            return persisted is null
                ? new LegendConnectUtteranceMeaningGraphSnapshot(false, [], [], [], "meaning_graph_state_invalid")
                : new LegendConnectUtteranceMeaningGraphSnapshot(
                    persisted.IsComposed,
                    persisted.Nodes,
                    persisted.Relations,
                    [],
                    persisted.ReasonCode);
        }
        catch (JsonException)
        {
            return new LegendConnectUtteranceMeaningGraphSnapshot(false, [], [], [], "meaning_graph_state_invalid");
        }
    }

    private static IReadOnlyList<LegendFounderAiDiscourseReferenceBinding> DeserializeBindings(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            return JsonSerializer.Deserialize<IReadOnlyList<LegendFounderAiDiscourseReferenceBinding>>(json) ?? [];
        }
        catch (JsonException)
        {
            // A legacy malformed state record cannot become a reference source.
            return [];
        }
    }

    private static PersistedMeaningGraph ToPersistedMeaningGraph(
        LegendConnectUtteranceMeaningGraphSnapshot meaning) =>
        new(
            meaning.IsComposed,
            meaning.Nodes,
            meaning.Relations,
            meaning.ReasonCode);

    private static bool IsRetryableConcurrencyFailure(Exception exception) =>
        exception.GetBaseException() is SqlException sqlException &&
        sqlException.Number is 1205 or 2601 or 2627;

    private sealed record DiscourseEntityCandidate(
        LegendFounderAiDiscourseTurn Turn,
        LegendConnectUtteranceMeaningNode Node,
        int NodeIndex);

    private sealed record PersistedMeaningGraph(
        bool IsComposed,
        IReadOnlyList<LegendConnectUtteranceMeaningNode> Nodes,
        IReadOnlyList<LegendConnectUtteranceMeaningRelation> Relations,
        string ReasonCode);
}

/// <summary>
/// Persisted semantic-reference outcome. All identifiers are governed graph
/// identities or opaque turn coordinates; no sentence surface is retained.
/// </summary>
internal sealed record LegendFounderAiDiscourseReferenceBinding(
    string ResolutionState,
    string ReasonCode,
    string SelectorSemanticSignature,
    string EntitySemanticDimension,
    string? EntitySemanticSignature,
    string? EntitySemanticValue,
    Guid? EntityTurnId,
    int? EntityTurnSequence,
    int? EntityNodeIndex,
    bool ReplacesActiveBinding);
