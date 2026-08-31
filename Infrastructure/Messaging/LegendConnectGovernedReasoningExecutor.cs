using System;
using System.Collections.Generic;
using System.Linq;

namespace Infrastructure.Messaging;

/// <summary>
/// Pure deterministic executor for Founder-governed semantic reasoning rules.
/// It owns no database, curriculum, response, provider, persistence, or surface
/// realization authority. The curriculum selector supplies already-qualified
/// rules and remains the sole authority that may consume the derived states.
///
/// Relationship identities describe execution semantics without naming a
/// subject domain:
///   reasoning.forward.*       source -> result
///   reasoning.bidirectional.* source <-> result
///   reasoning.constraint.*    source and result may not coexist
///   reasoning.deduction.universal.*   universal fact rule -> consequence
///   reasoning.deduction.conditional.* conditional premises -> consequence
///   reasoning.epistemic.observational-equivalence.* shared evidence -> no selection
///   reasoning.epistemic.insufficient-evidence.*     governed insufficiency -> no selection
///   reasoning.epistemic.discriminating-evidence.*  governed discriminator -> conclusion
/// The suffix is opaque curriculum meaning, allowing new skill domains without
/// adding code or a topic router.
/// </summary>
internal static class LegendConnectGovernedReasoningExecutor
{
    internal const int MaximumDepth = 12;
    internal const int MaximumStates = 512;
    internal const int MaximumRules = 4096;
    internal const int MaximumFrameDimensions = 12;
    internal const int MaximumRuleEvaluations = MaximumStates * MaximumRules;
    internal const string EpistemicStatusDimension = "epistemic_status";
    internal const string ObservationalEquivalenceValue = "observational_equivalence";
    internal const string InsufficientEvidenceValue = "insufficient_evidence";
    internal const string ResolvedByDiscriminatingEvidenceValue = "resolved_by_discriminating_evidence";
    internal const string UnresolvedContradictionValue = "unresolved_contradiction";
    internal const string CauseSelectionDimension = "cause_selection";
    internal const string UndeterminedValue = "undetermined";
    internal const string EvidenceAuthorityDimension = "evidence_authority";
    internal const string EqualAuthorityValue = "equal";
    internal const string NonDispositiveAuthorityValue = "non_dispositive_without_discrimination";
    internal const string EvidenceRequirementDimension = "evidence_requirement";
    internal const string DiscriminatingEvidenceValue = "discriminating_evidence";
    internal const string DiscriminatingEvidenceDimension = "discriminating_evidence";
    internal const string ConflictDimensionDimension = "conflict_dimension";
    internal const string MultipleConflictDimensionsValue = "multiple";

    internal static bool IsExecutableOperatorIdentity(string? identity) =>
        ResolveMode(identity) is not null;

    internal static LegendGovernedReasoningExecution Derive(
        IReadOnlyDictionary<string, string> initialValues,
        IReadOnlyList<LegendGovernedReasoningRule> rules,
        IReadOnlySet<Guid> initialSemanticFamilyIds)
    {
        if (initialValues.Count == 0 || rules.Count == 0)
            return LegendGovernedReasoningExecution.Empty;
        if (rules.Count > MaximumRules)
            return new(false, false, true, [], []);

        var normalizedRules = rules
            .Where(IsGovernedExecutableRule)
            // Enumeration order is deterministic for reproducibility. Proof
            // authority is decided independently by IsStrongerProof so a
            // later multi-hop HigherStandard proof can replace an earlier
            // broad or weak proof of the same state.
            .OrderByDescending(rule => rule.EvidenceStandard)
            .ThenBy(rule => rule.TransitionSignature, StringComparer.Ordinal)
            .ThenBy(rule => rule.OperatorIdentity, StringComparer.Ordinal)
            .ToArray();
        if (normalizedRules.Length == 0)
            return LegendGovernedReasoningExecution.Empty;

        var constraints = normalizedRules
            .Where(rule => ResolveMode(rule.OperatorIdentity) == ReasoningMode.Constraint)
            .ToArray();
        if (ViolatesConstraint(initialValues, constraints))
            return new(true, false, false, [], []);

        var directional = new List<DirectionalRule>();
        foreach (var rule in normalizedRules)
        {
            var mode = ResolveMode(rule.OperatorIdentity);
            if (mode == ReasoningMode.Forward)
            {
                directional.Add(new DirectionalRule(
                    rule,
                    rule.SourceFrame,
                    rule.ResultFrame,
                    rule.SourceSemanticFamilyIds,
                    rule.ResultSemanticFamilyIds,
                    rule.FamilyConnections,
                    false,
                    false));
            }
            else if (mode == ReasoningMode.Bidirectional)
            {
                directional.Add(new DirectionalRule(
                    rule,
                    rule.SourceFrame,
                    rule.ResultFrame,
                    rule.SourceSemanticFamilyIds,
                    rule.ResultSemanticFamilyIds,
                    rule.FamilyConnections,
                    false,
                    false));
                directional.Add(new DirectionalRule(
                    rule,
                    rule.ResultFrame,
                    rule.SourceFrame,
                    rule.ResultSemanticFamilyIds,
                    rule.SourceSemanticFamilyIds,
                    rule.FamilyConnections.Select(item => new LegendGovernedReasoningFamilyConnection(
                        item.ResultSemanticFamilyId,
                        item.SourceSemanticFamilyId,
                        item.HasExplicitGovernedTransfer)).ToArray(),
                    true,
                    false));
            }
            else if (mode == ReasoningMode.Deduction || IsEpistemicMode(mode))
            {
                // Governed deductive and epistemic rules are implications,
                // never equivalences. No reverse DirectionalRule is created,
                // so a conclusion cannot manufacture its premises merely
                // because its result frame is present.
                directional.Add(new DirectionalRule(
                    rule,
                    rule.SourceFrame,
                    rule.ResultFrame,
                    rule.SourceSemanticFamilyIds,
                    rule.ResultSemanticFamilyIds,
                    rule.FamilyConnections,
                    false,
                    true));
            }
        }

        var initial = Copy(initialValues);
        var initialFamilies = initialSemanticFamilyIds
            .OrderBy(item => item)
            .ToHashSet();
        var initialIdentity = CanonicalProofState(initial, initialFamilies);
        var initialProof = new LegendGovernedReasoningProof(
            initial,
            [],
            [],
            initialFamilies,
            0,
            int.MaxValue,
            int.MaxValue);
        var bestProofs = new Dictionary<string, LegendGovernedReasoningProof>(StringComparer.Ordinal)
        {
            [initialIdentity] = initialProof
        };
        var queue = new Queue<LegendGovernedReasoningProof>();
        queue.Enqueue(initialProof);
        var ruleEvaluations = 0;
        var conflictContexts = new Dictionary<string, ReasoningConflictContext>(StringComparer.Ordinal);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var currentIdentity = CanonicalProofState(
                current.Values,
                current.SemanticFamilyIds);
            if (!bestProofs.TryGetValue(currentIdentity, out var authoritative) ||
                !ReferenceEquals(authoritative, current))
            {
                continue;
            }
            if (current.Depth >= MaximumDepth)
                continue;

            foreach (var rule in directional)
            {
                ruleEvaluations++;
                if (ruleEvaluations > MaximumRuleEvaluations)
                    return new(false, false, true, [], []);
                var connectedResultFamilies = ResolveConnectedResultFamilies(
                    current.SemanticFamilyIds,
                    rule.FamilyConnections);
                if (rule.IsProofCarrying && connectedResultFamilies.Count == 0)
                {
                    continue;
                }
                if (!TryApply(
                        rule.SourceFrame,
                        rule.ResultFrame,
                        current.Values,
                        rule.IsProofCarrying,
                        out var nextValues,
                        out var instantiatedConclusions,
                        out var applicationConflicts))
                {
                    foreach (var applicationConflict in applicationConflicts)
                    {
                        var existingStep = FindConclusionAuthority(
                            current,
                            applicationConflict.SemanticDimension,
                            applicationConflict.ExistingValue);
                        // A governed rule may not silently overwrite a fact
                        // supplied by the present meaning graph or discourse
                        // state. Those inputs do not carry a comparable rule
                        // authority, so retain the established fail-closed
                        // contradiction outcome.
                        if (existingStep is null)
                            return new(false, true, false, [], []);

                        var proposedFamilies = rule.IsProofCarrying
                            ? connectedResultFamilies
                            : rule.ResultSemanticFamilyIds.OrderBy(item => item).ToHashSet();
                        var proposedStep = BuildProofStep(
                            rule,
                            current,
                            proposedFamilies,
                            instantiatedConclusions);
                        var context = BuildConflictContext(
                            applicationConflict,
                            current,
                            existingStep,
                            proposedStep,
                            proposedFamilies);
                        if (!conflictContexts.ContainsKey(context.Identity))
                        {
                            if (bestProofs.Count + conflictContexts.Count >= MaximumStates)
                                return new(false, false, true, [], []);
                            conflictContexts[context.Identity] = context;
                        }
                    }
                    continue;
                }
                if (ViolatesConstraint(nextValues, constraints))
                    continue;

                var nextFamilies = rule.IsProofCarrying
                    ? connectedResultFamilies
                    : rule.ResultSemanticFamilyIds.OrderBy(item => item).ToHashSet();
                var identity = CanonicalProofState(nextValues, nextFamilies);
                var transitionIdentity = rule.Rule.TransitionSignature +
                    (rule.Reversed ? ":reverse" : string.Empty);
                var path = current.TransitionPath.Append(transitionIdentity).ToArray();
                var evidenceLineage = current.EvidenceLineage.Append(
                    BuildProofStep(
                        rule,
                        current,
                        nextFamilies,
                        instantiatedConclusions)).ToArray();
                var evidence = current.Depth == 0
                    ? rule.Rule.IndependentEvidenceCount
                    : Math.Min(current.EvidenceCount, rule.Rule.IndependentEvidenceCount);
                var evidenceStandard = current.Depth == 0
                    ? rule.Rule.EvidenceStandard
                    : Math.Min(current.EvidenceStandard, rule.Rule.EvidenceStandard);
                var proof = new LegendGovernedReasoningProof(
                    nextValues,
                    path,
                    evidenceLineage,
                    nextFamilies,
                    current.Depth + 1,
                    evidence,
                    evidenceStandard);
                if (bestProofs.TryGetValue(identity, out var existing) &&
                    !IsStrongerProof(proof, existing))
                {
                    continue;
                }
                if (!bestProofs.ContainsKey(identity) &&
                    bestProofs.Count + conflictContexts.Count >= MaximumStates)
                    return new(false, false, true, [], []);

                bestProofs[identity] = proof;
                queue.Enqueue(proof);
            }
        }

        // A lower-standard tie among otherwise dispositive conclusions cannot
        // keep a dimension unresolved when a uniquely higher governed
        // authority also conflicts on that same dimension. Retain only pairs
        // that include the dimension's highest observed authority. The
        // non-dispositive observational/insufficiency gate below still
        // requires explicit discriminating evidence regardless of rank.
        var highestConflictStandards = conflictContexts.Values
            .GroupBy(item => item.Conflict.SemanticDimension, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Max(item => Math.Max(
                    item.Conflict.First.EvidenceStandard,
                    item.Conflict.Second.EvidenceStandard)),
                StringComparer.OrdinalIgnoreCase);
        var authoritativeConflictContexts = conflictContexts.Values
            .Where(item => Math.Max(
                    item.Conflict.First.EvidenceStandard,
                    item.Conflict.Second.EvidenceStandard) ==
                highestConflictStandards[item.Conflict.SemanticDimension])
            .ToArray();
        var conflicts = authoritativeConflictContexts
            .OrderBy(item => item.Identity, StringComparer.Ordinal)
            .Select(item => item.Conflict)
            .ToArray();
        var derived = bestProofs
            .Where(item => !string.Equals(item.Key, initialIdentity, StringComparison.Ordinal))
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => item.Value)
            .Where(proof => !IsRejectedByConflict(proof, conflicts))
            .ToArray();
        var unresolvedContexts = authoritativeConflictContexts
            .Where(item => IsUnresolvedConflict(item.Conflict))
            .OrderBy(item => item.Identity, StringComparer.Ordinal)
            .ToArray();
        LegendGovernedReasoningProof[] unresolvedProofs = unresolvedContexts.Length == 0
            ? []
            : new[] { BuildUnresolvedConflictProof(unresolvedContexts) };
        var finalStates = derived
            .Concat(unresolvedProofs)
            .GroupBy(proof => CanonicalProofState(proof.Values, proof.SemanticFamilyIds), StringComparer.Ordinal)
            .Select(group => group.Aggregate((best, candidate) =>
                IsStrongerProof(candidate, best) ? candidate : best))
            .OrderBy(proof => CanonicalProofState(proof.Values, proof.SemanticFamilyIds), StringComparer.Ordinal)
            .ToArray();
        return new(false, false, false, finalStates, conflicts);
    }

    private static bool IsGovernedExecutableRule(LegendGovernedReasoningRule rule)
    {
        var mode = ResolveMode(rule.OperatorIdentity);
        if (mode is null || string.IsNullOrWhiteSpace(rule.TransitionSignature) ||
            rule.SourceFrame.Count is < 1 or > MaximumFrameDimensions ||
            rule.ResultFrame.Count is < 1 or > MaximumFrameDimensions ||
            rule.SourceFrame.Any(item => string.IsNullOrWhiteSpace(item.Key) ||
                string.IsNullOrWhiteSpace(item.Value)) ||
            rule.ResultFrame.Any(item => string.IsNullOrWhiteSpace(item.Key) ||
                string.IsNullOrWhiteSpace(item.Value)) ||
            rule.SourceSemanticFamilyIds.Count == 0 ||
            rule.ResultSemanticFamilyIds.Count == 0 ||
            rule.IndependentEvidenceCount <= 0)
        {
            return false;
        }

        var evidenceIdentities = rule.IndependentEvidenceIdentities
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (evidenceIdentities.Length != rule.IndependentEvidenceCount)
            return false;

        if (IsEpistemicMode(mode) && !IsGovernedEpistemicRule(rule, mode!.Value))
            return false;

        if (rule.FamilyConnections.Count == 0 ||
            rule.FamilyConnections.Distinct().Count() != rule.FamilyConnections.Count ||
            rule.FamilyConnections.Any(item =>
                !rule.SourceSemanticFamilyIds.Contains(item.SourceSemanticFamilyId) ||
                !rule.ResultSemanticFamilyIds.Contains(item.ResultSemanticFamilyId) ||
                (item.SourceSemanticFamilyId != item.ResultSemanticFamilyId &&
                 !item.HasExplicitGovernedTransfer)))
        {
            return false;
        }

        return rule.SourceSemanticFamilyIds.SetEquals(
                   rule.FamilyConnections.Select(item => item.SourceSemanticFamilyId)) &&
               rule.ResultSemanticFamilyIds.SetEquals(
                   rule.FamilyConnections.Select(item => item.ResultSemanticFamilyId));
    }

    private static bool IsGovernedEpistemicRule(
        LegendGovernedReasoningRule rule,
        ReasoningMode mode)
    {
        if (mode == ReasoningMode.ObservationalEquivalence)
        {
            return HasSemanticValue(
                       rule.ResultFrame,
                       EpistemicStatusDimension,
                       ObservationalEquivalenceValue) &&
                   HasSemanticValue(
                       rule.ResultFrame,
                       CauseSelectionDimension,
                       UndeterminedValue) &&
                   HasSemanticValue(
                       rule.ResultFrame,
                       EvidenceRequirementDimension,
                       DiscriminatingEvidenceValue);
        }
        if (mode == ReasoningMode.InsufficientEvidence)
        {
            return HasSemanticValue(
                       rule.ResultFrame,
                       EpistemicStatusDimension,
                       InsufficientEvidenceValue) &&
                   HasSemanticValue(
                       rule.ResultFrame,
                       CauseSelectionDimension,
                       UndeterminedValue) &&
                   HasSemanticValue(
                       rule.ResultFrame,
                       EvidenceRequirementDimension,
                       DiscriminatingEvidenceValue);
        }
        if (mode == ReasoningMode.DiscriminatingEvidence)
        {
            return rule.SourceFrame.TryGetValue(
                       DiscriminatingEvidenceDimension,
                       out var discriminator) &&
                   !string.IsNullOrWhiteSpace(discriminator) &&
                   HasSemanticValue(
                       rule.ResultFrame,
                       EpistemicStatusDimension,
                       ResolvedByDiscriminatingEvidenceValue) &&
                   rule.ResultFrame.TryGetValue(CauseSelectionDimension, out var selected) &&
                   !string.IsNullOrWhiteSpace(selected) &&
                   !string.Equals(selected, UndeterminedValue, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private static bool HasSemanticValue(
        IReadOnlyDictionary<string, string> frame,
        string dimension,
        string expected) =>
        frame.TryGetValue(dimension, out var value) &&
        string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);

    private static HashSet<Guid> ResolveConnectedResultFamilies(
        IReadOnlySet<Guid> currentSemanticFamilyIds,
        IReadOnlyList<LegendGovernedReasoningFamilyConnection> connections) =>
        connections
            .Where(item => currentSemanticFamilyIds.Contains(item.SourceSemanticFamilyId))
            .Select(item => item.ResultSemanticFamilyId)
            .OrderBy(item => item)
            .ToHashSet();

    private static bool IsStrongerProof(
        LegendGovernedReasoningProof candidate,
        LegendGovernedReasoningProof existing)
    {
        var comparison = candidate.EvidenceStandard.CompareTo(existing.EvidenceStandard);
        if (comparison != 0)
            return comparison > 0;

        comparison = candidate.Depth.CompareTo(existing.Depth);
        if (comparison != 0)
            return comparison < 0;

        comparison = candidate.EvidenceCount.CompareTo(existing.EvidenceCount);
        if (comparison != 0)
            return comparison > 0;

        return ComparePath(candidate.TransitionPath, existing.TransitionPath) < 0;
    }

    private static IReadOnlyDictionary<string, string> ProjectFrameValues(
        IReadOnlyDictionary<string, string> frame,
        IReadOnlyDictionary<string, string> values) =>
        frame.Keys
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                key => key,
                key => values[key],
                StringComparer.OrdinalIgnoreCase);

    private static LegendGovernedReasoningProofStep BuildProofStep(
        DirectionalRule rule,
        LegendGovernedReasoningProof current,
        IReadOnlySet<Guid> resultFamilies,
        IReadOnlyDictionary<string, string> conclusions) =>
        new(
            rule.Rule.TransitionSignature,
            rule.Rule.OperatorIdentity,
            ProjectFrameValues(rule.SourceFrame, current.Values),
            conclusions,
            rule.Rule.IndependentEvidenceIdentities,
            rule.Rule.IndependentEvidenceCount,
            rule.Rule.EvidenceStandard,
            rule.IsProofCarrying
                ? rule.FamilyConnections
                    .Where(item => current.SemanticFamilyIds.Contains(
                        item.SourceSemanticFamilyId))
                    .Select(item => item.SourceSemanticFamilyId)
                    .ToHashSet()
                : rule.SourceSemanticFamilyIds,
            resultFamilies,
            rule.FamilyConnections.Any(item =>
                current.SemanticFamilyIds.Contains(item.SourceSemanticFamilyId) &&
                item.HasExplicitGovernedTransfer),
            rule.Reversed);

    private static LegendGovernedReasoningProofStep? FindConclusionAuthority(
        LegendGovernedReasoningProof proof,
        string semanticDimension,
        string semanticValue)
    {
        for (var index = proof.EvidenceLineage.Count - 1; index >= 0; index--)
        {
            var step = proof.EvidenceLineage[index];
            if (step.Conclusions.TryGetValue(semanticDimension, out var conclusion) &&
                string.Equals(conclusion, semanticValue, StringComparison.OrdinalIgnoreCase))
            {
                return step;
            }
        }
        return null;
    }

    private static ReasoningConflictContext BuildConflictContext(
        ReasoningApplicationConflict applicationConflict,
        LegendGovernedReasoningProof current,
        LegendGovernedReasoningProofStep existingStep,
        LegendGovernedReasoningProofStep proposedStep,
        IReadOnlySet<Guid> proposedFamilies)
    {
        var existing = ConflictSide(
            applicationConflict.ExistingValue,
            existingStep);
        var proposed = ConflictSide(
            applicationConflict.ProposedValue,
            proposedStep);
        var ordered = new[] { existing, proposed }
            .OrderBy(ConflictSideIdentity, StringComparer.Ordinal)
            .ToArray();
        var comparison = existing.EvidenceStandard.CompareTo(proposed.EvidenceStandard);
        var lacksDiscriminatingEvidence =
            (IsNonSelectingEpistemicOperator(existing.OperatorIdentity) &&
             !IsDiscriminatingEvidenceOperator(proposed.OperatorIdentity)) ||
            (IsNonSelectingEpistemicOperator(proposed.OperatorIdentity) &&
             !IsDiscriminatingEvidenceOperator(existing.OperatorIdentity));
        var resolution = lacksDiscriminatingEvidence
            ? LegendGovernedReasoningConflictResolution.UnresolvedWithoutDiscriminatingEvidence
            : comparison == 0
                ? LegendGovernedReasoningConflictResolution.UnresolvedEqualAuthority
                : LegendGovernedReasoningConflictResolution.ResolvedByHigherStandard;
        LegendGovernedReasoningConflictSide? selected =
            resolution != LegendGovernedReasoningConflictResolution.ResolvedByHigherStandard
            ? null
            : comparison > 0 ? existing : proposed;
        var conflict = new LegendGovernedReasoningConflict(
            applicationConflict.SemanticDimension,
            ordered[0],
            ordered[1],
            resolution,
            selected,
            resolution != LegendGovernedReasoningConflictResolution.ResolvedByHigherStandard);
        var identity = applicationConflict.SemanticDimension.Trim().ToLowerInvariant() + "\u001f" +
            string.Join("\u001f", ordered.Select(ConflictSideIdentity));
        return new(
            identity,
            conflict,
            current,
            proposedStep,
            proposedFamilies);
    }

    private static LegendGovernedReasoningConflictSide ConflictSide(
        string value,
        LegendGovernedReasoningProofStep step) =>
        new(
            value,
            step.TransitionSignature,
            step.OperatorIdentity,
            step.EvidenceStandard,
            step.IndependentEvidenceCount,
            step.IndependentEvidenceIdentities);

    private static string ConflictSideIdentity(LegendGovernedReasoningConflictSide side) =>
        side.TransitionSignature + "\u001e" + side.SemanticValue.Trim().ToLowerInvariant();

    private static bool IsNonSelectingEpistemicOperator(string identity) =>
        ResolveMode(identity) is ReasoningMode.ObservationalEquivalence or
            ReasoningMode.InsufficientEvidence;

    private static bool IsDiscriminatingEvidenceOperator(string identity) =>
        ResolveMode(identity) == ReasoningMode.DiscriminatingEvidence;

    private static bool IsUnresolvedConflict(LegendGovernedReasoningConflict conflict) =>
        conflict.Resolution != LegendGovernedReasoningConflictResolution.ResolvedByHigherStandard;

    private static bool IsRejectedByConflict(
        LegendGovernedReasoningProof proof,
        IReadOnlyList<LegendGovernedReasoningConflict> conflicts)
    {
        foreach (var conflict in conflicts)
        {
            var rejectedSides = IsUnresolvedConflict(conflict)
                ? new[] { conflict.First, conflict.Second }
                : new[] { conflict.Selected == conflict.First ? conflict.Second : conflict.First };
            foreach (var side in rejectedSides)
            {
                if (proof.EvidenceLineage.Any(step =>
                        string.Equals(
                            step.TransitionSignature,
                            side.TransitionSignature,
                            StringComparison.Ordinal) &&
                        step.Conclusions.TryGetValue(conflict.SemanticDimension, out var value) &&
                        string.Equals(value, side.SemanticValue, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static LegendGovernedReasoningProof BuildUnresolvedConflictProof(
        IReadOnlyList<ReasoningConflictContext> contexts)
    {
        var values = Copy(contexts[0].Current.Values);
        var conflictDimensions = contexts
            .Select(item => item.Conflict.SemanticDimension)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var dimension in conflictDimensions)
            values.Remove(dimension);
        if (conflictDimensions.Contains(
                CauseSelectionDimension,
                StringComparer.OrdinalIgnoreCase))
        {
            values[CauseSelectionDimension] = UndeterminedValue;
        }
        values[EpistemicStatusDimension] = UnresolvedContradictionValue;
        values[EvidenceAuthorityDimension] = contexts.All(item =>
                item.Conflict.Resolution ==
                    LegendGovernedReasoningConflictResolution.UnresolvedEqualAuthority)
            ? EqualAuthorityValue
            : NonDispositiveAuthorityValue;
        values[EvidenceRequirementDimension] = DiscriminatingEvidenceValue;
        values[ConflictDimensionDimension] = conflictDimensions.Length == 1
            ? conflictDimensions[0]
            : MultipleConflictDimensionsValue;

        var path = contexts
            .SelectMany(item => item.Current.TransitionPath.Append(
                item.ProposedStep.TransitionSignature))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var evidenceLineage = contexts
            .SelectMany(item => item.Current.EvidenceLineage.Append(item.ProposedStep))
            .GroupBy(step => step.TransitionSignature + "\u001f" +
                CanonicalState(step.Conclusions), StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        var families = contexts
            .SelectMany(item => item.Current.SemanticFamilyIds.Concat(item.ProposedFamilies))
            .OrderBy(item => item)
            .ToHashSet();
        return new(
            values,
            path,
            evidenceLineage,
            families,
            Math.Min(MaximumDepth, contexts.Max(item => item.Current.Depth + 1)),
            contexts.Min(item => Math.Min(
                item.Current.EvidenceCount,
                item.ProposedStep.IndependentEvidenceCount)),
            contexts.Min(item => Math.Min(
                item.Current.EvidenceStandard,
                item.ProposedStep.EvidenceStandard)));
    }

    private static int ComparePath(
        IReadOnlyList<string> left,
        IReadOnlyList<string> right)
    {
        var length = Math.Min(left.Count, right.Count);
        for (var index = 0; index < length; index++)
        {
            var comparison = string.Compare(
                left[index],
                right[index],
                StringComparison.Ordinal);
            if (comparison != 0)
                return comparison;
        }
        return left.Count.CompareTo(right.Count);
    }

    private static bool TryApply(
        IReadOnlyDictionary<string, string> sourceFrame,
        IReadOnlyDictionary<string, string> resultFrame,
        IReadOnlyDictionary<string, string> currentValues,
        bool requireMonotonicConclusion,
        out IReadOnlyDictionary<string, string> nextValues,
        out IReadOnlyDictionary<string, string> instantiatedConclusions,
        out IReadOnlyList<ReasoningApplicationConflict> conflicts)
    {
        conflicts = [];
        if (!TryBindFrame(sourceFrame, currentValues, null, out var bindings))
        {
            nextValues = currentValues;
            instantiatedConclusions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            return false;
        }

        var conclusions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in resultFrame)
        {
            string value;
            if (IsVariable(item.Value))
            {
                if (!bindings.TryGetValue(item.Value, out value!) || string.IsNullOrWhiteSpace(value))
                {
                    nextValues = currentValues;
                    instantiatedConclusions = conclusions;
                    return false;
                }
            }
            else
            {
                value = item.Value;
            }
            conclusions[item.Key] = value;
        }

        if (requireMonotonicConclusion)
        {
            conflicts = conclusions
                .Where(item => currentValues.TryGetValue(item.Key, out var existing) &&
                    !string.Equals(existing, item.Value, StringComparison.OrdinalIgnoreCase))
                .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .Select(item => new ReasoningApplicationConflict(
                    item.Key,
                    currentValues[item.Key],
                    item.Value))
                .ToArray();
            if (conflicts.Count > 0)
            {
                nextValues = currentValues;
                instantiatedConclusions = conclusions;
                return false;
            }
        }

        var next = Copy(currentValues);
        foreach (var conclusion in conclusions)
            next[conclusion.Key] = conclusion.Value;
        nextValues = next;
        instantiatedConclusions = conclusions;
        return !string.Equals(
            CanonicalState(currentValues),
            CanonicalState(next),
            StringComparison.Ordinal);
    }

    private static bool ViolatesConstraint(
        IReadOnlyDictionary<string, string> values,
        IReadOnlyList<LegendGovernedReasoningRule> constraints)
    {
        foreach (var rule in constraints)
        {
            if (!TryBindFrame(rule.SourceFrame, values, null, out var bindings))
                continue;
            if (TryBindFrame(rule.ResultFrame, values, bindings, out _))
                return true;
        }
        return false;
    }

    private static bool TryBindFrame(
        IReadOnlyDictionary<string, string> frame,
        IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, string>? existingBindings,
        out IReadOnlyDictionary<string, string> bindings)
    {
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        if (existingBindings is not null)
        {
            foreach (var item in existingBindings)
                resolved[item.Key] = item.Value;
        }

        foreach (var item in frame)
        {
            if (!values.TryGetValue(item.Key, out var observed) || string.IsNullOrWhiteSpace(observed))
            {
                bindings = resolved;
                return false;
            }
            if (!IsVariable(item.Value))
            {
                if (!string.Equals(item.Value, observed, StringComparison.OrdinalIgnoreCase))
                {
                    bindings = resolved;
                    return false;
                }
                continue;
            }
            if (resolved.TryGetValue(item.Value, out var existing) &&
                !string.Equals(existing, observed, StringComparison.OrdinalIgnoreCase))
            {
                bindings = resolved;
                return false;
            }
            resolved[item.Value] = observed;
        }

        bindings = resolved;
        return true;
    }

    private static Dictionary<string, string> Copy(IReadOnlyDictionary<string, string> source)
    {
        var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in source)
            copy[item.Key] = item.Value;
        return copy;
    }

    private static string CanonicalState(IReadOnlyDictionary<string, string> values) =>
        string.Join("\u001f", values
            .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Value, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Key.Trim().ToLowerInvariant() + "=" + item.Value.Trim().ToLowerInvariant()));

    private static string CanonicalProofState(
        IReadOnlyDictionary<string, string> values,
        IReadOnlySet<Guid> semanticFamilyIds) =>
        CanonicalState(values) + "\u001e" + string.Join(
            "\u001f",
            semanticFamilyIds.OrderBy(item => item).Select(item => item.ToString("N")));

    private static bool IsVariable(string value) =>
        value.Length > 1 && value[0] == '$';

    private static ReasoningMode? ResolveMode(string? identity)
    {
        var value = identity?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (value == "reasoning.forward" || value.StartsWith("reasoning.forward.", StringComparison.Ordinal))
            return ReasoningMode.Forward;
        if (value == "reasoning.bidirectional" || value.StartsWith("reasoning.bidirectional.", StringComparison.Ordinal))
            return ReasoningMode.Bidirectional;
        if (value == "reasoning.constraint" || value.StartsWith("reasoning.constraint.", StringComparison.Ordinal))
            return ReasoningMode.Constraint;
        if (value == "reasoning.deduction.universal" ||
            value.StartsWith("reasoning.deduction.universal.", StringComparison.Ordinal) ||
            value == "reasoning.deduction.conditional" ||
            value.StartsWith("reasoning.deduction.conditional.", StringComparison.Ordinal))
        {
            return ReasoningMode.Deduction;
        }
        if (value == "reasoning.epistemic.observational-equivalence" ||
            value.StartsWith("reasoning.epistemic.observational-equivalence.", StringComparison.Ordinal))
            return ReasoningMode.ObservationalEquivalence;
        if (value == "reasoning.epistemic.insufficient-evidence" ||
            value.StartsWith("reasoning.epistemic.insufficient-evidence.", StringComparison.Ordinal))
            return ReasoningMode.InsufficientEvidence;
        if (value == "reasoning.epistemic.discriminating-evidence" ||
            value.StartsWith("reasoning.epistemic.discriminating-evidence.", StringComparison.Ordinal))
            return ReasoningMode.DiscriminatingEvidence;
        return null;
    }

    private static bool IsEpistemicMode(ReasoningMode? mode) =>
        mode is ReasoningMode.ObservationalEquivalence or
            ReasoningMode.InsufficientEvidence or
            ReasoningMode.DiscriminatingEvidence;

    private enum ReasoningMode
    {
        Forward,
        Bidirectional,
        Constraint,
        Deduction,
        ObservationalEquivalence,
        InsufficientEvidence,
        DiscriminatingEvidence
    }

    private sealed record DirectionalRule(
        LegendGovernedReasoningRule Rule,
        IReadOnlyDictionary<string, string> SourceFrame,
        IReadOnlyDictionary<string, string> ResultFrame,
        IReadOnlySet<Guid> SourceSemanticFamilyIds,
        IReadOnlySet<Guid> ResultSemanticFamilyIds,
        IReadOnlyList<LegendGovernedReasoningFamilyConnection> FamilyConnections,
        bool Reversed,
        bool IsProofCarrying);

    private sealed record ReasoningApplicationConflict(
        string SemanticDimension,
        string ExistingValue,
        string ProposedValue);

    private sealed record ReasoningConflictContext(
        string Identity,
        LegendGovernedReasoningConflict Conflict,
        LegendGovernedReasoningProof Current,
        LegendGovernedReasoningProofStep ProposedStep,
        IReadOnlySet<Guid> ProposedFamilies);
}

internal sealed record LegendGovernedReasoningFamilyConnection(
    Guid SourceSemanticFamilyId,
    Guid ResultSemanticFamilyId,
    bool HasExplicitGovernedTransfer);

internal sealed record LegendGovernedReasoningRule(
    string TransitionSignature,
    string OperatorIdentity,
    IReadOnlyDictionary<string, string> SourceFrame,
    IReadOnlyDictionary<string, string> ResultFrame,
    int IndependentEvidenceCount,
    int EvidenceStandard,
    IReadOnlySet<Guid> SourceSemanticFamilyIds,
    IReadOnlySet<Guid> ResultSemanticFamilyIds,
    IReadOnlyList<string> IndependentEvidenceIdentities,
    IReadOnlyList<LegendGovernedReasoningFamilyConnection> FamilyConnections);

internal sealed record LegendGovernedReasoningProofStep(
    string TransitionSignature,
    string OperatorIdentity,
    IReadOnlyDictionary<string, string> Premises,
    IReadOnlyDictionary<string, string> Conclusions,
    IReadOnlyList<string> IndependentEvidenceIdentities,
    int IndependentEvidenceCount,
    int EvidenceStandard,
    IReadOnlySet<Guid> SourceSemanticFamilyIds,
    IReadOnlySet<Guid> ResultSemanticFamilyIds,
    bool HasExplicitGovernedTransfer,
    bool Reversed);

internal sealed record LegendGovernedReasoningProof(
    IReadOnlyDictionary<string, string> Values,
    IReadOnlyList<string> TransitionPath,
    IReadOnlyList<LegendGovernedReasoningProofStep> EvidenceLineage,
    IReadOnlySet<Guid> SemanticFamilyIds,
    int Depth,
    int EvidenceCount,
    int EvidenceStandard);

internal enum LegendGovernedReasoningConflictResolution
{
    UnresolvedEqualAuthority,
    UnresolvedWithoutDiscriminatingEvidence,
    ResolvedByHigherStandard
}

internal sealed record LegendGovernedReasoningConflictSide(
    string SemanticValue,
    string TransitionSignature,
    string OperatorIdentity,
    int EvidenceStandard,
    int IndependentEvidenceCount,
    IReadOnlyList<string> IndependentEvidenceIdentities);

internal sealed record LegendGovernedReasoningConflict(
    string SemanticDimension,
    LegendGovernedReasoningConflictSide First,
    LegendGovernedReasoningConflictSide Second,
    LegendGovernedReasoningConflictResolution Resolution,
    LegendGovernedReasoningConflictSide? Selected,
    bool RequiresDiscriminatingEvidence);

internal sealed record LegendGovernedReasoningExecution(
    bool InitialContradiction,
    bool DerivedContradiction,
    bool BudgetExceeded,
    IReadOnlyList<LegendGovernedReasoningProof> DerivedStates,
    IReadOnlyList<LegendGovernedReasoningConflict> Conflicts)
{
    internal static readonly LegendGovernedReasoningExecution Empty = new(false, false, false, [], []);
}
