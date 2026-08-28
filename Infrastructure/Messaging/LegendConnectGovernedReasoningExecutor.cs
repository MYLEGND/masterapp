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
/// The suffix is opaque curriculum meaning, allowing new skill domains without
/// adding code or a topic router.
/// </summary>
internal static class LegendConnectGovernedReasoningExecutor
{
    internal const int MaximumDepth = 12;
    internal const int MaximumStates = 512;
    internal const int MaximumRules = 4096;

    internal static bool IsExecutableOperatorIdentity(string? identity) =>
        ResolveMode(identity) is not null;

    internal static LegendGovernedReasoningExecution Derive(
        IReadOnlyDictionary<string, string> initialValues,
        IReadOnlyList<LegendGovernedReasoningRule> rules)
    {
        if (initialValues.Count == 0 || rules.Count == 0)
            return LegendGovernedReasoningExecution.Empty;
        if (rules.Count > MaximumRules)
            return new(false, true, []);

        var normalizedRules = rules
            .Where(rule => ResolveMode(rule.OperatorIdentity) is not null)
            // A lower-standard rule remains usable while the curriculum is
            // growing, but it can never win a duplicate derived state over a
            // higher-standard rule.  The visited-state authority below keeps
            // the first proof, so evidence precedence must be deterministic
            // here rather than being left to database enumeration order.
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
            return new(true, false, []);

        var directional = new List<DirectionalRule>();
        foreach (var rule in normalizedRules)
        {
            var mode = ResolveMode(rule.OperatorIdentity);
            if (mode == ReasoningMode.Forward)
            {
                directional.Add(new DirectionalRule(rule, rule.SourceFrame, rule.ResultFrame, false));
            }
            else if (mode == ReasoningMode.Bidirectional)
            {
                directional.Add(new DirectionalRule(rule, rule.SourceFrame, rule.ResultFrame, false));
                directional.Add(new DirectionalRule(rule, rule.ResultFrame, rule.SourceFrame, true));
            }
        }

        var initial = Copy(initialValues);
        var visited = new HashSet<string>(StringComparer.Ordinal) { CanonicalState(initial) };
        var queue = new Queue<LegendGovernedReasoningProof>();
        queue.Enqueue(new(initial, [], 0, 0, int.MaxValue));
        var derived = new List<LegendGovernedReasoningProof>();

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current.Depth >= MaximumDepth)
                continue;

            foreach (var rule in directional)
            {
                if (!TryApply(rule.SourceFrame, rule.ResultFrame, current.Values, out var nextValues))
                    continue;
                if (ViolatesConstraint(nextValues, constraints))
                    continue;

                var identity = CanonicalState(nextValues);
                if (!visited.Add(identity))
                    continue;
                if (visited.Count > MaximumStates)
                    return new(false, true, []);

                var transitionIdentity = rule.Rule.TransitionSignature +
                    (rule.Reversed ? ":reverse" : string.Empty);
                var path = current.TransitionPath.Append(transitionIdentity).ToArray();
                var evidence = current.Depth == 0
                    ? rule.Rule.IndependentEvidenceCount
                    : Math.Min(current.EvidenceCount, rule.Rule.IndependentEvidenceCount);
                var evidenceStandard = current.Depth == 0
                    ? rule.Rule.EvidenceStandard
                    : Math.Min(current.EvidenceStandard, rule.Rule.EvidenceStandard);
                var proof = new LegendGovernedReasoningProof(
                    nextValues,
                    path,
                    current.Depth + 1,
                    evidence,
                    evidenceStandard);
                derived.Add(proof);
                queue.Enqueue(proof);
            }
        }

        return new(false, false, derived);
    }

    private static bool TryApply(
        IReadOnlyDictionary<string, string> sourceFrame,
        IReadOnlyDictionary<string, string> resultFrame,
        IReadOnlyDictionary<string, string> currentValues,
        out IReadOnlyDictionary<string, string> nextValues)
    {
        if (!TryBindFrame(sourceFrame, currentValues, null, out var bindings))
        {
            nextValues = currentValues;
            return false;
        }

        var next = Copy(currentValues);
        foreach (var item in resultFrame)
        {
            string value;
            if (IsVariable(item.Value))
            {
                if (!bindings.TryGetValue(item.Value, out value!) || string.IsNullOrWhiteSpace(value))
                {
                    nextValues = currentValues;
                    return false;
                }
            }
            else
            {
                value = item.Value;
            }
            next[item.Key] = value;
        }

        nextValues = next;
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
        return null;
    }

    private enum ReasoningMode
    {
        Forward,
        Bidirectional,
        Constraint
    }

    private sealed record DirectionalRule(
        LegendGovernedReasoningRule Rule,
        IReadOnlyDictionary<string, string> SourceFrame,
        IReadOnlyDictionary<string, string> ResultFrame,
        bool Reversed);
}

internal sealed record LegendGovernedReasoningRule(
    string TransitionSignature,
    string OperatorIdentity,
    IReadOnlyDictionary<string, string> SourceFrame,
    IReadOnlyDictionary<string, string> ResultFrame,
    int IndependentEvidenceCount,
    int EvidenceStandard = 2);

internal sealed record LegendGovernedReasoningProof(
    IReadOnlyDictionary<string, string> Values,
    IReadOnlyList<string> TransitionPath,
    int Depth,
    int EvidenceCount,
    int EvidenceStandard);

internal sealed record LegendGovernedReasoningExecution(
    bool InitialContradiction,
    bool BudgetExceeded,
    IReadOnlyList<LegendGovernedReasoningProof> DerivedStates)
{
    internal static readonly LegendGovernedReasoningExecution Empty = new(false, false, []);
}
