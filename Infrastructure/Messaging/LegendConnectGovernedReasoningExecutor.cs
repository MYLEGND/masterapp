using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Domain.Messaging;

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
///   reasoning.causal-diagnostic.plan.*              hypotheses -> differing predictions/test
///   reasoning.causal-diagnostic.conclude.*          discriminating observation -> cause
///   reasoning.causal-diagnostic.contradictory-evidence.* incompatible observation -> uncertainty
///   reasoning.causal-diagnostic.resource-limited.*  unavailable discriminator -> uncertainty
///   reasoning.constrained-planning.step.*           admissible action -> next plan cursor
///   reasoning.constrained-planning.block.*          violated constraint -> blocked plan
///   reasoning.constrained-planning.evidence-branch.* governed evidence -> branch cursor
///   reasoning.constrained-planning.stop.*           observed stop condition -> stopped plan
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
    internal const int MaximumPlanningMinutes = 1440;
    internal const int MaximumPlanningResourceUnits = 1024;
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
    internal const string FirstHypothesisDimension = "first_hypothesis";
    internal const string SecondHypothesisDimension = "second_hypothesis";
    internal const string FirstPredictionDimension = "first_prediction";
    internal const string SecondPredictionDimension = "second_prediction";
    internal const string FirstPredictionHypothesisDimension = "first_prediction_hypothesis";
    internal const string SecondPredictionHypothesisDimension = "second_prediction_hypothesis";
    internal const string FirstPredictionEvidenceDimension = "first_prediction_evidence";
    internal const string SecondPredictionEvidenceDimension = "second_prediction_evidence";
    internal const string HypothesisStatusDimension = "hypothesis_status";
    internal const string CompetingHypothesesValue = "competing";
    internal const string PredictionStatusDimension = "prediction_status";
    internal const string DifferingPredictionsValue = "differing";
    internal const string SelectedDiscriminatingEvidenceDimension = "selected_discriminating_evidence";
    internal const string ObservedEvidenceSourceDimension = "observed_evidence_source";
    internal const string ObservedEvidenceDimension = "observed_evidence";
    internal const string DiagnosticPlanStatusDimension = "diagnostic_plan_status";
    internal const string DiscriminatingEvidenceSelectedValue = "discriminating_evidence_selected";
    internal const string DiagnosticConclusionStatusDimension = "diagnostic_conclusion_status";
    internal const string ContradictoryEvidenceValue = "contradictory_evidence";
    internal const string ResourceLimitedValue = "resource_limited";
    internal const string DiagnosticResourceDimension = "diagnostic_resource";
    internal const string DiagnosticResourceStatusDimension = "diagnostic_resource_status";
    internal const string ResourceUnavailableValue = "unavailable";
    internal const string PrematureAttributionStatusDimension = "premature_attribution_status";
    internal const string AttributionWithheldValue = "withheld";
    internal const string CausalAttributionStatusDimension = "causal_attribution_status";
    internal const string AttributionSupportedValue = "supported_by_discriminating_evidence";
    internal const string ReassessHypothesesValue = "reassess_hypotheses";
    internal const string PlanGoalDimension = "plan_goal";
    internal const string CurrentPlanActionDimension = "current_plan_action";
    internal const string CandidatePlanActionDimension = "candidate_plan_action";
    internal const string ActionPrerequisiteDimension = "action_prerequisite";
    internal const string CurrentActionOrderDimension = "current_action_order";
    internal const string CandidateActionOrderDimension = "candidate_action_order";
    internal const string ActionDurationMinutesDimension = "action_duration_minutes";
    internal const string PlanTimeLimitMinutesDimension = "plan_time_limit_minutes";
    internal const string PlanElapsedMinutesDimension = "plan_elapsed_minutes";
    internal const string AvailableResourceUnitsDimension = "available_resource_units";
    internal const string RequiredResourceUnitsDimension = "required_resource_units";
    internal const string SafetyConstraintStatusDimension = "safety_constraint_status";
    internal const string SafetySatisfiedValue = "satisfied";
    internal const string SafetyViolatedValue = "violated";
    internal const string ConstraintContradictionValue = "contradictory";
    internal const string PlanStatusDimension = "plan_status";
    internal const string PlanReadyValue = "ready";
    internal const string PlanInProgressValue = "in_progress";
    internal const string PlanCompletedValue = "completed";
    internal const string PlanBlockedValue = "blocked";
    internal const string PlanStoppedValue = "stopped";
    internal const string PlanBlockReasonDimension = "plan_block_reason";
    internal const string InsufficientResourceValue = "insufficient_resource";
    internal const string UnsafeStepValue = "unsafe_step";
    internal const string TimeLimitExceededValue = "time_limit_exceeded";
    internal const string PrerequisiteOrderViolationValue = "prerequisite_order_violation";
    internal const string ContradictoryConstraintsValue = "contradictory_constraints";
    internal const string UnprovenCausalAssumptionValue = "unproven_causal_assumption";
    internal const string StopConditionDimension = "stop_condition";
    internal const string ObservedStopEvidenceDimension = "observed_stop_evidence";
    internal const string PlanStopReasonDimension = "plan_stop_reason";
    internal const string SelectedStopEvidenceDimension = "selected_stop_evidence";
    internal const string RequiredBranchEvidenceDimension = "required_branch_evidence";
    internal const string ObservedBranchEvidenceDimension = "observed_branch_evidence";
    internal const string SelectedBranchEvidenceDimension = "selected_branch_evidence";
    internal const string EvidenceBranchStatusDimension = "evidence_branch_status";
    internal const string EvidenceBranchSelectedValue = "evidence_branch_selected";

    internal static bool IsExecutableOperatorIdentity(string? identity) =>
        ResolveMode(identity) is not null;

    /// <summary>
    /// Applies the executor's existing fail-closed evidence semantics to one
    /// bounded external research packet. Retrieval rank is intentionally
    /// absent: a fuzzy search score can find a candidate but can never
    /// authorize a claim. Every admissible claim must close a complete
    /// source/document/citation lineage, and any evidence-level contradiction
    /// remains unresolved rather than being silently ranked away.
    /// </summary>
    internal static LegendResearchEvidenceAssessment AssessResearchEvidence(
        IReadOnlyList<LegendConnectResearchSourceIdentity> sources,
        IReadOnlyList<LegendConnectRetrievedDocument> documents,
        IReadOnlyList<LegendConnectCitation> citations,
        IReadOnlyList<LegendConnectClaimEvidence> claims,
        IReadOnlyList<LegendConnectContradictingEvidence> contradictions,
        int minimumIndependentSources)
    {
        var boundedMinimum = Math.Clamp(minimumIndependentSources, 1, 3);
        var sourceById = sources
            .Where(item => !string.IsNullOrWhiteSpace(item.SourceIdentity))
            .GroupBy(item => item.SourceIdentity, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
        var documentById = documents
            .Where(item => item.RetrievalSucceeded && !string.IsNullOrWhiteSpace(item.DocumentIdentity))
            .GroupBy(item => item.DocumentIdentity, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
        var citationById = citations
            .Where(item => !string.IsNullOrWhiteSpace(item.CitationIdentity))
            .GroupBy(item => item.CitationIdentity, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);

        bool HasCompleteLineage(
            string sourceIdentity,
            string documentIdentity,
            string citationIdentity)
        {
            if (!sourceById.TryGetValue(sourceIdentity, out var source) ||
                !documentById.TryGetValue(documentIdentity, out var document) ||
                !citationById.TryGetValue(citationIdentity, out var citation))
            {
                return false;
            }

            return string.Equals(document.SourceIdentity, sourceIdentity, StringComparison.Ordinal) &&
                   string.Equals(citation.SourceIdentity, sourceIdentity, StringComparison.Ordinal) &&
                   string.Equals(citation.DocumentIdentity, documentIdentity, StringComparison.Ordinal) &&
                   string.Equals(document.CanonicalUri, source.CanonicalUri, StringComparison.Ordinal) &&
                   string.Equals(citation.CanonicalUri, source.CanonicalUri, StringComparison.Ordinal);
        }

        var admissibleClaims = claims
            .Where(item =>
                !string.IsNullOrWhiteSpace(item.EvidenceIdentity) &&
                !string.IsNullOrWhiteSpace(item.ClaimIdentity) &&
                !string.IsNullOrWhiteSpace(item.Statement) &&
                HasCompleteLineage(
                    item.SourceIdentity,
                    item.DocumentIdentity,
                    item.CitationIdentity))
            .GroupBy(item => item.EvidenceIdentity, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .Select(group => group.Single())
            .OrderBy(item => item.ClaimIdentity, StringComparer.Ordinal)
            .ThenBy(item => item.SourceIdentity, StringComparer.Ordinal)
            .ToArray();
        var admissibleContradictions = contradictions
            .Where(item =>
                !string.IsNullOrWhiteSpace(item.EvidenceIdentity) &&
                !string.IsNullOrWhiteSpace(item.ClaimIdentity) &&
                !string.IsNullOrWhiteSpace(item.Statement) &&
                HasCompleteLineage(
                    item.SourceIdentity,
                    item.DocumentIdentity,
                    item.CitationIdentity))
            .GroupBy(item => item.EvidenceIdentity, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .Select(group => group.Single())
            .OrderBy(item => item.ClaimIdentity, StringComparer.Ordinal)
            .ThenBy(item => item.SourceIdentity, StringComparer.Ordinal)
            .ToArray();

        var explicitConflictingClaimIdentities = admissibleContradictions
            .Select(item => item.ClaimIdentity)
            .Intersect(
                admissibleClaims.Select(item => item.ClaimIdentity),
                StringComparer.Ordinal)
            .ToArray();
        var implicitConflictingClaimIdentities = admissibleClaims
            .GroupBy(item => item.ClaimIdentity, StringComparer.Ordinal)
            .Where(group => group
                .Select(item => item.Statement.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() > 1)
            .Select(group => group.Key);
        var conflictingClaimIdentities = explicitConflictingClaimIdentities
            .Concat(implicitConflictingClaimIdentities)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();
        if (conflictingClaimIdentities.Length > 0)
        {
            return new LegendResearchEvidenceAssessment(
                LegendResearchEvidenceAssessmentState.UnresolvedConflict,
                admissibleClaims,
                admissibleContradictions,
                admissibleClaims.Select(item => item.SourceIdentity)
                    .Concat(admissibleContradictions.Select(item => item.SourceIdentity))
                    .Distinct(StringComparer.Ordinal)
                    .Count(),
                boundedMinimum,
                "research_evidence_conflict_unresolved");
        }

        var supportedClaims = admissibleClaims
            .GroupBy(item => item.ClaimIdentity, StringComparer.Ordinal)
            .Where(group =>
                group.Select(item => item.SourceIdentity)
                    .Distinct(StringComparer.Ordinal)
                    .Count() >= boundedMinimum)
            .SelectMany(group => group)
            .ToArray();
        var independentSources = supportedClaims
            .Select(item => item.SourceIdentity)
            .Distinct(StringComparer.Ordinal)
            .Count();
        if (supportedClaims.Length == 0)
        {
            return new LegendResearchEvidenceAssessment(
                LegendResearchEvidenceAssessmentState.InsufficientEvidence,
                admissibleClaims,
                admissibleContradictions,
                admissibleClaims.Select(item => item.SourceIdentity)
                    .Distinct(StringComparer.Ordinal)
                    .Count(),
                boundedMinimum,
                admissibleClaims.Length == 0
                    ? "research_claim_lineage_incomplete"
                    : "research_independent_sources_insufficient");
        }

        return new LegendResearchEvidenceAssessment(
            LegendResearchEvidenceAssessmentState.Conclusion,
            supportedClaims,
            admissibleContradictions,
            independentSources,
            boundedMinimum,
            "research_claims_governed");
    }

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
                    false,
                    ReasoningMode.Forward));
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
                    false,
                    ReasoningMode.Bidirectional));
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
                    false,
                    ReasoningMode.Bidirectional));
            }
            else if (mode == ReasoningMode.Deduction ||
                     IsEpistemicMode(mode) ||
                     IsCausalDiagnosticMode(mode) ||
                     IsConstrainedPlanningMode(mode))
            {
                // Governed deductive, epistemic, causal-diagnostic, and
                // constrained-planning rules are implications, never
                // equivalences. No reverse rule is created, so a conclusion
                // cannot manufacture its premises.
                directional.Add(new DirectionalRule(
                    rule,
                    rule.SourceFrame,
                    rule.ResultFrame,
                    rule.SourceSemanticFamilyIds,
                    rule.ResultSemanticFamilyIds,
                    rule.FamilyConnections,
                    false,
                    true,
                    mode!.Value));
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
                        rule.Mode,
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
        if (IsCausalDiagnosticMode(mode) &&
            !IsGovernedCausalDiagnosticRule(rule, mode!.Value))
        {
            return false;
        }
        if (IsConstrainedPlanningMode(mode) &&
            !IsGovernedConstrainedPlanningRule(rule, mode!.Value))
        {
            return false;
        }

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
        return false;
    }

    private static bool IsGovernedCausalDiagnosticRule(
        LegendGovernedReasoningRule rule,
        ReasoningMode mode)
    {
        if (!HasSemanticDimensions(
                rule.SourceFrame,
                FirstHypothesisDimension,
                SecondHypothesisDimension,
                FirstPredictionDimension,
                SecondPredictionDimension,
                FirstPredictionHypothesisDimension,
                SecondPredictionHypothesisDimension,
                FirstPredictionEvidenceDimension,
                SecondPredictionEvidenceDimension) ||
            !HasSemanticFlow(
                rule.SourceFrame,
                FirstHypothesisDimension,
                rule.SourceFrame,
                FirstPredictionHypothesisDimension) ||
            !HasSemanticFlow(
                rule.SourceFrame,
                SecondHypothesisDimension,
                rule.SourceFrame,
                SecondPredictionHypothesisDimension))
        {
            return false;
        }

        if (mode == ReasoningMode.CausalDiagnosticPlan)
        {
            return HasSemanticDimensions(rule.SourceFrame, DiscriminatingEvidenceDimension) &&
                   HasPredictionEvidenceFlow(
                       rule.SourceFrame,
                       DiscriminatingEvidenceDimension) &&
                   HasSemanticValue(
                       rule.ResultFrame,
                       HypothesisStatusDimension,
                       CompetingHypothesesValue) &&
                   HasSemanticValue(
                       rule.ResultFrame,
                       PredictionStatusDimension,
                       DifferingPredictionsValue) &&
                   HasSemanticValue(
                       rule.ResultFrame,
                       DiagnosticPlanStatusDimension,
                       DiscriminatingEvidenceSelectedValue) &&
                   HasSemanticValue(
                       rule.ResultFrame,
                       PrematureAttributionStatusDimension,
                       AttributionWithheldValue) &&
                   HasSemanticFlow(
                       rule.SourceFrame,
                       DiscriminatingEvidenceDimension,
                       rule.ResultFrame,
                       SelectedDiscriminatingEvidenceDimension) &&
                   !rule.ResultFrame.ContainsKey(CauseSelectionDimension);
        }

        if (mode == ReasoningMode.CausalDiagnosticConclusion)
        {
            return HasSemanticDimensions(
                       rule.SourceFrame,
                       SelectedDiscriminatingEvidenceDimension,
                       ObservedEvidenceSourceDimension,
                       ObservedEvidenceDimension) &&
                   HasPredictionEvidenceFlow(
                       rule.SourceFrame,
                       SelectedDiscriminatingEvidenceDimension) &&
                   HasSemanticValue(
                       rule.SourceFrame,
                       PredictionStatusDimension,
                       DifferingPredictionsValue) &&
                   HasSemanticFlow(
                       rule.SourceFrame,
                       SelectedDiscriminatingEvidenceDimension,
                       rule.SourceFrame,
                       ObservedEvidenceSourceDimension) &&
                   HasSemanticValue(
                       rule.ResultFrame,
                       DiagnosticConclusionStatusDimension,
                       ResolvedByDiscriminatingEvidenceValue) &&
                   HasSemanticValue(
                       rule.ResultFrame,
                       CausalAttributionStatusDimension,
                       AttributionSupportedValue) &&
                   HasOneHypothesisSelectionFlow(rule);
        }

        if (mode == ReasoningMode.CausalDiagnosticContradictoryEvidence)
        {
            return HasSemanticDimensions(
                       rule.SourceFrame,
                       SelectedDiscriminatingEvidenceDimension,
                       ObservedEvidenceSourceDimension,
                       ObservedEvidenceDimension) &&
                   HasPredictionEvidenceFlow(
                       rule.SourceFrame,
                       SelectedDiscriminatingEvidenceDimension) &&
                   HasSemanticValue(
                       rule.SourceFrame,
                       PredictionStatusDimension,
                       DifferingPredictionsValue) &&
                   HasSemanticFlow(
                       rule.SourceFrame,
                       SelectedDiscriminatingEvidenceDimension,
                       rule.SourceFrame,
                       ObservedEvidenceSourceDimension) &&
                   HasSemanticValue(
                       rule.ResultFrame,
                       DiagnosticConclusionStatusDimension,
                       ContradictoryEvidenceValue) &&
                   IsGovernedNonSelectionResult(
                       rule.ResultFrame,
                       ReassessHypothesesValue,
                       includeUnresolvedContradiction: true);
        }

        if (mode == ReasoningMode.CausalDiagnosticResourceLimited)
        {
            return HasSemanticDimensions(
                       rule.SourceFrame,
                       SelectedDiscriminatingEvidenceDimension,
                       DiagnosticResourceDimension) &&
                   HasPredictionEvidenceFlow(
                       rule.SourceFrame,
                       SelectedDiscriminatingEvidenceDimension) &&
                   HasSemanticValue(
                       rule.SourceFrame,
                       PredictionStatusDimension,
                       DifferingPredictionsValue) &&
                   HasSemanticValue(
                       rule.SourceFrame,
                       DiagnosticResourceStatusDimension,
                       ResourceUnavailableValue) &&
                   HasSemanticFlow(
                       rule.SourceFrame,
                       SelectedDiscriminatingEvidenceDimension,
                       rule.SourceFrame,
                       DiagnosticResourceDimension) &&
                   HasSemanticValue(
                       rule.ResultFrame,
                       DiagnosticConclusionStatusDimension,
                       ResourceLimitedValue) &&
                   IsGovernedNonSelectionResult(
                       rule.ResultFrame,
                       DiscriminatingEvidenceValue,
                       includeUnresolvedContradiction: false);
        }

        return false;
    }

    private static bool HasPredictionEvidenceFlow(
        IReadOnlyDictionary<string, string> sourceFrame,
        string evidenceDimension) =>
        HasSemanticFlow(
            sourceFrame,
            evidenceDimension,
            sourceFrame,
            FirstPredictionEvidenceDimension) &&
        HasSemanticFlow(
            sourceFrame,
            evidenceDimension,
            sourceFrame,
            SecondPredictionEvidenceDimension);

    private static bool IsGovernedConstrainedPlanningRule(
        LegendGovernedReasoningRule rule,
        ReasoningMode mode)
    {
        if (rule.SourceFrame.ContainsKey(CauseSelectionDimension) ||
            rule.ResultFrame.ContainsKey(CauseSelectionDimension))
        {
            return false;
        }

        if (mode is ReasoningMode.ConstrainedPlanningStep or
            ReasoningMode.ConstrainedPlanningBlock)
        {
            if (!HasPlanningActionSource(rule.SourceFrame))
                return false;
        }

        if (mode == ReasoningMode.ConstrainedPlanningStep)
        {
            var continues = HasSemanticValue(
                rule.ResultFrame,
                PlanStatusDimension,
                PlanInProgressValue);
            var completes = HasSemanticValue(
                rule.ResultFrame,
                PlanStatusDimension,
                PlanCompletedValue);
            if (continues == completes ||
                !HasSemanticDimensions(rule.ResultFrame, PlanElapsedMinutesDimension) ||
                !HasSemanticFlow(
                    rule.SourceFrame,
                    CandidatePlanActionDimension,
                    rule.ResultFrame,
                    CurrentPlanActionDimension) ||
                !HasSemanticFlow(
                    rule.SourceFrame,
                    CandidateActionOrderDimension,
                    rule.ResultFrame,
                    CurrentActionOrderDimension))
            {
                return false;
            }

            return continues
                ? HasNextPlanningCandidate(rule.ResultFrame)
                : !ContainsAnyPlanningCandidateDimension(rule.ResultFrame);
        }

        if (mode == ReasoningMode.ConstrainedPlanningBlock)
        {
            return HasSemanticValue(
                       rule.ResultFrame,
                       PlanStatusDimension,
                       PlanBlockedValue) &&
                   rule.ResultFrame.TryGetValue(PlanBlockReasonDimension, out var reason) &&
                   IsKnownPlanningBlockReason(reason) &&
                   !rule.ResultFrame.ContainsKey(CurrentPlanActionDimension) &&
                   !rule.ResultFrame.ContainsKey(CurrentActionOrderDimension) &&
                   !rule.ResultFrame.ContainsKey(PlanElapsedMinutesDimension) &&
                   !ContainsAnyPlanningCandidateDimension(rule.ResultFrame);
        }

        if (mode == ReasoningMode.ConstrainedPlanningEvidenceBranch)
        {
            return HasSemanticDimensions(
                       rule.SourceFrame,
                       PlanGoalDimension,
                       PlanStatusDimension,
                       CurrentPlanActionDimension,
                       CandidatePlanActionDimension,
                       ActionPrerequisiteDimension,
                       CandidateActionOrderDimension,
                       ActionDurationMinutesDimension,
                       RequiredResourceUnitsDimension,
                       SafetyConstraintStatusDimension,
                       RequiredBranchEvidenceDimension,
                       ObservedBranchEvidenceDimension) &&
                   HasSemanticFlow(
                       rule.SourceFrame,
                       RequiredBranchEvidenceDimension,
                       rule.SourceFrame,
                       ObservedBranchEvidenceDimension) &&
                   HasSemanticFlow(
                       rule.SourceFrame,
                       CurrentPlanActionDimension,
                       rule.SourceFrame,
                       ActionPrerequisiteDimension) &&
                   HasSemanticFlow(
                       rule.SourceFrame,
                       CurrentPlanActionDimension,
                       rule.ResultFrame,
                       CurrentPlanActionDimension) &&
                   HasNextPlanningCandidate(rule.ResultFrame) &&
                   HasSemanticValue(
                       rule.ResultFrame,
                       EvidenceBranchStatusDimension,
                       EvidenceBranchSelectedValue) &&
                   !rule.ResultFrame.ContainsKey(PlanStatusDimension) &&
                   !rule.ResultFrame.ContainsKey(CurrentActionOrderDimension) &&
                   !rule.ResultFrame.ContainsKey(PlanElapsedMinutesDimension) &&
                   HasSemanticFlow(
                       rule.SourceFrame,
                       ObservedBranchEvidenceDimension,
                       rule.ResultFrame,
                       SelectedBranchEvidenceDimension);
        }

        if (mode == ReasoningMode.ConstrainedPlanningStop)
        {
            return HasSemanticDimensions(
                       rule.SourceFrame,
                       PlanGoalDimension,
                       PlanStatusDimension,
                       CurrentPlanActionDimension,
                       StopConditionDimension,
                       ObservedStopEvidenceDimension) &&
                   HasSemanticFlow(
                       rule.SourceFrame,
                       StopConditionDimension,
                       rule.SourceFrame,
                       ObservedStopEvidenceDimension) &&
                   HasSemanticValue(
                       rule.ResultFrame,
                       PlanStatusDimension,
                       PlanStoppedValue) &&
                   HasSemanticFlow(
                       rule.SourceFrame,
                       StopConditionDimension,
                       rule.ResultFrame,
                       PlanStopReasonDimension) &&
                   HasSemanticFlow(
                       rule.SourceFrame,
                       ObservedStopEvidenceDimension,
                       rule.ResultFrame,
                       SelectedStopEvidenceDimension) &&
                   !rule.ResultFrame.ContainsKey(CurrentPlanActionDimension) &&
                   !rule.ResultFrame.ContainsKey(CurrentActionOrderDimension) &&
                   !rule.ResultFrame.ContainsKey(PlanElapsedMinutesDimension) &&
                   !ContainsAnyPlanningCandidateDimension(rule.ResultFrame);
        }

        return false;
    }

    private static bool HasPlanningActionSource(IReadOnlyDictionary<string, string> sourceFrame) =>
        HasSemanticDimensions(
            sourceFrame,
            PlanGoalDimension,
            CandidatePlanActionDimension,
            ActionPrerequisiteDimension,
            CurrentActionOrderDimension,
            CandidateActionOrderDimension,
            ActionDurationMinutesDimension,
            PlanTimeLimitMinutesDimension,
            PlanElapsedMinutesDimension,
            AvailableResourceUnitsDimension,
            RequiredResourceUnitsDimension,
            SafetyConstraintStatusDimension,
            PlanStatusDimension);

    private static bool HasNextPlanningCandidate(IReadOnlyDictionary<string, string> frame) =>
        HasSemanticDimensions(
            frame,
            CandidatePlanActionDimension,
            ActionPrerequisiteDimension,
            CandidateActionOrderDimension,
            ActionDurationMinutesDimension,
            RequiredResourceUnitsDimension,
            SafetyConstraintStatusDimension) &&
        HasSemanticFlow(
            frame,
            CurrentPlanActionDimension,
            frame,
            ActionPrerequisiteDimension);

    private static bool ContainsAnyPlanningCandidateDimension(
        IReadOnlyDictionary<string, string> frame) =>
        frame.ContainsKey(CandidatePlanActionDimension) ||
        frame.ContainsKey(ActionPrerequisiteDimension) ||
        frame.ContainsKey(CandidateActionOrderDimension) ||
        frame.ContainsKey(ActionDurationMinutesDimension) ||
        frame.ContainsKey(RequiredResourceUnitsDimension) ||
        frame.ContainsKey(SafetyConstraintStatusDimension);

    private static bool IsKnownPlanningBlockReason(string value) =>
        value is InsufficientResourceValue or
            UnsafeStepValue or
            TimeLimitExceededValue or
            PrerequisiteOrderViolationValue or
            ContradictoryConstraintsValue or
            UnprovenCausalAssumptionValue;

    private static bool IsGovernedNonSelectionResult(
        IReadOnlyDictionary<string, string> resultFrame,
        string evidenceRequirement,
        bool includeUnresolvedContradiction) =>
        HasSemanticValue(
            resultFrame,
            HypothesisStatusDimension,
            CompetingHypothesesValue) &&
        HasSemanticValue(
            resultFrame,
            CauseSelectionDimension,
            UndeterminedValue) &&
        HasSemanticValue(
            resultFrame,
            PrematureAttributionStatusDimension,
            AttributionWithheldValue) &&
        HasSemanticValue(
            resultFrame,
            EvidenceRequirementDimension,
            evidenceRequirement) &&
        (!includeUnresolvedContradiction || HasSemanticValue(
            resultFrame,
            EpistemicStatusDimension,
            UnresolvedContradictionValue));

    private static bool HasOneHypothesisSelectionFlow(LegendGovernedReasoningRule rule) =>
        HasSemanticFlow(
            rule.SourceFrame,
            FirstHypothesisDimension,
            rule.ResultFrame,
            CauseSelectionDimension) ^
        HasSemanticFlow(
            rule.SourceFrame,
            SecondHypothesisDimension,
            rule.ResultFrame,
            CauseSelectionDimension);

    private static bool HasSemanticDimensions(
        IReadOnlyDictionary<string, string> frame,
        params string[] dimensions) =>
        dimensions.All(dimension => frame.TryGetValue(dimension, out var value) &&
            !string.IsNullOrWhiteSpace(value));

    private static bool HasSemanticFlow(
        IReadOnlyDictionary<string, string> sourceFrame,
        string sourceDimension,
        IReadOnlyDictionary<string, string> resultFrame,
        string resultDimension) =>
        sourceFrame.TryGetValue(sourceDimension, out var sourceValue) &&
        resultFrame.TryGetValue(resultDimension, out var resultValue) &&
        !string.IsNullOrWhiteSpace(sourceValue) &&
        string.Equals(sourceValue, resultValue, StringComparison.OrdinalIgnoreCase);

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
            (IsNonDispositiveOperator(existing.OperatorIdentity) &&
             !IsDiscriminatingEvidenceOperator(proposed.OperatorIdentity)) ||
            (IsNonDispositiveOperator(proposed.OperatorIdentity) &&
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

    private static bool IsNonDispositiveOperator(string identity) =>
        ResolveMode(identity) is ReasoningMode.ObservationalEquivalence or
            ReasoningMode.InsufficientEvidence or
            ReasoningMode.CausalDiagnosticContradictoryEvidence or
            ReasoningMode.CausalDiagnosticResourceLimited;

    private static bool IsDiscriminatingEvidenceOperator(string identity) =>
        ResolveMode(identity) == ReasoningMode.CausalDiagnosticConclusion;

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
        ReasoningMode mode,
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

        if (IsCausalDiagnosticMode(mode) &&
            !IsValidCausalDiagnosticApplication(mode, currentValues, conclusions))
        {
            nextValues = currentValues;
            instantiatedConclusions = conclusions;
            return false;
        }

        if (IsConstrainedPlanningMode(mode) &&
            !IsValidConstrainedPlanningApplication(mode, currentValues, conclusions))
        {
            nextValues = currentValues;
            instantiatedConclusions = conclusions;
            return false;
        }

        if (requireMonotonicConclusion)
        {
            // Planning is the sole bounded state-machine exception to the
            // executor's monotonic conclusion rule. The planning guard above
            // has already proved that each permitted replacement consumes
            // the current cursor, advances exactly one order, and preserves
            // all other governed facts and proof lineage.
            conflicts = conclusions
                .Where(item => currentValues.TryGetValue(item.Key, out var existing) &&
                    !string.Equals(existing, item.Value, StringComparison.OrdinalIgnoreCase) &&
                    !CanReplaceConstrainedPlanningState(mode, item.Key))
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

    private static bool IsValidCausalDiagnosticApplication(
        ReasoningMode mode,
        IReadOnlyDictionary<string, string> currentValues,
        IReadOnlyDictionary<string, string> conclusions)
    {
        if (!TryGetSemanticValue(currentValues, FirstHypothesisDimension, out var firstHypothesis) ||
            !TryGetSemanticValue(currentValues, SecondHypothesisDimension, out var secondHypothesis) ||
            !TryGetSemanticValue(currentValues, FirstPredictionDimension, out var firstPrediction) ||
            !TryGetSemanticValue(currentValues, SecondPredictionDimension, out var secondPrediction) ||
            !HasMatchingCurrentValues(
                currentValues,
                FirstHypothesisDimension,
                FirstPredictionHypothesisDimension) ||
            !HasMatchingCurrentValues(
                currentValues,
                SecondHypothesisDimension,
                SecondPredictionHypothesisDimension) ||
            string.Equals(firstHypothesis, secondHypothesis, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var predictionsDiffer = !string.Equals(
            firstPrediction,
            secondPrediction,
            StringComparison.OrdinalIgnoreCase);
        if (!predictionsDiffer)
            return false;

        if (mode == ReasoningMode.CausalDiagnosticPlan)
        {
            return HasMatchingCurrentValues(
                       currentValues,
                       DiscriminatingEvidenceDimension,
                       FirstPredictionEvidenceDimension) &&
                   HasMatchingCurrentValues(
                       currentValues,
                       DiscriminatingEvidenceDimension,
                       SecondPredictionEvidenceDimension);
        }

        if (!HasMatchingCurrentValues(
                currentValues,
                SelectedDiscriminatingEvidenceDimension,
                FirstPredictionEvidenceDimension) ||
            !HasMatchingCurrentValues(
                currentValues,
                SelectedDiscriminatingEvidenceDimension,
                SecondPredictionEvidenceDimension))
        {
            return false;
        }

        if (mode == ReasoningMode.CausalDiagnosticResourceLimited)
        {
            return HasMatchingCurrentValues(
                       currentValues,
                       SelectedDiscriminatingEvidenceDimension,
                       DiagnosticResourceDimension) &&
                   HasSemanticValue(
                       currentValues,
                       DiagnosticResourceStatusDimension,
                       ResourceUnavailableValue);
        }

        if (!HasMatchingCurrentValues(
                currentValues,
                SelectedDiscriminatingEvidenceDimension,
                ObservedEvidenceSourceDimension) ||
            !TryGetSemanticValue(currentValues, ObservedEvidenceDimension, out var observed))
        {
            return false;
        }

        var supportsFirst = string.Equals(
            observed,
            firstPrediction,
            StringComparison.OrdinalIgnoreCase);
        var supportsSecond = string.Equals(
            observed,
            secondPrediction,
            StringComparison.OrdinalIgnoreCase);
        if (mode == ReasoningMode.CausalDiagnosticContradictoryEvidence)
            return !supportsFirst && !supportsSecond;
        if (mode != ReasoningMode.CausalDiagnosticConclusion || supportsFirst == supportsSecond ||
            !TryGetSemanticValue(conclusions, CauseSelectionDimension, out var selected))
        {
            return false;
        }

        return string.Equals(
            selected,
            supportsFirst ? firstHypothesis : secondHypothesis,
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasMatchingCurrentValues(
        IReadOnlyDictionary<string, string> values,
        string firstDimension,
        string secondDimension) =>
        TryGetSemanticValue(values, firstDimension, out var first) &&
        TryGetSemanticValue(values, secondDimension, out var second) &&
        string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

    private static bool TryGetSemanticValue(
        IReadOnlyDictionary<string, string> values,
        string dimension,
        out string value)
    {
        if (values.TryGetValue(dimension, out value!) && !string.IsNullOrWhiteSpace(value))
            return true;
        value = string.Empty;
        return false;
    }

    private static bool IsValidConstrainedPlanningApplication(
        ReasoningMode mode,
        IReadOnlyDictionary<string, string> currentValues,
        IReadOnlyDictionary<string, string> conclusions)
    {
        if (mode == ReasoningMode.ConstrainedPlanningStop)
            return IsValidPlanningStop(currentValues, conclusions);

        if (!IsActivePlan(currentValues) || HasSatisfiedStopCondition(currentValues))
            return false;

        if (mode == ReasoningMode.ConstrainedPlanningEvidenceBranch)
            return IsValidPlanningEvidenceBranch(currentValues, conclusions);

        if (!TryReadPlanningCandidate(currentValues, out var candidate))
            return false;

        var blockReason = DeterminePlanningBlockReason(currentValues, candidate);
        if (mode == ReasoningMode.ConstrainedPlanningBlock)
        {
            return blockReason is not null &&
                   HasSemanticValue(conclusions, PlanStatusDimension, PlanBlockedValue) &&
                   HasSemanticValue(conclusions, PlanBlockReasonDimension, blockReason);
        }

        if (mode != ReasoningMode.ConstrainedPlanningStep || blockReason is not null ||
            !TryGetSemanticValue(
                conclusions,
                CurrentPlanActionDimension,
                out var selectedAction) ||
            !string.Equals(
                selectedAction,
                candidate.CandidateAction,
                StringComparison.OrdinalIgnoreCase) ||
            !TryGetBoundedPlanningInteger(
                conclusions,
                CurrentActionOrderDimension,
                0,
                MaximumDepth,
                out var selectedOrder) ||
            selectedOrder != candidate.CandidateOrder ||
            !TryGetBoundedPlanningInteger(
                conclusions,
                PlanElapsedMinutesDimension,
                0,
                MaximumPlanningMinutes,
                out var resultingElapsed) ||
            resultingElapsed != candidate.ElapsedMinutes + candidate.DurationMinutes)
        {
            return false;
        }

        if (HasSemanticValue(conclusions, PlanStatusDimension, PlanCompletedValue))
            return true;
        if (!HasSemanticValue(conclusions, PlanStatusDimension, PlanInProgressValue))
            return false;

        return TryGetSemanticValue(
                   conclusions,
                   CandidatePlanActionDimension,
                   out var nextAction) &&
               !string.Equals(
                   nextAction,
                   candidate.CandidateAction,
                   StringComparison.OrdinalIgnoreCase) &&
               HasSemanticValue(
                   conclusions,
                   ActionPrerequisiteDimension,
                   candidate.CandidateAction) &&
               TryGetBoundedPlanningInteger(
                   conclusions,
                   CandidateActionOrderDimension,
                   1,
                   MaximumDepth,
                   out var nextOrder) &&
               nextOrder == candidate.CandidateOrder + 1 &&
               TryGetBoundedPlanningInteger(
                   conclusions,
                   ActionDurationMinutesDimension,
                   1,
                   MaximumPlanningMinutes,
                   out _) &&
               TryGetBoundedPlanningInteger(
                   conclusions,
                   RequiredResourceUnitsDimension,
                   1,
                   MaximumPlanningResourceUnits,
                   out _) &&
               TryGetSemanticValue(
                   conclusions,
                   SafetyConstraintStatusDimension,
                   out var nextSafety) &&
               IsKnownSafetyConstraintStatus(nextSafety);
    }

    private static bool IsValidPlanningEvidenceBranch(
        IReadOnlyDictionary<string, string> currentValues,
        IReadOnlyDictionary<string, string> conclusions)
    {
        if (HasUnsupportedCausalAssumption(currentValues) ||
            !TryGetSemanticValue(
                currentValues,
                CurrentPlanActionDimension,
                out var currentAction) ||
            !TryGetSemanticValue(
                currentValues,
                CandidatePlanActionDimension,
                out var pendingBranch) ||
            !TryGetSemanticValue(
                currentValues,
                RequiredBranchEvidenceDimension,
                out var requiredEvidence) ||
            !TryGetSemanticValue(
                currentValues,
                ObservedBranchEvidenceDimension,
                out var observedEvidence) ||
            !string.Equals(requiredEvidence, observedEvidence, StringComparison.OrdinalIgnoreCase) ||
            !HasSemanticValue(conclusions, CurrentPlanActionDimension, currentAction) ||
            !TryGetSemanticValue(
                conclusions,
                CandidatePlanActionDimension,
                out var selectedBranch) ||
            string.Equals(selectedBranch, pendingBranch, StringComparison.OrdinalIgnoreCase) ||
            !HasSemanticValue(conclusions, ActionPrerequisiteDimension, currentAction) ||
            !TryGetBoundedPlanningInteger(
                currentValues,
                CurrentActionOrderDimension,
                0,
                MaximumDepth - 1,
                out var currentOrder) ||
            !TryGetBoundedPlanningInteger(
                conclusions,
                CandidateActionOrderDimension,
                1,
                MaximumDepth,
                out var branchOrder) ||
            branchOrder != currentOrder + 1 ||
            !TryGetBoundedPlanningInteger(
                conclusions,
                ActionDurationMinutesDimension,
                1,
                MaximumPlanningMinutes,
                out _) ||
            !TryGetBoundedPlanningInteger(
                conclusions,
                RequiredResourceUnitsDimension,
                1,
                MaximumPlanningResourceUnits,
                out _) ||
            !TryGetSemanticValue(
                conclusions,
                SafetyConstraintStatusDimension,
                out var safetyStatus) ||
            !IsKnownSafetyConstraintStatus(safetyStatus))
        {
            return false;
        }

        return HasSemanticValue(
                   conclusions,
                   SelectedBranchEvidenceDimension,
                   observedEvidence) &&
               HasSemanticValue(
                   conclusions,
                   EvidenceBranchStatusDimension,
                   EvidenceBranchSelectedValue);
    }

    private static bool IsValidPlanningStop(
        IReadOnlyDictionary<string, string> currentValues,
        IReadOnlyDictionary<string, string> conclusions)
    {
        if (!IsActivePlan(currentValues) ||
            !TryGetSemanticValue(
                currentValues,
                StopConditionDimension,
                out var stopCondition) ||
            !TryGetSemanticValue(
                currentValues,
                ObservedStopEvidenceDimension,
                out var observedStopEvidence) ||
            !string.Equals(
                stopCondition,
                observedStopEvidence,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return HasSemanticValue(conclusions, PlanStatusDimension, PlanStoppedValue) &&
               HasSemanticValue(conclusions, PlanStopReasonDimension, stopCondition) &&
               HasSemanticValue(
                   conclusions,
                   SelectedStopEvidenceDimension,
                   observedStopEvidence);
    }

    private static bool TryReadPlanningCandidate(
        IReadOnlyDictionary<string, string> values,
        out PlanningCandidateState candidate)
    {
        candidate = default!;
        if (!TryGetSemanticValue(values, CurrentPlanActionDimension, out var currentAction) ||
            !TryGetSemanticValue(values, CandidatePlanActionDimension, out var candidateAction) ||
            !TryGetSemanticValue(values, ActionPrerequisiteDimension, out var prerequisite) ||
            !TryGetBoundedPlanningInteger(
                values,
                CurrentActionOrderDimension,
                0,
                MaximumDepth,
                out var currentOrder) ||
            !TryGetBoundedPlanningInteger(
                values,
                CandidateActionOrderDimension,
                0,
                MaximumDepth,
                out var candidateOrder) ||
            !TryGetBoundedPlanningInteger(
                values,
                ActionDurationMinutesDimension,
                1,
                MaximumPlanningMinutes,
                out var durationMinutes) ||
            !TryGetBoundedPlanningInteger(
                values,
                PlanTimeLimitMinutesDimension,
                1,
                MaximumPlanningMinutes,
                out var timeLimitMinutes) ||
            !TryGetBoundedPlanningInteger(
                values,
                PlanElapsedMinutesDimension,
                0,
                MaximumPlanningMinutes,
                out var elapsedMinutes) ||
            !TryGetBoundedPlanningInteger(
                values,
                AvailableResourceUnitsDimension,
                0,
                MaximumPlanningResourceUnits,
                out var availableResources) ||
            !TryGetBoundedPlanningInteger(
                values,
                RequiredResourceUnitsDimension,
                1,
                MaximumPlanningResourceUnits,
                out var requiredResources) ||
            !TryGetSemanticValue(
                values,
                SafetyConstraintStatusDimension,
                out var safetyStatus) ||
            !IsKnownSafetyConstraintStatus(safetyStatus))
        {
            return false;
        }

        candidate = new PlanningCandidateState(
            currentAction,
            candidateAction,
            prerequisite,
            currentOrder,
            candidateOrder,
            durationMinutes,
            timeLimitMinutes,
            elapsedMinutes,
            availableResources,
            requiredResources,
            safetyStatus);
        return true;
    }

    private static string? DeterminePlanningBlockReason(
        IReadOnlyDictionary<string, string> values,
        PlanningCandidateState candidate)
    {
        if (HasUnsupportedCausalAssumption(values))
            return UnprovenCausalAssumptionValue;
        if (string.Equals(
                candidate.SafetyStatus,
                ConstraintContradictionValue,
                StringComparison.OrdinalIgnoreCase))
        {
            return ContradictoryConstraintsValue;
        }
        if (!string.Equals(
                candidate.Prerequisite,
                candidate.CurrentAction,
                StringComparison.OrdinalIgnoreCase) ||
            candidate.CandidateOrder != candidate.CurrentOrder + 1)
        {
            return PrerequisiteOrderViolationValue;
        }
        if (string.Equals(
                candidate.SafetyStatus,
                SafetyViolatedValue,
                StringComparison.OrdinalIgnoreCase))
        {
            return UnsafeStepValue;
        }
        if (candidate.RequiredResources > candidate.AvailableResources)
            return InsufficientResourceValue;
        if (candidate.ElapsedMinutes + candidate.DurationMinutes > candidate.TimeLimitMinutes)
            return TimeLimitExceededValue;
        return null;
    }

    private static bool HasUnsupportedCausalAssumption(
        IReadOnlyDictionary<string, string> values)
    {
        if (!TryGetSemanticValue(values, CauseSelectionDimension, out var cause) ||
            string.Equals(cause, UndeterminedValue, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !HasSemanticValue(
            values,
            CausalAttributionStatusDimension,
            AttributionSupportedValue);
    }

    private static bool IsActivePlan(IReadOnlyDictionary<string, string> values) =>
        HasSemanticValue(values, PlanStatusDimension, PlanReadyValue) ||
        HasSemanticValue(values, PlanStatusDimension, PlanInProgressValue);

    private static bool HasSatisfiedStopCondition(IReadOnlyDictionary<string, string> values) =>
        TryGetSemanticValue(values, StopConditionDimension, out var stopCondition) &&
        TryGetSemanticValue(values, ObservedStopEvidenceDimension, out var observed) &&
        string.Equals(stopCondition, observed, StringComparison.OrdinalIgnoreCase);

    private static bool IsKnownSafetyConstraintStatus(string value) =>
        value is SafetySatisfiedValue or SafetyViolatedValue or ConstraintContradictionValue;

    private static bool TryGetBoundedPlanningInteger(
        IReadOnlyDictionary<string, string> values,
        string dimension,
        int minimum,
        int maximum,
        out int result)
    {
        result = 0;
        return TryGetSemanticValue(values, dimension, out var value) &&
               int.TryParse(
                   value,
                   NumberStyles.None,
                   CultureInfo.InvariantCulture,
                   out result) &&
               result >= minimum &&
               result <= maximum;
    }

    private static bool CanReplaceConstrainedPlanningState(
        ReasoningMode mode,
        string dimension)
    {
        if (mode == ReasoningMode.ConstrainedPlanningStep)
        {
            return dimension is CurrentPlanActionDimension or
                CurrentActionOrderDimension or
                PlanElapsedMinutesDimension or
                PlanStatusDimension or
                CandidatePlanActionDimension or
                ActionPrerequisiteDimension or
                CandidateActionOrderDimension or
                ActionDurationMinutesDimension or
                RequiredResourceUnitsDimension or
                SafetyConstraintStatusDimension;
        }
        if (mode == ReasoningMode.ConstrainedPlanningBlock ||
            mode == ReasoningMode.ConstrainedPlanningStop)
        {
            return dimension == PlanStatusDimension;
        }
        if (mode == ReasoningMode.ConstrainedPlanningEvidenceBranch)
        {
            return dimension is CandidatePlanActionDimension or
                ActionPrerequisiteDimension or
                CandidateActionOrderDimension or
                ActionDurationMinutesDimension or
                RequiredResourceUnitsDimension or
                SafetyConstraintStatusDimension;
        }
        return false;
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
        if (value == "reasoning.causal-diagnostic.plan" ||
            value.StartsWith("reasoning.causal-diagnostic.plan.", StringComparison.Ordinal))
            return ReasoningMode.CausalDiagnosticPlan;
        if (value == "reasoning.causal-diagnostic.conclude" ||
            value.StartsWith("reasoning.causal-diagnostic.conclude.", StringComparison.Ordinal))
            return ReasoningMode.CausalDiagnosticConclusion;
        if (value == "reasoning.causal-diagnostic.contradictory-evidence" ||
            value.StartsWith("reasoning.causal-diagnostic.contradictory-evidence.", StringComparison.Ordinal))
            return ReasoningMode.CausalDiagnosticContradictoryEvidence;
        if (value == "reasoning.causal-diagnostic.resource-limited" ||
            value.StartsWith("reasoning.causal-diagnostic.resource-limited.", StringComparison.Ordinal))
            return ReasoningMode.CausalDiagnosticResourceLimited;
        if (value == "reasoning.constrained-planning.step" ||
            value.StartsWith("reasoning.constrained-planning.step.", StringComparison.Ordinal))
            return ReasoningMode.ConstrainedPlanningStep;
        if (value == "reasoning.constrained-planning.block" ||
            value.StartsWith("reasoning.constrained-planning.block.", StringComparison.Ordinal))
            return ReasoningMode.ConstrainedPlanningBlock;
        if (value == "reasoning.constrained-planning.evidence-branch" ||
            value.StartsWith("reasoning.constrained-planning.evidence-branch.", StringComparison.Ordinal))
            return ReasoningMode.ConstrainedPlanningEvidenceBranch;
        if (value == "reasoning.constrained-planning.stop" ||
            value.StartsWith("reasoning.constrained-planning.stop.", StringComparison.Ordinal))
            return ReasoningMode.ConstrainedPlanningStop;
        return null;
    }

    private static bool IsEpistemicMode(ReasoningMode? mode) =>
        mode is ReasoningMode.ObservationalEquivalence or
            ReasoningMode.InsufficientEvidence;

    private static bool IsCausalDiagnosticMode(ReasoningMode? mode) =>
        mode is ReasoningMode.CausalDiagnosticPlan or
            ReasoningMode.CausalDiagnosticConclusion or
            ReasoningMode.CausalDiagnosticContradictoryEvidence or
            ReasoningMode.CausalDiagnosticResourceLimited;

    private static bool IsConstrainedPlanningMode(ReasoningMode? mode) =>
        mode is ReasoningMode.ConstrainedPlanningStep or
            ReasoningMode.ConstrainedPlanningBlock or
            ReasoningMode.ConstrainedPlanningEvidenceBranch or
            ReasoningMode.ConstrainedPlanningStop;

    private enum ReasoningMode
    {
        Forward,
        Bidirectional,
        Constraint,
        Deduction,
        ObservationalEquivalence,
        InsufficientEvidence,
        CausalDiagnosticPlan,
        CausalDiagnosticConclusion,
        CausalDiagnosticContradictoryEvidence,
        CausalDiagnosticResourceLimited,
        ConstrainedPlanningStep,
        ConstrainedPlanningBlock,
        ConstrainedPlanningEvidenceBranch,
        ConstrainedPlanningStop
    }

    private sealed record DirectionalRule(
        LegendGovernedReasoningRule Rule,
        IReadOnlyDictionary<string, string> SourceFrame,
        IReadOnlyDictionary<string, string> ResultFrame,
        IReadOnlySet<Guid> SourceSemanticFamilyIds,
        IReadOnlySet<Guid> ResultSemanticFamilyIds,
        IReadOnlyList<LegendGovernedReasoningFamilyConnection> FamilyConnections,
        bool Reversed,
        bool IsProofCarrying,
        ReasoningMode Mode);

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

    private sealed record PlanningCandidateState(
        string CurrentAction,
        string CandidateAction,
        string Prerequisite,
        int CurrentOrder,
        int CandidateOrder,
        int DurationMinutes,
        int TimeLimitMinutes,
        int ElapsedMinutes,
        int AvailableResources,
        int RequiredResources,
        string SafetyStatus);
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

internal enum LegendResearchEvidenceAssessmentState
{
    Conclusion,
    InsufficientEvidence,
    UnresolvedConflict
}

internal sealed record LegendResearchEvidenceAssessment(
    LegendResearchEvidenceAssessmentState State,
    IReadOnlyList<LegendConnectClaimEvidence> Claims,
    IReadOnlyList<LegendConnectContradictingEvidence> Contradictions,
    int IndependentSourceCount,
    int RequiredIndependentSourceCount,
    string ReasonCode);
