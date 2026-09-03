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
    private readonly ILegendConnectOperations _operations;

    public LegendFounderAiDiscourseStateService(
        MasterAppDbContext db,
        AgentProfileAccessResolver profiles,
        ILegendConnectOperations operations)
    {
        _db = db;
        _profiles = profiles;
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
    }

    public async Task RecordObservationAsync(
        ClaimsPrincipal founder,
        string? conversationId,
        string role,
        LegendConnectUtteranceMeaningGraphSnapshot meaning,
        CancellationToken cancellationToken = default,
        string sourceLanguageCode = "en")
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
                    sourceLanguageCode,
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
        string sourceLanguageCode,
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
        var turnId = Guid.NewGuid();
        var turnSequence = conversation.NextTurnSequence;
        var priorTurns = await _db.LegendFounderAiDiscourseTurns
            .Where(item => item.DiscourseConversationId == conversation.Id)
            .OrderBy(item => item.SequenceNumber)
            .ToListAsync(cancellationToken);
        var bindings = await ResolveReferenceBindingsAsync(
            role,
            meaning,
            priorTurns,
            turnId,
            turnSequence,
            sourceLanguageCode,
            cancellationToken);
        _db.LegendFounderAiDiscourseTurns.Add(new LegendFounderAiDiscourseTurn
        {
            Id = turnId,
            DiscourseConversationId = conversation.Id,
            SequenceNumber = turnSequence,
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
        if (latest.Count == 0)
            return [];
        var validated = await LoadValidatedBindingsAsync(latest, cancellationToken);
        return validated[latest[^1].Id];
    }

    internal async Task<IReadOnlyList<LegendFounderAiDiscourseReferenceBinding>> GetActiveBindingsAsync(
        string founderAgentUserId,
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        var turns = await GetTurnsAsync(founderAgentUserId, conversationId, cancellationToken);
        var validated = await LoadValidatedBindingsAsync(turns, cancellationToken);
        var entries = BindingEntries(turns, validated);
        return entries
            .Where(item =>
                item.Binding.ResolutionState == "bound" ||
                IsReplacementAttempt(item.Binding))
            .Select(item => item.Binding.EntitySemanticDimension)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.Ordinal)
            .Select(dimension => ResolveActiveBinding(entries, dimension).Binding)
            .Where(item => item is not null)
            .Select(item => item!)
            .ToArray();
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
        var validated = await LoadValidatedBindingsAsync(turns, cancellationToken);
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
                    validated[turn.Id]
                        .Select(binding => new LegendConnectDiscourseReferenceBindingSnapshot(
                            binding.ResolutionState,
                            binding.ReasonCode,
                            binding.EntitySemanticDimension,
                            binding.EntitySemanticSignature,
                            binding.EntitySemanticValue,
                            binding.EntityTurnSequence,
                            binding.EntityNodeIndex,
                            binding.ReplacesActiveBinding,
                            binding.SelectorSemanticSignature,
                            binding.ReferenceRuleSignature)
                        {
                            SupersededTurnId = binding.SupersededTurnId,
                            SupersededTurnSequence = binding.SupersededTurnSequence,
                            SupersededNodeIndex = binding.SupersededNodeIndex,
                            SupersededEntitySemanticSignature = binding.SupersededEntitySemanticSignature,
                            SupersededEntitySemanticDimension = binding.SupersededEntitySemanticDimension,
                            SupersededEntitySemanticValue = binding.SupersededEntitySemanticValue,
                            SupersededNodeStartTokenIndex = binding.SupersededNodeStartTokenIndex,
                            SupersededNodeTokenLength = binding.SupersededNodeTokenLength,
                            SelectorTurnId = binding.SelectorTurnId,
                            SelectorTurnSequence = binding.SelectorTurnSequence,
                            SelectorNodeIndex = binding.SelectorNodeIndex,
                            SelectorNodeStartTokenIndex = binding.SelectorNodeStartTokenIndex,
                            SelectorNodeTokenLength = binding.SelectorNodeTokenLength,
                            RuleLanguageCode = binding.RuleLanguageCode,
                            RuleResolutionMode = binding.RuleResolutionMode,
                            RuleSelectionRank = binding.RuleSelectionRank,
                            RuleAllowedSourceRoles = binding.RuleAllowedSourceRoles,
                            HasSupersededCurrentTurnEntity = binding.HasSupersededCurrentTurnEntity,
                            SupersededCurrentTurnNodeIndex = binding.SupersededCurrentTurnNodeIndex,
                            SupersededCurrentTurnSemanticSignature = binding.SupersededCurrentTurnSemanticSignature,
                            SupersededCurrentTurnSemanticDimension = binding.SupersededCurrentTurnSemanticDimension,
                            SupersededCurrentTurnSemanticValue = binding.SupersededCurrentTurnSemanticValue,
                            SupersededCurrentTurnNodeStartTokenIndex = binding.SupersededCurrentTurnNodeStartTokenIndex,
                            SupersededCurrentTurnNodeTokenLength = binding.SupersededCurrentTurnNodeTokenLength
                        })
                        .ToArray());
            }).ToArray());
    }

    private async Task<IReadOnlyList<LegendFounderAiDiscourseReferenceBinding>> ResolveReferenceBindingsAsync(
        string role,
        LegendConnectUtteranceMeaningGraphSnapshot meaning,
        IReadOnlyList<LegendFounderAiDiscourseTurn> priorTurns,
        Guid turnId,
        int turnSequence,
        string sourceLanguageCode,
        CancellationToken cancellationToken)
    {
        // Reference selectors are allowed to depend on prior governed state;
        // requiring the current surface to be independently composed here is
        // the late-binding defect. Nodes still come only from the curriculum
        // meaning authority, and the completed proposition is validated there
        // before transition selection.
        if (meaning.Nodes.Count == 0)
            return [];

        var selectorSignatures = meaning.Nodes.Select(item => item.SemanticSignature)
            .Distinct(StringComparer.Ordinal).ToArray();
        var rules = await _operations.GetProductionDiscourseReferenceRulesAsync(
            sourceLanguageCode, selectorSignatures, cancellationToken);
        if (rules.Count == 0)
            return [];
        var validatedPriorBindings = await LoadValidatedBindingsAsync(priorTurns, cancellationToken);

        var results = new List<LegendFounderAiDiscourseReferenceBinding>();
        foreach (var selectorOccurrence in meaning.Nodes
                     .Select((node, index) => new { Node = node, Index = index })
                     .OrderBy(item => item.Node.StartTokenIndex)
                     .ThenBy(item => item.Node.SemanticSignature, StringComparer.Ordinal)
                     .ThenBy(item => item.Index))
        {
            var selector = selectorOccurrence.Node;
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
                .SelectMany(ActiveDiscourseEntityCandidates)
                .Where(item => string.Equals(item.Node.SemanticDimension, rule.EntitySemanticDimension,
                    StringComparison.Ordinal))
                .OrderBy(item => item.Turn.SequenceNumber)
                .ThenBy(item => item.Node.StartTokenIndex)
                .ThenBy(item => item.NodeIndex)
                .ToArray();

            var latestTurnCandidates = candidates
                .GroupBy(item => item.Turn.SequenceNumber)
                .OrderByDescending(group => group.Key)
                .Select(group => group
                    .OrderBy(item => item.Node.StartTokenIndex)
                    .ThenBy(item => item.NodeIndex)
                    .ToArray())
                .FirstOrDefault() ?? [];
            var uniqueCandidates = candidates
                .GroupBy(item => new
                {
                    item.Node.SemanticSignature,
                    item.Node.SemanticDimension,
                    item.Node.SemanticValue
                })
                .Select(group => group
                    .OrderByDescending(item => item.Turn.SequenceNumber)
                    .ThenByDescending(item => item.Node.StartTokenIndex)
                    .ThenByDescending(item => item.NodeIndex)
                    .First())
                .ToArray();
            var uniqueLatestTurnCandidates = latestTurnCandidates
                .GroupBy(item => new
                {
                    item.Node.SemanticSignature,
                    item.Node.SemanticDimension,
                    item.Node.SemanticValue
                })
                .Select(group => group.First())
                .ToArray();
            var replacementOrdinalCandidates = candidates
                .OrderBy(item => item.Turn.SequenceNumber)
                .ThenBy(item => item.Node.StartTokenIndex)
                .ThenBy(item => item.NodeIndex)
                .GroupBy(item => new
                {
                    item.Node.SemanticSignature,
                    item.Node.SemanticDimension,
                    item.Node.SemanticValue
                })
                .Select(group => group.First())
                .ToArray();
            var activeBinding = ResolveActiveDiscourseBindingCandidate(
                priorTurns,
                validatedPriorBindings,
                rule);
            if (rule.ReplacesActiveBinding && activeBinding.Candidate is null)
            {
                results.Add(Unresolved(selector, rule, "reference_active_binding_invalid"));
                continue;
            }
            if (rule.ResolutionMode == "unique" &&
                activeBinding.HasBinding && activeBinding.Candidate is null)
            {
                results.Add(Unresolved(selector, rule, "reference_active_binding_invalid"));
                continue;
            }

            DiscourseEntityCandidate? selected = rule.ResolutionMode switch
            {
                "ordinal" when rule.ReplacesActiveBinding &&
                    rule.SelectionRank is > 0 &&
                    replacementOrdinalCandidates.Length >= rule.SelectionRank.Value =>
                    replacementOrdinalCandidates[rule.SelectionRank.Value - 1],
                "ordinal" when rule.SelectionRank is > 0 &&
                    latestTurnCandidates.Length >= rule.SelectionRank.Value =>
                    latestTurnCandidates[rule.SelectionRank.Value - 1],
                "unique" when activeBinding.Candidate is not null => activeBinding.Candidate,
                "unique" when uniqueCandidates.Length == 1 => uniqueCandidates[0],
                "recent" when uniqueLatestTurnCandidates.Length == 1 =>
                    uniqueLatestTurnCandidates[0],
                _ => null
            };
            if (selected is null)
            {
                var reason = candidates.Length == 0 ? "reference_candidate_missing" :
                    rule.ResolutionMode == "unique" ? "reference_candidate_ambiguous" :
                    rule.ResolutionMode == "recent" ? "reference_recent_candidate_ambiguous" :
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
                rule.ReplacesActiveBinding,
                rule.RuleSignature,
                rule.ReplacesActiveBinding ? activeBinding.Candidate!.Turn.Id : null,
                rule.ReplacesActiveBinding ? activeBinding.Candidate!.Turn.SequenceNumber : null,
                rule.ReplacesActiveBinding ? activeBinding.Candidate!.NodeIndex : null,
                rule.ReplacesActiveBinding ? activeBinding.Candidate!.Node.StartTokenIndex : null,
                rule.ReplacesActiveBinding ? activeBinding.Candidate!.Node.TokenLength : null,
                sourceLanguageCode,
                rule.ResolutionMode,
                rule.SelectionRank,
                string.Join("|", rule.AllowedSourceRoles.OrderBy(item => item, StringComparer.Ordinal)),
                rule.ReplacesActiveBinding ? activeBinding.Candidate!.Node.SemanticSignature : null,
                rule.ReplacesActiveBinding ? activeBinding.Candidate!.Node.SemanticDimension : null,
                rule.ReplacesActiveBinding ? activeBinding.Candidate!.Node.SemanticValue : null,
                rule.ReplacesActiveBinding ? turnId : null,
                rule.ReplacesActiveBinding ? turnSequence : null,
                rule.ReplacesActiveBinding ? selectorOccurrence.Index : null,
                rule.ReplacesActiveBinding ? selector.StartTokenIndex : null,
                rule.ReplacesActiveBinding ? selector.TokenLength : null,
                false));
        }
        return results;
    }

    private static ActiveDiscourseBindingResolution ResolveActiveDiscourseBindingCandidate(
        IReadOnlyList<LegendFounderAiDiscourseTurn> priorTurns,
        IReadOnlyDictionary<Guid, IReadOnlyList<LegendFounderAiDiscourseReferenceBinding>> validatedBindings,
        LegendConnectDiscourseReferenceRuleSnapshot rule)
    {
        var active = ResolveActiveBinding(
            BindingEntries(priorTurns, validatedBindings),
            rule.EntitySemanticDimension);
        if (!active.HasBinding)
            return ActiveDiscourseBindingResolution.None;
        if (active.Binding is null)
            return ActiveDiscourseBindingResolution.Invalid;

        if (active.Binding.EntityTurnId is not Guid entityTurnId ||
            active.Binding.EntityTurnSequence is not int entityTurnSequence ||
            active.Binding.EntityNodeIndex is not int entityNodeIndex)
        {
            return ActiveDiscourseBindingResolution.Invalid;
        }

        var sourceTurns = priorTurns.Where(item =>
                item.Id == entityTurnId &&
                item.SequenceNumber == entityTurnSequence)
            .ToArray();
        if (sourceTurns.Length != 1)
            return ActiveDiscourseBindingResolution.Invalid;
        if (!rule.AllowedSourceRoles.Contains(sourceTurns[0].Role, StringComparer.Ordinal))
            return ActiveDiscourseBindingResolution.None;

        var source = ActiveDiscourseEntityCandidates(sourceTurns[0])
            .Where(item => item.NodeIndex == entityNodeIndex &&
                string.Equals(
                    item.Node.SemanticDimension,
                    active.Binding.EntitySemanticDimension,
                    StringComparison.Ordinal) &&
                string.Equals(
                    item.Node.SemanticSignature,
                    active.Binding.EntitySemanticSignature,
                    StringComparison.Ordinal) &&
                string.Equals(
                    item.Node.SemanticValue,
                    active.Binding.EntitySemanticValue,
                    StringComparison.Ordinal))
            .ToArray();
        return source.Length == 1
            ? new(true, source[0])
            : ActiveDiscourseBindingResolution.Invalid;
    }

    private static IEnumerable<DiscourseEntityCandidate> ActiveDiscourseEntityCandidates(
        LegendFounderAiDiscourseTurn turn)
    {
        var graph = DeserializeMeaning(turn.MeaningGraphJson);
        if (!graph.IsComposed || graph.Nodes.Count == 0)
            yield break;

        if (graph.Nodes.Count == 1 && graph.Relations.Count == 0)
        {
            yield return new DiscourseEntityCandidate(turn, graph.Nodes[0], 0);
            yield break;
        }

        var activeIndexes = new HashSet<int>();
        foreach (var relation in graph.Relations)
        {
            if (relation.SourceNodeIndex < 0 || relation.SourceNodeIndex >= graph.Nodes.Count ||
                relation.TargetNodeIndex < 0 || relation.TargetNodeIndex >= graph.Nodes.Count)
            {
                yield break;
            }
            activeIndexes.Add(relation.SourceNodeIndex);
            activeIndexes.Add(relation.TargetNodeIndex);
        }
        foreach (var nodeIndex in activeIndexes.OrderBy(item => item))
            yield return new DiscourseEntityCandidate(turn, graph.Nodes[nodeIndex], nodeIndex);
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
            rule?.ReplacesActiveBinding ?? false,
            null);

    private static LegendConnectUtteranceMeaningGraphSnapshot DeserializeMeaning(string json)
    {
        try
        {
            var persisted = JsonSerializer.Deserialize<PersistedMeaningGraph>(json);
            return persisted is null ||
                persisted.Nodes is null ||
                persisted.Relations is null ||
                persisted.Nodes.Any(item => item is null ||
                    string.IsNullOrWhiteSpace(item.SemanticSignature) ||
                    item.StartTokenIndex < 0 ||
                    item.TokenLength <= 0) ||
                persisted.Relations.Any(item => item is null ||
                    string.IsNullOrWhiteSpace(item.RelationSignature) ||
                    item.SourceNodeIndex < 0 ||
                    item.SourceNodeIndex >= persisted.Nodes.Count ||
                    item.TargetNodeIndex < 0 ||
                    item.TargetNodeIndex >= persisted.Nodes.Count)
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

    private static IReadOnlyList<LegendFounderAiDiscourseReferenceBinding> DeserializeBindings(
        LegendFounderAiDiscourseTurn turn) =>
        DeserializeBindings(turn, DeserializeMeaning(turn.MeaningGraphJson));

    private static IReadOnlyList<LegendFounderAiDiscourseReferenceBinding> DeserializeAndValidateBindings(
        LegendFounderAiDiscourseTurn turn,
        IReadOnlyList<LegendFounderAiDiscourseTurn> availableTurns) =>
        DeserializeAndValidateBindings(turn, DeserializeMeaning(turn.MeaningGraphJson), availableTurns);

    private static IReadOnlyList<LegendFounderAiDiscourseReferenceBinding> DeserializeAndValidateBindings(
        LegendFounderAiDiscourseTurn turn,
        LegendConnectUtteranceMeaningGraphSnapshot graph,
        IReadOnlyList<LegendFounderAiDiscourseTurn> availableTurns) =>
        DeserializeBindings(turn, graph)
            .Select(binding =>
                binding.ResolutionState != "bound" ||
                IsBindingAntecedentValid(binding, turn, availableTurns) &&
                (!binding.ReplacesActiveBinding ||
                 ValidateSupersededEntityIdentity(binding, turn, availableTurns))
                    ? binding
                    : InvalidateBinding(
                        binding,
                        binding.ReplacesActiveBinding &&
                        IsBindingAntecedentValid(binding, turn, availableTurns)
                            ? "reference_replacement_occurrence_invalid"
                            : "reference_antecedent_identity_invalid"))
            .ToArray();

    private async Task<IReadOnlyDictionary<Guid, IReadOnlyList<LegendFounderAiDiscourseReferenceBinding>>>
        LoadValidatedBindingsAsync(
            IReadOnlyList<LegendFounderAiDiscourseTurn> turns,
            CancellationToken cancellationToken)
    {
        var structural = turns.ToDictionary(
            turn => turn.Id,
            turn => DeserializeAndValidateBindings(turn, turns));
        var bound = structural.Values.SelectMany(item => item)
            .Where(item => item.ResolutionState == "bound")
            .ToArray();
        var authoritative = new List<(string LanguageCode, LegendConnectDiscourseReferenceRuleSnapshot Rule)>();
        foreach (var group in bound
                     .Where(item =>
                         !string.IsNullOrWhiteSpace(item.RuleLanguageCode) &&
                         !string.IsNullOrWhiteSpace(item.SelectorSemanticSignature))
                     .GroupBy(item => item.RuleLanguageCode!, StringComparer.Ordinal))
        {
            var rules = await _operations.GetProductionDiscourseReferenceRulesAsync(
                group.Key,
                group.Select(item => item.SelectorSemanticSignature)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                cancellationToken);
            authoritative.AddRange(rules.Select(rule => (group.Key, rule)));
        }

        return structural.ToDictionary(
            item => item.Key,
            item => (IReadOnlyList<LegendFounderAiDiscourseReferenceBinding>)item.Value
                .Select(binding =>
                {
                    if (binding.ResolutionState != "bound")
                        return binding;
                    var matches = authoritative.Where(candidate =>
                            string.Equals(candidate.LanguageCode, binding.RuleLanguageCode, StringComparison.Ordinal) &&
                            string.Equals(candidate.Rule.RuleSignature, binding.ReferenceRuleSignature, StringComparison.Ordinal) &&
                            string.Equals(candidate.Rule.SelectorSemanticSignature, binding.SelectorSemanticSignature, StringComparison.Ordinal) &&
                            string.Equals(candidate.Rule.EntitySemanticDimension, binding.EntitySemanticDimension, StringComparison.Ordinal) &&
                            string.Equals(candidate.Rule.ResolutionMode, binding.RuleResolutionMode, StringComparison.Ordinal) &&
                            candidate.Rule.SelectionRank == binding.RuleSelectionRank &&
                            candidate.Rule.ReplacesActiveBinding == binding.ReplacesActiveBinding &&
                            string.Equals(
                                string.Join("|", candidate.Rule.AllowedSourceRoles.OrderBy(role => role, StringComparer.Ordinal)),
                                binding.RuleAllowedSourceRoles,
                                StringComparison.Ordinal))
                        .ToArray();
                    return matches.Length == 1
                        ? binding
                        : InvalidateBinding(binding, "reference_rule_provenance_invalid");
                })
                .ToArray());
    }

    private static bool IsBindingAntecedentValid(
        LegendFounderAiDiscourseReferenceBinding binding,
        LegendFounderAiDiscourseTurn containingTurn,
        IReadOnlyList<LegendFounderAiDiscourseTurn> availableTurns)
    {
        if (string.IsNullOrWhiteSpace(binding.SelectorSemanticSignature) ||
            string.IsNullOrWhiteSpace(binding.ReferenceRuleSignature) ||
            string.IsNullOrWhiteSpace(binding.EntitySemanticDimension) ||
            string.IsNullOrWhiteSpace(binding.EntitySemanticSignature) ||
            binding.EntityTurnId is not Guid entityTurnId ||
            binding.EntityTurnSequence is not int entityTurnSequence ||
            binding.EntityNodeIndex is not int entityNodeIndex ||
            entityTurnSequence >= containingTurn.SequenceNumber)
        {
            return false;
        }

        var sourceTurns = availableTurns.Where(item =>
                item.Id == entityTurnId &&
                item.SequenceNumber == entityTurnSequence)
            .ToArray();
        if (sourceTurns.Length != 1)
            return false;
        var source = ActiveDiscourseEntityCandidates(sourceTurns[0])
            .Where(item => item.NodeIndex == entityNodeIndex &&
                string.Equals(
                    item.Node.SemanticDimension,
                    binding.EntitySemanticDimension,
                    StringComparison.Ordinal) &&
                string.Equals(
                    item.Node.SemanticSignature,
                    binding.EntitySemanticSignature,
                    StringComparison.Ordinal) &&
                string.Equals(
                    item.Node.SemanticValue,
                    binding.EntitySemanticValue,
                    StringComparison.Ordinal))
            .ToArray();
        return source.Length == 1;
    }

    private static IReadOnlyList<LegendFounderAiDiscourseReferenceBinding> DeserializeBindings(
        LegendFounderAiDiscourseTurn turn,
        LegendConnectUtteranceMeaningGraphSnapshot graph)
    {
        var json = turn.ResolvedBindingsJson;
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            var bindings =
                JsonSerializer.Deserialize<IReadOnlyList<LegendFounderAiDiscourseReferenceBinding>>(json);
            if (bindings is null)
                return [MalformedBindingState()];
            if (bindings.Any(item => item is null))
                return [MalformedBindingState()];
            var conflictingOccurrences = bindings
                .Where(item => item.ResolutionState == "bound" && item.ReplacesActiveBinding)
                .GroupBy(item => new
                {
                    item.SelectorTurnId,
                    item.SelectorTurnSequence,
                    item.SelectorNodeIndex
                })
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToHashSet();
            return bindings.Select(binding =>
            {
                if (binding.ResolutionState != "bound" || !binding.ReplacesActiveBinding)
                {
                    var carriesReplacementIdentity =
                        binding.SelectorTurnId is not null ||
                        binding.SelectorTurnSequence is not null ||
                        binding.SelectorNodeIndex is not null ||
                        binding.SelectorNodeStartTokenIndex is not null ||
                        binding.SelectorNodeTokenLength is not null ||
                        binding.SupersededTurnId is not null ||
                        binding.SupersededTurnSequence is not null ||
                        binding.SupersededNodeIndex is not null ||
                        binding.HasSupersededCurrentTurnEntity ||
                        binding.SupersededCurrentTurnNodeIndex is not null ||
                        binding.SupersededCurrentTurnSemanticSignature is not null ||
                        binding.SupersededCurrentTurnSemanticDimension is not null ||
                        binding.SupersededCurrentTurnSemanticValue is not null ||
                        binding.SupersededCurrentTurnNodeStartTokenIndex is not null ||
                        binding.SupersededCurrentTurnNodeTokenLength is not null;
                    return binding.ResolutionState == "bound" && carriesReplacementIdentity
                        ? InvalidateBinding(binding, "reference_replacement_occurrence_invalid")
                        : binding;
                }

                var identity = new
                {
                    binding.SelectorTurnId,
                    binding.SelectorTurnSequence,
                    binding.SelectorNodeIndex
                };
                var selectorIsValid = binding.SelectorTurnId == turn.Id &&
                    binding.SelectorTurnSequence == turn.SequenceNumber &&
                    binding.SelectorNodeIndex is int nodeIndex &&
                    nodeIndex >= 0 &&
                    nodeIndex < graph.Nodes.Count &&
                    !string.IsNullOrWhiteSpace(binding.SelectorSemanticSignature) &&
                    !string.IsNullOrWhiteSpace(binding.ReferenceRuleSignature) &&
                    string.Equals(
                        graph.Nodes[nodeIndex].SemanticSignature,
                        binding.SelectorSemanticSignature,
                        StringComparison.Ordinal) &&
                    graph.Nodes[nodeIndex].StartTokenIndex == binding.SelectorNodeStartTokenIndex &&
                    graph.Nodes[nodeIndex].TokenLength == binding.SelectorNodeTokenLength &&
                    ValidateCurrentTurnSupersededEntityIdentity(binding, graph) &&
                    !conflictingOccurrences.Contains(identity);
                return selectorIsValid
                    ? binding
                    : InvalidateBinding(binding, "reference_replacement_occurrence_invalid");
            }).ToArray();
        }
        catch (JsonException)
        {
            // A legacy malformed state record cannot become a reference source.
            return [MalformedBindingState()];
        }
    }

    private static LegendFounderAiDiscourseReferenceBinding InvalidateBinding(
        LegendFounderAiDiscourseReferenceBinding binding,
        string reasonCode) =>
        binding with
        {
            ResolutionState = "unresolved",
            ReasonCode = reasonCode
        };

    private static bool IsReplacementAttempt(
        LegendFounderAiDiscourseReferenceBinding binding) =>
        binding.ReplacesActiveBinding ||
        binding.SelectorTurnId is not null ||
        binding.SelectorTurnSequence is not null ||
        binding.SelectorNodeIndex is not null ||
        binding.SelectorNodeStartTokenIndex is not null ||
        binding.SelectorNodeTokenLength is not null ||
        binding.SupersededTurnId is not null ||
        binding.SupersededTurnSequence is not null ||
        binding.SupersededNodeIndex is not null ||
        binding.HasSupersededCurrentTurnEntity ||
        binding.SupersededCurrentTurnNodeIndex is not null ||
        binding.SupersededCurrentTurnSemanticSignature is not null ||
        binding.SupersededCurrentTurnSemanticDimension is not null ||
        binding.SupersededCurrentTurnSemanticValue is not null ||
        binding.SupersededCurrentTurnNodeStartTokenIndex is not null ||
        binding.SupersededCurrentTurnNodeTokenLength is not null;

    private static bool ValidateSupersededEntityIdentity(
        LegendFounderAiDiscourseReferenceBinding binding,
        LegendFounderAiDiscourseTurn containingTurn,
        IReadOnlyList<LegendFounderAiDiscourseTurn> availableTurns)
    {
        if (binding.SupersededTurnId is not Guid turnId ||
            turnId == Guid.Empty ||
            binding.SupersededTurnSequence is not int turnSequence ||
            binding.SupersededNodeIndex is not int nodeIndex ||
            binding.SupersededNodeStartTokenIndex is not int startTokenIndex ||
            binding.SupersededNodeTokenLength is not int tokenLength ||
            turnSequence <= 0 ||
            turnSequence >= containingTurn.SequenceNumber ||
            nodeIndex < 0 ||
            startTokenIndex < 0 ||
            tokenLength <= 0 ||
            string.IsNullOrWhiteSpace(binding.SupersededEntitySemanticSignature) ||
            string.IsNullOrWhiteSpace(binding.SupersededEntitySemanticDimension) ||
            string.IsNullOrWhiteSpace(binding.SupersededEntitySemanticValue) ||
            !string.Equals(
                binding.SupersededEntitySemanticDimension,
                binding.EntitySemanticDimension,
                StringComparison.Ordinal))
        {
            return false;
        }
        var sourceTurns = availableTurns.Where(item =>
                item.Id == turnId && item.SequenceNumber == turnSequence)
            .ToArray();
        if (sourceTurns.Length != 1)
            return false;
        var graph = DeserializeMeaning(sourceTurns[0].MeaningGraphJson);
        if (!graph.IsComposed || nodeIndex >= graph.Nodes.Count)
            return false;
        var node = graph.Nodes[nodeIndex];
        return string.Equals(
                node.SemanticSignature,
                binding.SupersededEntitySemanticSignature,
                StringComparison.Ordinal) &&
            string.Equals(
                node.SemanticDimension,
                binding.SupersededEntitySemanticDimension,
                StringComparison.Ordinal) &&
            string.Equals(
                node.SemanticValue,
                binding.SupersededEntitySemanticValue,
                StringComparison.Ordinal) &&
            node.StartTokenIndex == startTokenIndex &&
            node.TokenLength == tokenLength;
    }

    private static bool ValidateCurrentTurnSupersededEntityIdentity(
        LegendFounderAiDiscourseReferenceBinding binding,
        LegendConnectUtteranceMeaningGraphSnapshot graph)
    {
        var carriesIdentity =
            binding.SupersededCurrentTurnNodeIndex is not null ||
            binding.SupersededCurrentTurnSemanticSignature is not null ||
            binding.SupersededCurrentTurnSemanticDimension is not null ||
            binding.SupersededCurrentTurnSemanticValue is not null ||
            binding.SupersededCurrentTurnNodeStartTokenIndex is not null ||
            binding.SupersededCurrentTurnNodeTokenLength is not null;
        if (!binding.HasSupersededCurrentTurnEntity)
            return !carriesIdentity;
        if (binding.SupersededCurrentTurnNodeIndex is not int nodeIndex ||
            nodeIndex < 0 || nodeIndex >= graph.Nodes.Count ||
            binding.SupersededCurrentTurnNodeStartTokenIndex is not int startTokenIndex ||
            binding.SupersededCurrentTurnNodeTokenLength is not int tokenLength ||
            startTokenIndex < 0 ||
            tokenLength <= 0 ||
            string.IsNullOrWhiteSpace(binding.SupersededCurrentTurnSemanticSignature) ||
            string.IsNullOrWhiteSpace(binding.SupersededCurrentTurnSemanticDimension) ||
            string.IsNullOrWhiteSpace(binding.SupersededCurrentTurnSemanticValue) ||
            !string.Equals(
                binding.SupersededCurrentTurnSemanticDimension,
                binding.EntitySemanticDimension,
                StringComparison.Ordinal))
            return false;
        var node = graph.Nodes[nodeIndex];
        return string.Equals(node.SemanticSignature, binding.SupersededCurrentTurnSemanticSignature, StringComparison.Ordinal) &&
            string.Equals(node.SemanticDimension, binding.SupersededCurrentTurnSemanticDimension, StringComparison.Ordinal) &&
            string.Equals(node.SemanticValue, binding.SupersededCurrentTurnSemanticValue, StringComparison.Ordinal) &&
            node.StartTokenIndex == startTokenIndex &&
            node.TokenLength == tokenLength;
    }

    private static bool IsMalformedBindingState(
        LegendFounderAiDiscourseReferenceBinding binding) =>
        binding.ReasonCode == "reference_binding_state_invalid";

    private static LegendFounderAiDiscourseReferenceBinding MalformedBindingState() =>
        new(
            "unresolved",
            "reference_binding_state_invalid",
            string.Empty,
            string.Empty,
            null,
            null,
            null,
            null,
            null,
            true,
            null);

    private static IReadOnlyList<DiscourseBindingEntry> BindingEntries(
        IReadOnlyList<LegendFounderAiDiscourseTurn> turns,
        IReadOnlyDictionary<Guid, IReadOnlyList<LegendFounderAiDiscourseReferenceBinding>> validatedBindings) =>
        turns.OrderBy(item => item.SequenceNumber)
            .SelectMany(turn => validatedBindings[turn.Id]
                .Select(binding => new DiscourseBindingEntry(turn, binding)))
            .ToArray();

    private static ActiveBindingSelection ResolveActiveBinding(
        IReadOnlyList<DiscourseBindingEntry> entries,
        string semanticDimension)
    {
        var actions = entries.Where(item =>
                string.Equals(
                    item.Binding.EntitySemanticDimension,
                    semanticDimension,
                    StringComparison.Ordinal) &&
                (item.Binding.ResolutionState == "bound" ||
                 IsReplacementAttempt(item.Binding)))
            .ToArray();
        var latestActionSequence = actions.Length == 0
            ? (int?)null
            : actions.Max(item => item.Turn.SequenceNumber);
        var latestMalformedSequence = entries
            .Where(item => IsMalformedBindingState(item.Binding))
            .Select(item => (int?)item.Turn.SequenceNumber)
            .Max();
        var latestUnscopedReplacementSequence = entries
            .Where(item =>
                IsReplacementAttempt(item.Binding) &&
                string.IsNullOrWhiteSpace(item.Binding.EntitySemanticDimension))
            .Select(item => (int?)item.Turn.SequenceNumber)
            .Max();
        var latestUntrustedSequence = new[]
        {
            latestMalformedSequence,
            latestUnscopedReplacementSequence
        }.Max();
        if (latestUntrustedSequence is not null &&
            (latestActionSequence is null || latestUntrustedSequence >= latestActionSequence))
        {
            return ActiveBindingSelection.Invalid;
        }
        if (latestActionSequence is null)
            return ActiveBindingSelection.None;

        var latest = actions
            .Where(item => item.Turn.SequenceNumber == latestActionSequence)
            .ToArray();
        if (latest.Any(item =>
                IsReplacementAttempt(item.Binding) &&
                item.Binding.ResolutionState != "bound"))
        {
            return ActiveBindingSelection.Invalid;
        }

        var boundTargets = latest
            .Where(item => item.Binding.ResolutionState == "bound")
            .GroupBy(item => new
            {
                item.Binding.EntityTurnId,
                item.Binding.EntityTurnSequence,
                item.Binding.EntityNodeIndex,
                item.Binding.EntitySemanticSignature,
                item.Binding.EntitySemanticValue
            })
            .ToArray();
        if (boundTargets.Length != 1)
            return ActiveBindingSelection.Invalid;

        var selected = boundTargets[0]
            .OrderBy(item => item.Binding.SelectorTurnId)
            .ThenBy(item => item.Binding.SelectorTurnSequence)
            .ThenBy(item => item.Binding.SelectorNodeIndex)
            .ThenBy(item => item.Binding.SelectorSemanticSignature, StringComparer.Ordinal)
            .ThenBy(item => item.Binding.ReferenceRuleSignature, StringComparer.Ordinal)
            .First();
        return new(true, selected.Binding);
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

    private sealed record DiscourseBindingEntry(
        LegendFounderAiDiscourseTurn Turn,
        LegendFounderAiDiscourseReferenceBinding Binding);

    private sealed record ActiveBindingSelection(
        bool HasBinding,
        LegendFounderAiDiscourseReferenceBinding? Binding)
    {
        public static ActiveBindingSelection None { get; } = new(false, null);
        public static ActiveBindingSelection Invalid { get; } = new(true, null);
    }

    private sealed record ActiveDiscourseBindingResolution(
        bool HasBinding,
        DiscourseEntityCandidate? Candidate)
    {
        public static ActiveDiscourseBindingResolution None { get; } = new(false, null);
        public static ActiveDiscourseBindingResolution Invalid { get; } = new(true, null);
    }

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
    bool ReplacesActiveBinding,
    string? ReferenceRuleSignature,
    Guid? SupersededTurnId = null,
    int? SupersededTurnSequence = null,
    int? SupersededNodeIndex = null,
    int? SupersededNodeStartTokenIndex = null,
    int? SupersededNodeTokenLength = null,
    string? RuleLanguageCode = null,
    string? RuleResolutionMode = null,
    int? RuleSelectionRank = null,
    string? RuleAllowedSourceRoles = null,
    string? SupersededEntitySemanticSignature = null,
    string? SupersededEntitySemanticDimension = null,
    string? SupersededEntitySemanticValue = null,
    Guid? SelectorTurnId = null,
    int? SelectorTurnSequence = null,
    int? SelectorNodeIndex = null,
    int? SelectorNodeStartTokenIndex = null,
    int? SelectorNodeTokenLength = null,
    bool HasSupersededCurrentTurnEntity = false,
    int? SupersededCurrentTurnNodeIndex = null,
    string? SupersededCurrentTurnSemanticSignature = null,
    string? SupersededCurrentTurnSemanticDimension = null,
    string? SupersededCurrentTurnSemanticValue = null,
    int? SupersededCurrentTurnNodeStartTokenIndex = null,
    int? SupersededCurrentTurnNodeTokenLength = null);
